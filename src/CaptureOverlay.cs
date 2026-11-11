using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using DrawingRectangle = System.Drawing.Rectangle;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace FrameDock
{
    internal sealed class CaptureOverlay : Window
    {
        readonly Canvas canvas = new Canvas();
        readonly ShapeRectangle selection = new ShapeRectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, IsHitTestVisible = false };
        readonly System.Windows.Shapes.Path shade = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0)),
            IsHitTestVisible = false
        };
        readonly BitmapSource snapshot;
        readonly DrawingRectangle screen;
        Point start;
        readonly TaskCompletionSource<BitmapSource> completion =
            new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        BitmapSource result;
        Exception failure;
        bool finishing;
        bool dragging;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        CaptureOverlay(DrawingRectangle screen, BitmapSource snapshot)
        {
            this.screen = screen;
            this.snapshot = snapshot;
            Title = "PicShot · 区域截图";
            WindowStyle = WindowStyle.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Background = Brushes.Black;
            var root = new Grid();
            root.Children.Add(new Image { Source = snapshot, Stretch = Stretch.Fill, IsHitTestVisible = false });
            canvas.Cursor = Cursors.Cross;
            canvas.Background = Brushes.Transparent;
            root.Children.Add(canvas);
            Content = root;
            canvas.Children.Add(shade);
            canvas.Children.Add(selection);
            selection.Visibility = Visibility.Collapsed;
            SourceInitialized += delegate
            {
                // Screen bounds are physical pixels, whereas WPF window dimensions are DIPs.
                if (!SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1),
                    screen.Left, screen.Top, screen.Width, screen.Height, 0x0010))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            };
            canvas.SizeChanged += delegate { UpdateShade(); };
            MouseLeftButtonDown += BeginSelection;
            MouseMove += UpdateSelection;
            MouseLeftButtonUp += EndSelection;
            LostMouseCapture += delegate
            {
                if (dragging) CloseWith(null);
            };
            KeyDown += Cancel;
            MouseRightButtonUp += delegate { CloseWith(null); };
            Closed += delegate
            {
                if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult(result);
            };
        }

        public static Task<BitmapSource> ShowAsync()
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            // Capture before showing any selection UI; never capture our own mask or border.
            var snapshot = WindowsNative.Capture(screen);
            var overlay = new CaptureOverlay(screen, snapshot);
            try
            {
                overlay.Show();
                overlay.Activate();
            }
            catch
            {
                overlay.Close();
                throw;
            }
            return overlay.completion.Task;
        }

        void BeginSelection(object sender, MouseButtonEventArgs args)
        {
            start = ClampPoint(args.GetPosition(canvas));
            dragging = true;
            selection.Visibility = Visibility.Visible;
            Canvas.SetLeft(selection, start.X);
            Canvas.SetTop(selection, start.Y);
            selection.Width = 0;
            selection.Height = 0;
            UpdateShade();
            if (!CaptureMouse()) CloseWith(null);
        }

        void UpdateSelection(object sender, MouseEventArgs args)
        {
            if (!dragging) return;
            UpdateSelectionTo(ClampPoint(args.GetPosition(canvas)));
        }

        Point ClampPoint(Point point)
        {
            return new Point(Math.Max(0, Math.Min(canvas.ActualWidth, point.X)),
                Math.Max(0, Math.Min(canvas.ActualHeight, point.Y)));
        }

        void UpdateSelectionTo(Point point)
        {
            double left = Math.Min(start.X, point.X);
            double top = Math.Min(start.Y, point.Y);
            Canvas.SetLeft(selection, left);
            Canvas.SetTop(selection, top);
            selection.Width = Math.Abs(point.X - start.X);
            selection.Height = Math.Abs(point.Y - start.Y);
            UpdateShade();
        }

        void UpdateShade()
        {
            var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight)));
            if (selection.Visibility == Visibility.Visible && selection.Width > 0 && selection.Height > 0)
                geometry.Children.Add(new RectangleGeometry(new Rect(
                    Canvas.GetLeft(selection), Canvas.GetTop(selection), selection.Width, selection.Height)));
            shade.Data = geometry;
        }

        internal static Int32Rect SelectionPixels(Point start, Point end, Size viewport, int width, int height)
        {
            if (viewport.Width <= 0 || viewport.Height <= 0 || width <= 0 || height <= 0)
                return Int32Rect.Empty;
            double x1 = Math.Max(0, Math.Min(width, Math.Min(start.X, end.X) * width / viewport.Width));
            double y1 = Math.Max(0, Math.Min(height, Math.Min(start.Y, end.Y) * height / viewport.Height));
            double x2 = Math.Max(0, Math.Min(width, Math.Max(start.X, end.X) * width / viewport.Width));
            double y2 = Math.Max(0, Math.Min(height, Math.Max(start.Y, end.Y) * height / viewport.Height));
            int left = (int)Math.Floor(x1), top = (int)Math.Floor(y1);
            return new Int32Rect(left, top, (int)Math.Ceiling(x2) - left, (int)Math.Ceiling(y2) - top);
        }

        void EndSelection(object sender, MouseButtonEventArgs args)
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            var point = ClampPoint(args.GetPosition(canvas));
            var pixels = SelectionPixels(start, point, new Size(canvas.ActualWidth, canvas.ActualHeight),
                snapshot.PixelWidth, snapshot.PixelHeight);
            if (pixels.Width < 4 || pixels.Height < 4) { CloseWith(null); return; }
            try
            {
                // Detach selected pixels so a small capture does not retain the whole desktop.
                var result = new WriteableBitmap(new CroppedBitmap(snapshot, pixels));
                result.Freeze();
                CloseWith(result);
            }
            catch (Exception error)
            {
                failure = error;
                Close();
            }
        }

        void Cancel(object sender, KeyEventArgs args)
        {
            if (args != null && args.Key != Key.Escape) return;
            CloseWith(null);
        }

        void CloseWith(BitmapSource result)
        {
            if (finishing) return;
            finishing = true;
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            this.result = result;
            Close();
        }
    }
}
