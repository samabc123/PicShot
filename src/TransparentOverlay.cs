using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class TransparentOverlay
    {
        const double CardWidth = 120;
        const double CardHeight = 91;
        const double HangerWidth = 128;
        const double HangerStep = 132;
        const double CardTop = 17;
        const int WsPopup = unchecked((int)0x80000000);
        const int WsExTopmost = 0x00000008;
        const int WsExToolWindow = 0x00000080;
        const int WsExNoActivate = 0x08000000;
        const int WmNchitTest = 0x0084;
        const int HtTransparent = -1;
        const uint SwpNoActivate = 0x0010;
        const uint SwpShowWindow = 0x0040;
        const int SwShownoactivate = 4;
        const int SwHide = 0;

        static readonly IntPtr HwndTopmost = new IntPtr(-1);

        readonly OverlayCanvas canvas;
        readonly int left;
        readonly int top;
        readonly int width;
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord pressedItem;
        Point pressPoint;
        bool dragging;
        bool suppressClick;
        bool expanded;
        bool visible;
        HwndSource source;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr handle, int command);

        public Action CaptureRequested;
        public Action ImportRequested;
        public Action AddFolderRequested;
        public Action CollapseRequested;
        public Action<ImageRecord> CopyRequested;
        public Action<ImageRecord> PinRequested;
        public Action<ImageRecord> OpenRequested;
        public Action<ImageRecord> LocateRequested;
        public Action<ImageRecord> SaveRequested;
        public Action<ImageRecord> DeleteRequested;
        public Action<ImageRecord> DragRequested;

        public TransparentOverlay(int left, int top, int width, int height)
        {
            this.left = left;
            this.top = top;
            this.width = width;
            canvas = new OverlayCanvas(this)
            {
                Width = width,
                Height = height
            };

            canvas.MouseLeftButtonDown += OverlayMouseDown;
            canvas.MouseMove += OverlayMouseMove;
            canvas.MouseLeftButtonUp += OverlayMouseUp;
            canvas.MouseRightButtonUp += OverlayRightButtonUp;

            var parameters = new HwndSourceParameters("PicShotOverlay")
            {
                PositionX = left,
                PositionY = top,
                Width = width,
                Height = height,
                WindowStyle = WsPopup,
                ExtendedWindowStyle = WsExTopmost | WsExToolWindow | WsExNoActivate,
                UsesPerPixelOpacity = true
            };
            source = new HwndSource(parameters);
            source.RootVisual = canvas;
            source.AddHook(WindowMessageHook);
        }

        public void Show()
        {
            visible = true;
            SetWindowPos(
                source.Handle,
                HwndTopmost,
                left,
                top,
                width,
                expanded ? 132 : 6,
                SwpNoActivate | SwpShowWindow);
            ShowWindow(source.Handle, SwShownoactivate);
        }

        public void Hide()
        {
            visible = false;
            ShowWindow(source.Handle, SwHide);
        }

        public void Close()
        {
            visible = false;
            if (source != null)
            {
                source.RemoveHook(WindowMessageHook);
                source.Dispose();
                source = null;
            }
        }

        public void SetItems(IEnumerable<ImageRecord> records)
        {
            items = records == null ? new List<ImageRecord>() : records.ToList();
            canvas.InvalidateVisual();
        }

        public void SetExpanded(bool value)
        {
            expanded = value;
            canvas.Height = value ? 132 : 6;
            SetWindowPos(
                source.Handle,
                HwndTopmost,
                left,
                top,
                width,
                value ? 132 : 6,
                SwpNoActivate | (visible ? SwpShowWindow : 0));
            canvas.InvalidateVisual();
        }

        public bool IsInteractivePoint(DrawingPoint screenPoint)
        {
            if (!visible) return false;
            return IsInteractiveLocalPoint(new Point(screenPoint.X - left, screenPoint.Y - top));
        }

        IntPtr WindowMessageHook(
            IntPtr handle,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message == WmNchitTest)
            {
                int x = (short)(lParam.ToInt64() & 0xffff);
                int y = (short)((lParam.ToInt64() >> 16) & 0xffff);
                if (!IsInteractivePoint(new DrawingPoint(x, y)))
                {
                    handled = true;
                    return new IntPtr(HtTransparent);
                }
            }
            return IntPtr.Zero;
        }

        bool IsInteractiveLocalPoint(Point point)
        {
            if (!expanded)
                return point.Y >= 0 && point.Y < 6;

            if (point.Y >= 15 && point.Y < 16) return true;
            int handleLeft = (int)((width - 162) / 2);
            if (new Rect(handleLeft, 6, 162, 18).Contains(point)) return true;

            for (int index = 0; index < items.Count && index < 50; index++)
            {
                double cardLeft = 4 + index * HangerStep;
                if (new Rect(cardLeft, 0, HangerWidth, CardTop + CardHeight).Contains(point))
                    return true;
            }
            return false;
        }

        ImageRecord HitItem(Point point, out bool delete, out bool pin)
        {
            delete = false;
            pin = false;
            for (int index = 0; index < items.Count && index < 50; index++)
            {
                double cardLeft = 4 + index * HangerStep;
                var card = new Rect(cardLeft, CardTop, CardWidth, CardHeight);
                if (!card.Contains(point)) continue;
                delete = new Rect(cardLeft + 101, CardTop + 7, 18, 18).Contains(point);
                pin = new Rect(cardLeft + 92, CardTop + 56, 22, 17).Contains(point);
                return items[index];
            }
            return null;
        }

        void OverlayMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton != MouseButton.Left || !expanded) return;
            bool delete;
            bool pin;
            pressedItem = HitItem(args.GetPosition(canvas), out delete, out pin);
            pressPoint = args.GetPosition(canvas);
            dragging = false;
            suppressClick = false;
            if (pressedItem != null) canvas.CaptureMouse();
        }

        void OverlayMouseMove(object sender, MouseEventArgs args)
        {
            if (pressedItem == null ||
                args.LeftButton != MouseButtonState.Pressed ||
                dragging) return;

            Point current = args.GetPosition(canvas);
            if (Math.Abs(current.X - pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            dragging = true;
            suppressClick = true;
            canvas.ReleaseMouseCapture();
            if (DragRequested != null) DragRequested(pressedItem);
            pressedItem = null;
        }

        void OverlayMouseUp(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton != MouseButton.Left) return;
            if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
            pressedItem = null;
            dragging = false;
            if (suppressClick)
            {
                suppressClick = false;
                return;
            }

            bool delete;
            bool pin;
            ImageRecord item = HitItem(args.GetPosition(canvas), out delete, out pin);
            if (item == null) return;
            if (delete && DeleteRequested != null) DeleteRequested(item);
            else if (pin && PinRequested != null) PinRequested(item);
            else if (CopyRequested != null) CopyRequested(item);
        }

        void OverlayRightButtonUp(object sender, MouseButtonEventArgs args)
        {
            args.Handled = true;
            bool delete;
            bool pin;
            ImageRecord item = HitItem(args.GetPosition(canvas), out delete, out pin);
            Point local = args.GetPosition(canvas);
            Point screen = new Point(left + local.X, top + local.Y);
            if (item == null) ShowShelfMenu(screen);
            else ShowItemMenu(item, screen);
        }

        void ShowShelfMenu(Point screen)
        {
            var menu = new Forms.ContextMenuStrip();
            AddMenuItem(menu, "截图", CaptureRequested);
            AddMenuItem(menu, "导入图片", ImportRequested);
            AddMenuItem(menu, "添加监听目录", AddFolderRequested);
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem(menu, "收起截图绳", CollapseRequested);
            menu.Show((int)screen.X, (int)screen.Y);
        }

        void ShowItemMenu(ImageRecord item, Point screen)
        {
            var menu = new Forms.ContextMenuStrip();
            AddMenuItem(menu, "复制图片", delegate { if (CopyRequested != null) CopyRequested(item); });
            AddMenuItem(menu, "钉在桌面", delegate { if (PinRequested != null) PinRequested(item); });
            AddMenuItem(menu, "打开图片", delegate { if (OpenRequested != null) OpenRequested(item); });
            AddMenuItem(menu, "定位文件", delegate { if (LocateRequested != null) LocateRequested(item); });
            AddMenuItem(menu, "保存副本", delegate { if (SaveRequested != null) SaveRequested(item); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem(menu, "删除图片", delegate { if (DeleteRequested != null) DeleteRequested(item); });
            menu.Show((int)screen.X, (int)screen.Y);
        }

        static void AddMenuItem(Forms.ContextMenuStrip menu, string text, Action action)
        {
            var item = new Forms.ToolStripMenuItem(text);
            if (action != null) item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        sealed class OverlayCanvas : Canvas
        {
            readonly TransparentOverlay owner;

            public OverlayCanvas(TransparentOverlay owner)
            {
                this.owner = owner;
                SnapsToDevicePixels = true;
                IsHitTestVisible = true;
            }

            protected override HitTestResult HitTestCore(PointHitTestParameters parameters)
            {
                return new PointHitTestResult(this, parameters.HitPoint);
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                owner.Render(drawingContext);
            }
        }

        void Render(DrawingContext drawing)
        {
            if (!expanded) return;

            drawing.DrawLine(new Pen(Brush(144, 174, 181), 1), new Point(0, 15), new Point(width, 15));
            DrawHandle(drawing);
            for (int index = 0; index < items.Count && index < 50; index++)
                DrawCard(drawing, items[index], index);
        }

        void DrawHandle(DrawingContext drawing)
        {
            double leftPosition = (width - 162) / 2;
            drawing.DrawRectangle(
                Brush(245, 252, 250),
                new Pen(Brush(213, 231, 227), 1),
                new Rect(leftPosition, 6, 162, 18));
            DrawText(drawing, "···", new Point(leftPosition + 73, 5), 9, Brush(135, 151, 154));
        }

        void DrawCard(DrawingContext drawing, ImageRecord item, int index)
        {
            double cardLeft = 4 + index * HangerStep;
            double center = cardLeft + HangerWidth / 2;

            drawing.DrawRectangle(Brush(157, 128, 102), null, new Rect(center - 1, 0, 2, 12));
            drawing.DrawRoundedRectangle(
                Brush(214, 163, 107),
                new Pen(Brush(169, 121, 76), 1),
                new Rect(center - 8, 4, 17, 20),
                3,
                3);

            var card = new Rect(cardLeft, CardTop, CardWidth, CardHeight);
            drawing.DrawRectangle(Brush(243, 240, 232), new Pen(Brush(200, 192, 176), 1), card);

            var media = new Rect(cardLeft + 5, CardTop + 5, CardWidth - 10, 68);
            drawing.DrawRectangle(Brush(32, 39, 46), null, media);
            if (item.IsAvailable && item.Thumbnail != null)
                drawing.DrawImage(item.Thumbnail, FitRectangle(item.Thumbnail, media));
            else
                DrawText(drawing, "离线", new Point(media.X + 42, media.Y + 25), 8, Brush(216, 101, 101));

            var delete = new Rect(cardLeft + 101, CardTop + 7, 18, 18);
            drawing.DrawRectangle(Brush(215, 107, 102), null, delete);
            DrawText(drawing, "×", new Point(delete.X + 3, delete.Y - 1), 11, Brushes.White, true);

            var pin = new Rect(cardLeft + 92, CardTop + 56, 22, 17);
            drawing.DrawRectangle(Brush(220, 239, 232), null, pin);
            DrawText(drawing, "钉", new Point(pin.X + 5, pin.Y + 2), 8, Brush(44, 107, 88));

            string name = item.IsAvailable ? item.Name : "离线 · " + item.Name;
            DrawText(
                drawing,
                TrimName(name),
                new Point(cardLeft + 5, CardTop + 75),
                7,
                item.IsAvailable ? Brush(90, 81, 71) : Brush(180, 35, 24));
        }

        static Rect FitRectangle(BitmapSource source, Rect target)
        {
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return target;
            double scale = Math.Min(target.Width / source.PixelWidth, target.Height / source.PixelHeight);
            double imageWidth = Math.Max(1, source.PixelWidth * scale);
            double imageHeight = Math.Max(1, source.PixelHeight * scale);
            return new Rect(
                target.X + (target.Width - imageWidth) / 2,
                target.Y + (target.Height - imageHeight) / 2,
                imageWidth,
                imageHeight);
        }

        static string TrimName(string value)
        {
            if (value == null || value.Length <= 19) return value;
            return value.Substring(0, 18) + "...";
        }

        static Brush Brush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        static void DrawText(
            DrawingContext drawing,
            string value,
            Point location,
            double size,
            Brush brush,
            bool bold = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            var text = new FormattedText(
                value,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(
                    new FontFamily("Segoe UI"),
                    FontStyles.Normal,
                    bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStretches.Normal),
                size,
                brush,
                1.0);
            drawing.DrawText(text, location);
        }
    }
}
