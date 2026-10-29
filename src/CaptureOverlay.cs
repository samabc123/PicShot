using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        readonly ShapeRectangle selection = new ShapeRectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(48, 55, 170, 255)) };
        readonly DrawingRectangle screen;
        Point start;
        TaskCompletionSource<BitmapSource> completion;
        bool dragging;

        CaptureOverlay(DrawingRectangle screen)
        {
            this.screen = screen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Background = new SolidColorBrush(Color.FromArgb(96, 0, 0, 0));
            canvas.Cursor = Cursors.Cross;
            Content = canvas;
            canvas.Children.Add(selection);
            selection.Visibility = Visibility.Collapsed;
            MouseLeftButtonDown += BeginSelection;
            MouseMove += UpdateSelection;
            MouseLeftButtonUp += EndSelection;
            KeyDown += Cancel;
        }

        public static Task<BitmapSource> ShowAsync()
        {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
            var overlay = new CaptureOverlay(screen);
            overlay.completion = new TaskCompletionSource<BitmapSource>();
            overlay.Show();
            overlay.Activate();
            return overlay.completion.Task;
        }

        void BeginSelection(object sender, MouseButtonEventArgs args)
        {
            start = args.GetPosition(this);
            dragging = true;
            selection.Visibility = Visibility.Visible;
            Canvas.SetLeft(selection, start.X);
            Canvas.SetTop(selection, start.Y);
            selection.Width = 0;
            selection.Height = 0;
            CaptureMouse();
        }

        void UpdateSelection(object sender, MouseEventArgs args)
        {
            if (!dragging) return;
            var point = args.GetPosition(this);
            double left = Math.Min(start.X, point.X);
            double top = Math.Min(start.Y, point.Y);
            Canvas.SetLeft(selection, left);
            Canvas.SetTop(selection, top);
            selection.Width = Math.Abs(point.X - start.X);
            selection.Height = Math.Abs(point.Y - start.Y);
        }

        void EndSelection(object sender, MouseButtonEventArgs args)
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            var point = args.GetPosition(this);
            int left = (int)Math.Round(Math.Min(start.X, point.X));
            int top = (int)Math.Round(Math.Min(start.Y, point.Y));
            int width = (int)Math.Round(Math.Abs(point.X - start.X));
            int height = (int)Math.Round(Math.Abs(point.Y - start.Y));
            if (width < 4 || height < 4) { Cancel(null, null); return; }
            CloseWith(WindowsNative.Capture(new DrawingRectangle(screen.Left + left, screen.Top + top, width, height)));
        }

        void Cancel(object sender, KeyEventArgs args)
        {
            if (args != null && args.Key != Key.Escape) return;
            CloseWith(null);
        }

        void CloseWith(BitmapSource result)
        {
            if (completion != null && !completion.Task.IsCompleted) completion.SetResult(result);
            Close();
        }
    }
}
