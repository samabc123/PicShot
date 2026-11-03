using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class TransparentOverlay
    {
        const double CardWidth = 134;
        const double CardHeight = 96;
        const double HangerStep = 148;
        const double CardTop = 21;
        const double RopeTop = 12;
        const double RopeSag = 5.5;
        const double RopeThickness = 3.6;
        const int CollapsedHeight = 6;
        const int ExpandedHeight = 132;
        const double RetractDurationMs = 240;
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
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        readonly DispatcherTimer scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        readonly Stopwatch retractClock = new Stopwatch();
        readonly int left;
        readonly int top;
        readonly int width;
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord pressedItem;
        ImageRecord hoveredItem;
        int archiveCount;
        Point pressPoint;
        bool dragging;
        bool suppressClick;
        bool hoveredDelete;
        bool hoveredPin;
        bool hoveredArchive;
        double scrollPosition;
        double scrollTarget;
        bool expanded;
        bool visible;
        Action retractCompleted;
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
        public Func<bool> ClipboardCollectionEnabled;
        public Action<bool> SetClipboardCollectionRequested;
        public Action CollapseRequested;
        public Action ExpandRequested;
        public Action<ImageRecord> CopyRequested;
        public Action<ImageRecord> PinRequested;
        public Action<ImageRecord> OpenRequested;
        public Action<ImageRecord> LocateRequested;
        public Action<ImageRecord> SaveRequested;
        public Action<ImageRecord> DeleteRequested;
        public Action<ImageRecord> DragRequested;
        public Action OpenArchiveRequested;
        public Action ExitRequested;

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
            canvas.RenderTransform = slide;
            retractTimer.Tick += OnRetractTick;
            scrollTimer.Tick += delegate
            {
                scrollPosition += (scrollTarget - scrollPosition) * 0.28;
                if (Math.Abs(scrollTarget - scrollPosition) < 0.5)
                {
                    scrollPosition = scrollTarget;
                    scrollTimer.Stop();
                }
                canvas.InvalidateVisual();
            };

            canvas.MouseLeftButtonDown += OverlayMouseDown;
            canvas.MouseEnter += delegate
            {
                if (retractTimer.IsEnabled && ExpandRequested != null) ExpandRequested();
            };
            canvas.MouseMove += OverlayMouseMove;
            canvas.MouseWheel += OverlayMouseWheel;
            canvas.MouseLeave += delegate
            {
                hoveredItem = null;
                hoveredDelete = false;
                hoveredPin = false;
                hoveredArchive = false;
                canvas.InvalidateVisual();
            };
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
                expanded ? ExpandedHeight : CollapsedHeight,
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
            CancelRetract();
            scrollTimer.Stop();
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
            scrollTarget = Math.Min(scrollTarget, MaxScrollOffset());
            scrollPosition = Math.Min(scrollPosition, MaxScrollOffset());
            hoveredItem = null;
            canvas.InvalidateVisual();
        }

        public void SetArchiveCount(int count)
        {
            if (archiveCount == count) return;
            archiveCount = count;
            canvas.InvalidateVisual();
        }

        public void SetExpanded(bool value)
        {
            CancelRetract();
            expanded = value;
            canvas.Height = value ? ExpandedHeight : CollapsedHeight;
            SetWindowPos(
                source.Handle,
                HwndTopmost,
                left,
                top,
                width,
                value ? ExpandedHeight : CollapsedHeight,
                SwpNoActivate | (visible ? SwpShowWindow : 0));
            canvas.InvalidateVisual();
        }

        public void Retract(Action completed)
        {
            CancelRetract();
            retractCompleted = completed;
            retractClock.Restart();
            retractTimer.Start();
        }

        public void CancelRetract()
        {
            retractTimer.Stop();
            retractClock.Reset();
            retractCompleted = null;
            slide.Y = 0;
        }

        void OnRetractTick(object sender, EventArgs args)
        {
            double progress = Math.Min(1, retractClock.Elapsed.TotalMilliseconds / RetractDurationMs);
            double eased = progress * progress * (3 - 2 * progress);
            slide.Y = -(ExpandedHeight - CollapsedHeight) * eased;
            if (progress < 1) return;

            retractTimer.Stop();
            retractClock.Stop();
            Action completed = retractCompleted;
            retractCompleted = null;
            if (completed != null) completed();
        }

        public bool IsInteractivePoint(DrawingPoint screenPoint)
        {
            if (!visible ||
                screenPoint.X < left || screenPoint.X >= left + width ||
                screenPoint.Y < top || screenPoint.Y >= top + (expanded ? ExpandedHeight : CollapsedHeight))
                return false;
            return IsInteractiveLocalPoint(new Point(screenPoint.X - left, screenPoint.Y - top - slide.Y));
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

            if (SockHitRect.Contains(point)) return true;
            if (Math.Abs(point.Y - RopeY(point.X)) <= RopeThickness / 2 + 2) return true;
            if (point.X >= width - 88) return false;

            for (int index = 0; index < items.Count && index < 50; index++)
            {
                double cardLeft = CardLeft(index);
                if (point.X < cardLeft || point.X >= cardLeft + CardWidth) continue;
                double center = cardLeft + CardWidth / 2;
                double ropeY = RopeY(center);
                if (CardRect(items[index], index).Contains(point) ||
                    new Rect(center - 9, ropeY - 6, 18, 24).Contains(point))
                    return true;
            }
            return false;
        }

        ImageRecord HitItem(Point point, out bool delete, out bool pin)
        {
            delete = false;
            pin = false;
            if (point.X >= width - 88) return null;
            for (int index = 0; index < items.Count && index < 50; index++)
            {
                double cardLeft = CardLeft(index);
                if (point.X < cardLeft || point.X >= cardLeft + CardWidth) continue;
                double center = cardLeft + CardWidth / 2;
                double ropeY = RopeY(center);
                var card = CardRect(items[index], index);
                var clip = new Rect(center - 9, ropeY - 6, 18, 24);
                if (!card.Contains(point) && !clip.Contains(point)) continue;
                delete = new Rect(card.Right - 23, card.Top + 5, 18, 18).Contains(point);
                pin = clip.Contains(point);
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
            if (retractTimer.IsEnabled && ExpandRequested != null)
            {
                ExpandRequested();
                return;
            }
            bool overDelete;
            bool overPin;
            Point position = args.GetPosition(canvas);
            bool overArchive = SockHitRect.Contains(position);
            ImageRecord overItem = HitItem(position, out overDelete, out overPin);
            if (overItem != hoveredItem || overDelete != hoveredDelete ||
                overPin != hoveredPin || overArchive != hoveredArchive)
            {
                hoveredItem = overItem;
                hoveredDelete = overDelete;
                hoveredPin = overPin;
                hoveredArchive = overArchive;
                canvas.InvalidateVisual();
            }
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

        void OverlayMouseWheel(object sender, MouseWheelEventArgs args)
        {
            if (!expanded || MaxScrollOffset() == 0) return;
            scrollTarget = Math.Max(0, Math.Min(MaxScrollOffset(),
                scrollTarget - Math.Sign(args.Delta) * HangerStep * 2));
            hoveredItem = null;
            scrollTimer.Start();
            args.Handled = true;
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

            if (SockHitRect.Contains(args.GetPosition(canvas)))
            {
                if (OpenArchiveRequested != null) OpenArchiveRequested();
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
            if (SockHitRect.Contains(local))
            {
                ShowShelfMenu(screen);
                return;
            }
            if (item == null) ShowShelfMenu(screen);
            else ShowItemMenu(item, screen);
        }

        void ShowShelfMenu(Point screen)
        {
            var menu = new Forms.ContextMenuStrip();
            AddMenuItem(menu, "截图", CaptureRequested);
            AddMenuItem(menu, "导入图片", ImportRequested);
            AddMenuItem(menu, "添加监听目录", AddFolderRequested);
            AddMenuItem(menu, "打开图片收纳", OpenArchiveRequested);
            var clipboardItem = new Forms.ToolStripMenuItem("收集剪贴板图片")
            {
                CheckOnClick = true,
                Checked = ClipboardCollectionEnabled != null && ClipboardCollectionEnabled()
            };
            clipboardItem.CheckedChanged += delegate
            {
                if (SetClipboardCollectionRequested != null)
                    SetClipboardCollectionRequested(clipboardItem.Checked);
            };
            menu.Items.Add(clipboardItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddMenuItem(menu, "收起截图绳", CollapseRequested);
            AddMenuItem(menu, "退出 PicShot", ExitRequested);
            menu.Show((int)screen.X, (int)screen.Y);
        }

        void ShowItemMenu(ImageRecord item, Point screen)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add(new Forms.ToolStripMenuItem(item.Name) { Enabled = false });
            menu.Items.Add(new Forms.ToolStripSeparator());
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

            Geometry ropePath = RopePath(0);
            var rope = new Pen(Brush(143, 157, 170), RopeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawing.DrawGeometry(null, rope, ropePath);
            drawing.PushClip(ropePath.GetWidenedPathGeometry(rope));
            var darkFiber = new Pen(Brush(72, 92, 111, 105), 0.85);
            var lightFiber = new Pen(Brush(242, 248, 252, 175), 0.8);
            for (double x = 2; x < width + 5; x += 7)
            {
                double y = RopeY(x);
                drawing.DrawLine(darkFiber, new Point(x - 2.5, y - 2),
                    new Point(x + 1.2, y + 2));
                drawing.DrawLine(lightFiber, new Point(x + 1.2, y + 2),
                    new Point(x + 4.9, y - 2));
            }
            drawing.Pop();
            drawing.DrawGeometry(null, new Pen(Brush(223, 232, 238, 175), 0.65), RopePath(-0.95));
            drawing.DrawGeometry(null, new Pen(Brush(94, 110, 124, 85), 0.45), RopePath(1.35));
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, width - 88), ExpandedHeight)));
            for (int index = 0; index < items.Count && index < 50; index++)
            {
                double cardLeft = CardLeft(index);
                if (cardLeft < width - 88 && cardLeft + CardWidth > 0)
                    DrawCard(drawing, items[index], index);
            }
            drawing.Pop();
            DrawSock(drawing);
        }

        Rect SockRect
        {
            get { return new Rect(width - 84, RopeY(width - 42) + 12, 72, 97); }
        }

        Rect SockHitRect
        {
            get
            {
                Rect area = SockRect;
                return new Rect(area.X, area.Y - 20, area.Width, area.Height + 20);
            }
        }

        double MaxScrollOffset()
        {
            int count = Math.Min(items.Count, 50);
            double span = (count - 1) * HangerStep + CardWidth;
            return Math.Max(0, span - (width - 108));
        }

        double CardLeft(int index)
        {
            int count = Math.Min(items.Count, 50);
            double span = (count - 1) * HangerStep + CardWidth;
            return Math.Max(16, (width - 92 - span) / 2) + index * HangerStep - scrollPosition;
        }

        void DrawSock(DrawingContext drawing)
        {
            Rect area = SockRect;
            double x = area.X + 37;
            double y = area.Y;
            var body = new StreamGeometry();
            using (var shape = body.Open())
            {
                shape.BeginFigure(new Point(x - 17, y + 13), true, true);
                shape.LineTo(new Point(x + 15, y + 13), true, false);
                shape.LineTo(new Point(x + 14, y + 57), true, false);
                shape.BezierTo(new Point(x + 16, y + 66), new Point(x + 12, y + 72),
                    new Point(x + 2, y + 75), true, false);
                shape.LineTo(new Point(x - 24, y + 84), true, false);
                shape.BezierTo(new Point(x - 34, y + 87), new Point(x - 39, y + 80),
                    new Point(x - 36, y + 71), true, false);
                shape.BezierTo(new Point(x - 33, y + 62), new Point(x - 25, y + 60),
                    new Point(x - 19, y + 54), true, false);
                shape.LineTo(new Point(x - 17, y + 13), true, false);
            }
            body.Freeze();
            drawing.PushTransform(new TranslateTransform(1.5, 2.5));
            drawing.DrawGeometry(Brush(35, 49, 64, 31), null, body);
            drawing.Pop();
            drawing.DrawGeometry(hoveredArchive ? Brush(209, 226, 238) : Brush(225, 234, 241),
                new Pen(Brush(121, 144, 161), 1), body);
            drawing.DrawRoundedRectangle(Brush(244, 248, 250),
                new Pen(Brush(151, 169, 181), 0.9),
                new Rect(x - 19, y + 6, 36, 17), 4, 4);
            drawing.DrawLine(new Pen(Brush(161, 180, 193), 0.8),
                new Point(x - 15, y + 17), new Point(x + 13, y + 17));
            drawing.DrawRoundedRectangle(Brush(137, 151, 165), null,
                new Rect(x - 2, RopeY(x) - 5, 7, 24), 3, 3);
            drawing.DrawRoundedRectangle(Brush(234, 241, 246),
                new Pen(Brush(146, 162, 177), 0.7),
                new Rect(x - 5, RopeY(x) - 6, 7, 24), 3, 3);
            if (archiveCount > 0)
                DrawText(drawing, archiveCount > 99 ? "99+" : archiveCount.ToString(),
                    new Point(x - 12, y + 37), 12, Brush(64, 87, 107), true);
        }

        Rect CardRect(ImageRecord item, int index)
        {
            double cardWidth = CardWidth;
            double cardHeight = CardHeight;
            BitmapSource thumbnail = item.Thumbnail;
            if (thumbnail != null &&
                thumbnail.PixelWidth > 0 && thumbnail.PixelHeight > 0)
            {
                double scale = Math.Min((CardWidth - 10) / thumbnail.PixelWidth,
                    (CardHeight - 10) / thumbnail.PixelHeight);
                cardWidth = Math.Max(82, thumbnail.PixelWidth * scale + 10);
                cardHeight = Math.Max(38, thumbnail.PixelHeight * scale + 10);
            }
            double center = CardLeft(index) + CardWidth / 2;
            return new Rect(center - cardWidth / 2,
                CardTop + RopeY(center) - RopeTop, cardWidth, cardHeight);
        }

        double RopeY(double x)
        {
            double t = Math.Max(0, Math.Min(1, x / Math.Max(1, width)));
            double taper = Math.Sin(Math.PI * t);
            double bend = 3.75 * Math.Sin(3.4 * Math.PI * t + 0.4)
                + 1.8 * Math.Sin(8.6 * Math.PI * t + 1.2);
            return RopeTop + 4 * RopeSag * t * (1 - t) + taper * bend;
        }

        Geometry RopePath(double offset)
        {
            var path = new StreamGeometry();
            using (StreamGeometryContext context = path.Open())
            {
                context.BeginFigure(new Point(0, RopeY(0) + offset), false, false);
                var points = new List<Point>();
                for (int x = 16; x < width; x += 16)
                    points.Add(new Point(x, RopeY(x) + offset));
                points.Add(new Point(width, RopeY(width) + offset));
                context.PolyLineTo(points, true, false);
            }
            path.Freeze();
            return path;
        }

        void DrawCard(DrawingContext drawing, ImageRecord item, int index)
        {
            double center = CardLeft(index) + CardWidth / 2;
            double ropeY = RopeY(center);
            var card = CardRect(item, index);
            drawing.DrawRoundedRectangle(Brush(30, 41, 56, 30), null,
                new Rect(card.X + 1.5, card.Y + 3.5, card.Width, card.Height), 8, 8);
            drawing.DrawRoundedRectangle(Brush(255, 255, 255, 247),
                new Pen(item == hoveredItem ? Brush(159, 186, 215) : Brush(218, 225, 233), 1),
                card, 8, 8);

            var media = new Rect(card.X + 5, card.Y + 5, card.Width - 10, card.Height - 10);
            drawing.DrawRoundedRectangle(Brush(240, 243, 247), null, media, 5, 5);
            if (item.IsAvailable && item.Thumbnail != null)
            {
                drawing.PushClip(new RectangleGeometry(media, 5, 5));
                drawing.DrawImage(item.Thumbnail, FitRectangle(item.Thumbnail, media));
                drawing.Pop();
            }
            else
                DrawText(drawing, "离线", new Point(media.X + media.Width / 2 - 10,
                    media.Y + media.Height / 2 - 7), 12, Brush(177, 70, 69));

            var delete = new Rect(card.Right - 23, card.Top + 5, 18, 18);
            drawing.DrawRoundedRectangle(item == hoveredItem && hoveredDelete
                    ? Brush(255, 244, 244, 252) : Brush(253, 254, 255, 233),
                null, delete, 6, 6);
            var closePen = new Pen(item == hoveredItem && hoveredDelete
                ? Brush(186, 58, 55) : Brush(70, 83, 95), 1.6)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawing.DrawLine(closePen, new Point(delete.X + 6, delete.Y + 6),
                new Point(delete.X + 12, delete.Y + 12));
            drawing.DrawLine(closePen, new Point(delete.X + 12, delete.Y + 6),
                new Point(delete.X + 6, delete.Y + 12));

            var clipPen = new Pen(Brush(135, 149, 164), 0.7);
            var clipFinish = new LinearGradientBrush(
                Color.FromRgb(247, 249, 251), Color.FromRgb(183, 198, 213), 20);
            clipFinish.Freeze();
            drawing.DrawRoundedRectangle(Brush(146, 160, 175), null,
                new Rect(center - 2.5, ropeY - 5, 8, 24), 3, 3);
            drawing.DrawRoundedRectangle(
                clipFinish, clipPen,
                new Rect(center - 5, ropeY - 6, 8, 24), 3, 3);
            drawing.DrawEllipse(
                hoveredItem == item && hoveredPin ? Brush(70, 128, 189) : Brush(142, 155, 168),
                new Pen(Brush(239, 245, 249), 0.6),
                new Point(center + 2.5, ropeY + 7), 1.7, 1.7);
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

        static Brush Brush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        static Brush Brush(byte red, byte green, byte blue, byte alpha)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
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
