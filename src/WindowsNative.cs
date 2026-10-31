using System;
using System.Collections.Generic;
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

        [DllImport("user32.dll")]
        static extern bool SetWindowRgn(IntPtr handle, IntPtr region, bool redraw);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

        const int RgnOr = 2;

        public static uint ClipboardSequence { get { return GetClipboardSequenceNumber(); } }

        public static BitmapSource Capture(Rectangle bounds)
        {
            using (var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
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

        public static void SetWindowRegion(Window window, IEnumerable<Rectangle> rectangles)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if (source == null) return;

            IntPtr combined = CreateRectRgn(0, 0, 0, 0);
            if (combined == IntPtr.Zero) return;

            try
            {
                foreach (var rectangle in rectangles)
                {
                    if (rectangle.Width <= 0 || rectangle.Height <= 0) continue;
                    IntPtr current = CreateRectRgn(
                        rectangle.Left,
                        rectangle.Top,
                        rectangle.Right,
                        rectangle.Bottom);
                    if (current == IntPtr.Zero) continue;

                    try { CombineRgn(combined, combined, current, RgnOr); }
                    finally { DeleteObject(current); }
                }

                if (!SetWindowRgn(source.Handle, combined, true))
                    DeleteObject(combined);
                else
                    combined = IntPtr.Zero;
            }
            finally
            {
                if (combined != IntPtr.Zero) DeleteObject(combined);
            }
        }

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);
    }
}
