using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FrameDock
{
    internal static class WindowsNative
    {
        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        static extern uint RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr handle, int id);

        public static uint ClipboardSequence { get { return GetClipboardSequenceNumber(); } }

        public static BitmapSource Capture(Rectangle bounds)
        {
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                var handle = bitmap.GetHbitmap();
                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally { DeleteObject(handle); }
            }
        }

        public static void SavePng(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
        }

        public static bool RegisterHotKey(Window window, int id, uint modifiers, uint key)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            return source != null && RegisterHotKey(source.Handle, id, modifiers, key) != 0;
        }

        public static void UnregisterHotKey(Window window, int id)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source != null) UnregisterHotKey(source.Handle, id);
        }

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);
    }
}
