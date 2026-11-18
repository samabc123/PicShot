using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;

namespace FrameDock
{
    internal sealed class TransparentOverlay
    {
        const double CardWidth = 134;
        const double CardHeight = 96;
        const double HangerStep = 148;
        const double CardDrop = 9;
        const double CardBottomPadding = 10;
        const double RopeTop = 22;
        const double NailWidth = 48;
        const double NailRopeX = 34;
        static readonly ImageSource Nail = SceneArtwork.Load("nail");
        static readonly ImageSource NailHover = SceneArtwork.Load("nail-hover");
        static readonly SceneArtwork.Mask BasketMask = new SceneArtwork.Mask(SceneArtwork.BasketIcon);
        static readonly Pen BasketHookHitPen = new Pen(Brushes.Black, 5);
        const double NaturalSag = 4.5;
        const double ItemSag = 3.0;
        const double MaxLoadSag = 24;
        const double RopeThickness = 3.6;
        const int CollapsedHeight = 6;
        const int ExpandedHeight = 132;
        const double RetractDurationMs = 240;
        const double PhotoSlideTimeConstantMs = 46;
        const double SlotHysteresis = 6;
        const double ExportClearance = 10;
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
        readonly DrawingGroup versionLabel = CreateVersionLabel(AppUpdates.CurrentVersion);
        readonly TranslateTransform slide = new TranslateTransform();
        readonly DispatcherTimer retractTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        readonly DispatcherTimer scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        readonly Stopwatch retractClock = new Stopwatch();
        readonly Stopwatch photoSlideClock = new Stopwatch();
        int left;
        int top;
        int width;
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord pressedItem;
        ImageRecord lastClickedItem;
        bool pressedDoubleClick;
        bool pressedDelete;
        bool pressedPin;
        ImageRecord hoveredItem;
        int archiveCount;
        ContextMenu activeMenu;
        MouseButtonEventHandler photoMenuOutsideDown;
        Point pressPoint;
        int reorderSource = -1;
        int reorderTarget = -1;
        int? photoMenuDismissalDown;
        double grabOffset;
        double[] photoCenters;
        ImageRecord foregroundPhoto;
        bool suppressClick;
        bool hoveredDelete;
        bool hoveredPin;
        bool hoveredArchive;
        bool hoveredNail;
        double scrollPosition;
        double scrollTarget;
        bool expanded;
        bool visible;
        bool closed;
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
        public Action MenuDismissed;
        public Action PhotoMenuOutsideClicked;
        public Action ExpandRequested;
        public Action<ImageRecord> CopyRequested;
        public Action<ImageRecord> PinRequested;
        public Action<ImageRecord> ArchiveRequested;
        public Action<ImageRecord> ResetPlacementRequested;
        public Action<ImageRecord> OpenRequested;
        public Action<ImageRecord> LocateRequested;
        public Action<ImageRecord> SaveRequested;
        public Action<ImageRecord> DeleteRequested;
        public Action<ImageRecord> DragRequested;
        public Action<ImageRecord[]> RopeOrderChanged;
        public Action OpenArchiveRequested;
        public Action CheckUpdatesRequested;
        public Action SettingsRequested;
        public Action ExitRequested;
        public bool IsMenuOpen { get { return activeMenu != null && activeMenu.IsOpen; } }
        public bool IsPointerPressed { get { return pressedItem != null; } }
        public bool IsReordering { get { return reorderSource >= 0; } }
        public int RopeCapacity
        {
            get
            {
                double available = Math.Max(0, width - NailWidth - 108);
                return available < CardWidth ? 0 :
                    1 + (int)Math.Floor((available - CardWidth) / HangerStep);
            }
        }

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
                if (!canvas.IsMouseCaptured) CancelPress();
                hoveredItem = null;
                hoveredDelete = false;
                hoveredPin = false;
                hoveredArchive = false;
                hoveredNail = false;
                canvas.Cursor = null;
                canvas.InvalidateVisual();
            };
            canvas.MouseLeftButtonUp += OverlayMouseUp;
            canvas.LostMouseCapture += delegate { CancelPress(); };
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
            if (closed) return;
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
            if (closed) return;
            lastClickedItem = null;
            ClearPress();
            ResetPhotoSlide();
            CloseMenu();
            visible = false;
            ShowWindow(source.Handle, SwHide);
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            ClearPress();
            ResetPhotoSlide();
            CloseMenu();
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
            if (closed) return;
            var next = records == null ? new List<ImageRecord>() : records.ToList();
            if (!items.SequenceEqual(next))
            {
                CancelPress();
                ResetPhotoSlide();
            }
            items = next;
            scrollTarget = Math.Min(scrollTarget, MaxScrollOffset());
            scrollPosition = Math.Min(scrollPosition, MaxScrollOffset());
            hoveredItem = null;
            canvas.InvalidateVisual();
        }

        internal void UpdateBounds(int x, int y, int newWidth)
        {
            if (closed || newWidth <= 0) return;
            ClearPress();
            ResetPhotoSlide();
            CloseMenu();
            left = x;
            top = y;
            width = newWidth;
            canvas.Width = newWidth;
            scrollTarget = Math.Min(scrollTarget, MaxScrollOffset());
            scrollPosition = Math.Min(scrollPosition, MaxScrollOffset());
            SetWindowPos(source.Handle, HwndTopmost, left, top, width,
                expanded ? ExpandedHeight : CollapsedHeight,
                SwpNoActivate | (visible ? SwpShowWindow : 0));
            canvas.InvalidateVisual();
        }

        public void SetArchiveCount(int count)
        {
            if (archiveCount == count) return;
            archiveCount = count;
            canvas.InvalidateVisual();
        }

        public void RevealItem(ImageRecord item)
        {
            int index = items.IndexOf(item);
            if (index < 0) return;
            CancelPress();
            ResetPhotoSlide();
            scrollTimer.Stop();
            scrollPosition = Math.Max(0, Math.Min(MaxScrollOffset(),
                CardOrigin() + index * HangerStep - NailWidth));
            scrollTarget = scrollPosition;
            canvas.InvalidateVisual();
        }

        public void SetExpanded(bool value)
        {
            if (closed) return;
            CancelRetract();
            if (!value)
            {
                ClearPress();
                ResetPhotoSlide();
            }
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
            if (message == 0x0010 || (message == 0x0112 && (wParam.ToInt64() & 0xfff0) == 0xf060))
            {
                handled = true;
                AppDiagnostics.Log("ignored-system-close; target=rope-overlay");
                return IntPtr.Zero;
            }
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

            if (NailHitRect.Contains(point) || HitsBasket(point)) return true;
            if (Math.Abs(point.Y - RopeY(point.X)) <= RopeThickness / 2 + 2) return true;
            if (point.X < NailWidth || point.X >= width - 88) return false;

            int index;
            bool pin;
            Rect card;
            return FindCard(point, out index, out card, out pin);
        }

        ImageRecord HitItem(Point point, out bool delete, out bool pin)
        {
            delete = false;
            pin = false;
            if (point.X < NailWidth || point.X >= width - 88 ||
                point.Y < 0 || point.Y >= ExpandedHeight ||
                NailHitRect.Contains(point) || BasketHitRect.Contains(point))
                return null;
            int index;
            Rect card;
            if (!FindCard(point, out index, out card, out pin)) return null;
            delete = new Rect(card.Right - 23, card.Top + 5, 18, 18).Contains(point);
            return items[index];
        }

        bool FindCard(Point point, out int index, out Rect card, out bool pin)
        {
            int foreground = foregroundPhoto == null ? -1 : items.IndexOf(foregroundPhoto);
            if (foreground >= 0 && HitsCard(point, foreground, out card, out pin))
            {
                index = foreground;
                return true;
            }
            int first = FirstVisibleIndex();
            for (index = EndVisibleIndex() - 1; index >= first; index--)
                if (index != foreground && HitsCard(point, index, out card, out pin)) return true;
            card = Rect.Empty;
            pin = false;
            return false;
        }

        bool HitsCard(Point point, int index, out Rect card, out bool pin)
        {
            double center = CardCenter(index);
            pin = false;
            card = Rect.Empty;
            if (Math.Abs(point.X - center) > CardWidth / 2) return false;
            double ropeY = RopeY(center);
            card = CardRect(items[index], index);
            pin = new Rect(center - 9, ropeY - 6, 18, 24).Contains(point);
            return pin || card.Contains(point);
        }

        void OverlayMouseDown(object sender, MouseButtonEventArgs args)
        {
            if (args.ChangedButton != MouseButton.Left || !expanded) return;
            if (photoMenuOutsideDown != null)
                photoMenuOutsideDown(sender, args);
            if (args.Timestamp == photoMenuDismissalDown)
            {
                args.Handled = true;
                return;
            }
            photoMenuDismissalDown = null;
            ClearPress();
            bool delete;
            bool pin;
            pressedItem = HitItem(args.GetPosition(canvas), out delete, out pin);
            pressedDelete = delete;
            pressedPin = pin;
            pressedDoubleClick = args.ClickCount == 2 && pressedItem != null &&
                pressedItem == lastClickedItem && !delete && !pin;
            if (pressedItem == null || delete || pin) lastClickedItem = null;
            pressPoint = args.GetPosition(canvas);
            suppressClick = false;
            if (pressedItem != null)
            {
                grabOffset = pressPoint.X - CardCenter(items.IndexOf(pressedItem));
                canvas.CaptureMouse();
            }
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
            if (IsReordering)
            {
                if (args.LeftButton != MouseButtonState.Pressed) CancelPress();
                else if (!pressedPin && IsOutsideForExport(position)) ExportPressedItem();
                else UpdateReorderPosition(position);
                return;
            }
            bool overArchive = BasketHitRect.Contains(position);
            bool overNail = NailHitRect.Contains(position);
            ImageRecord overItem = HitItem(position, out overDelete, out overPin);
            if (overItem != hoveredItem || overDelete != hoveredDelete ||
                overPin != hoveredPin || overArchive != hoveredArchive ||
                overNail != hoveredNail)
            {
                hoveredItem = overItem;
                hoveredDelete = overDelete;
                hoveredPin = overPin;
                hoveredArchive = overArchive;
                hoveredNail = overNail;
                canvas.Cursor = overItem != null && !overDelete && (overPin || items.Count > 1)
                    ? Cursors.SizeWE : null;
                canvas.InvalidateVisual();
            }
            if (pressedItem == null || args.LeftButton != MouseButtonState.Pressed) return;

            MovePressedItem(position);
        }

        void MovePressedItem(Point position)
        {
            bool horizontal = Math.Abs(position.X - pressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance;
            if (!horizontal && Math.Abs(position.Y - pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            suppressClick = true;
            lastClickedItem = null;
            pressedDoubleClick = false;
            if (pressedDelete) return;
            if (!pressedPin && IsOutsideForExport(position)) ExportPressedItem();
            else if (horizontal) BeginReorder(position);
        }

        static bool IsOutsideForExport(Point point)
        {
            return point.Y >= ExpandedHeight + ExportClearance;
        }

        void ExportPressedItem()
        {
            var item = pressedItem;
            ClearPress();
            if (DragRequested != null) DragRequested(item);
        }

        void BeginReorder(Point point)
        {
            scrollTimer.Stop();
            scrollTarget = scrollPosition;
            reorderSource = items.IndexOf(pressedItem);
            reorderTarget = reorderSource;
            foregroundPhoto = pressedItem;
            if (photoCenters == null)
                photoCenters = Enumerable.Range(0, items.Count).Select(SlotCenter).ToArray();
            hoveredItem = null;
            canvas.Cursor = Cursors.SizeWE;
            UpdateReorderPosition(point);
            if (!photoSlideClock.IsRunning)
            {
                photoSlideClock.Start();
                CompositionTarget.Rendering += OnPhotoSlideFrame;
            }
        }

        void UpdateReorderPosition(Point point)
        {
            double minCenter = Math.Min(NailWidth + CardWidth / 2, (NailWidth + width - 88) / 2);
            double maxCenter = Math.Max(minCenter, width - 88 - CardWidth / 2);
            double center = Math.Max(minCenter, Math.Min(maxCenter, point.X - grabOffset)) + scrollPosition;
            photoCenters[reorderSource] = center;
            if (Math.Abs(center - SlotCenter(reorderTarget)) > HangerStep / 2 + SlotHysteresis)
            {
                int slot = (int)Math.Floor((center - SlotCenter(0)) / HangerStep + 0.5);
                reorderTarget = Math.Max(0, Math.Min(items.Count - 1, slot));
            }
            canvas.InvalidateVisual();
        }

        void OnPhotoSlideFrame(object sender, EventArgs args)
        {
            if (photoCenters == null) return;
            double elapsed = photoSlideClock.Elapsed.TotalMilliseconds;
            photoSlideClock.Restart();
            double step = SystemParameters.ClientAreaAnimation
                ? 1 - Math.Exp(-elapsed / PhotoSlideTimeConstantMs) : 1;
            bool settling = false;
            bool repaint = false;
            for (int index = 0; index < photoCenters.Length; index++)
            {
                if (index == reorderSource) continue;
                int slot = index;
                if (reorderSource >= 0)
                {
                    if (index > reorderSource && index <= reorderTarget) slot--;
                    else if (index < reorderSource && index >= reorderTarget) slot++;
                }
                double target = SlotCenter(slot);
                double delta = target - photoCenters[index];
                if (delta == 0) continue;
                repaint = true;
                if (Math.Abs(delta) > 0.35 && step < 1)
                {
                    photoCenters[index] += delta * step;
                    settling = true;
                }
                else photoCenters[index] = target;
            }
            if (!IsReordering && !settling)
            {
                ResetPhotoSlide();
                repaint = true;
            }
            if (repaint) canvas.InvalidateVisual();
        }

        void ResetPhotoSlide()
        {
            if (!photoSlideClock.IsRunning) return;
            CompositionTarget.Rendering -= OnPhotoSlideFrame;
            photoSlideClock.Reset();
            photoCenters = null;
            foregroundPhoto = null;
        }

        void ClearPress()
        {
            bool repaint = IsReordering;
            reorderSource = -1;
            reorderTarget = -1;
            pressedItem = null;
            pressedDoubleClick = false;
            if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
            if (repaint)
            {
                canvas.Cursor = null;
                canvas.InvalidateVisual();
            }
        }

        void CancelPress()
        {
            if (pressedItem == null) return;
            suppressClick = true;
            lastClickedItem = null;
            ClearPress();
        }

        bool IsRopeDropPoint(Point point)
        {
            return point.X >= NailWidth && point.X < width - 88 &&
                point.Y >= 0 && point.Y < ExpandedHeight;
        }

        void CompleteReorder(int from, int to)
        {
            if (to < 0 || from == to) return;
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
            // Keep the current rendered positions when the list is committed.
            double center = photoCenters[from];
            if (from < to) Array.Copy(photoCenters, from + 1, photoCenters, from, to - from);
            else Array.Copy(photoCenters, to, photoCenters, to + 1, from - to);
            photoCenters[to] = center;
            hoveredItem = null;
            if (RopeOrderChanged != null) RopeOrderChanged(items.ToArray());
            canvas.InvalidateVisual();
        }

        void OverlayMouseWheel(object sender, MouseWheelEventArgs args)
        {
            lastClickedItem = null;
            if (IsReordering)
            {
                args.Handled = true;
                return;
            }
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
            Point local = args.GetPosition(canvas);
            int from = reorderSource;
            ImageRecord downItem = pressedItem;
            bool doubleClick = pressedDoubleClick;
            int to = -1;
            if (from >= 0 && IsRopeDropPoint(local))
            {
                UpdateReorderPosition(local);
                to = reorderTarget;
            }
            ClearPress();
            if (from >= 0)
            {
                suppressClick = false;
                CompleteReorder(from, to);
                return;
            }
            if (suppressClick)
            {
                suppressClick = false;
                return;
            }
            if (!expanded) return;

            if (NailHitRect.Contains(local)) return;
            if (HitsBasket(local))
            {
                if (OpenArchiveRequested != null) OpenArchiveRequested();
                return;
            }
            bool delete;
            bool pin;
            ImageRecord item = HitItem(local, out delete, out pin);
            if (item == null || item != downItem || delete != pressedDelete || pin != pressedPin) return;
            if (delete && DeleteRequested != null) DeleteRequested(item);
            else if (pin && PinRequested != null) PinRequested(item);
            else if (!delete && !pin)
            {
                lastClickedItem = doubleClick ? null : item;
                if (doubleClick)
                {
                    if (OpenRequested != null) OpenRequested(item);
                }
                else if (CopyRequested != null) CopyRequested(item);
            }
        }

        void OverlayRightButtonUp(object sender, MouseButtonEventArgs args)
        {
            lastClickedItem = null;
            args.Handled = true;
            if (IsReordering) return;
            bool delete;
            bool pin;
            ImageRecord item = HitItem(args.GetPosition(canvas), out delete, out pin);
            Point local = args.GetPosition(canvas);
            Point screen = new Point(left + local.X, top + local.Y);
            if (NailHitRect.Contains(local))
            {
                ShowShelfMenu(screen);
                return;
            }
            if (BasketHitRect.Contains(local)) return;
            if (item != null) ShowItemMenu(item, screen);
        }

        void ShowShelfMenu(Point screen)
        {
            var menu = new ContextMenu();
            AddMenuItem(menu, "截图", CaptureRequested);
            AddMenuItem(menu, "导入图片", ImportRequested);
            AddMenuItem(menu, "添加监听目录", AddFolderRequested);
            AddMenuItem(menu, "设置", SettingsRequested);
            AddMenuItem(menu, "打开图片收纳", OpenArchiveRequested);
            AddMenuItem(menu, "检查更新", CheckUpdatesRequested);
            var clipboardItem = new MenuItem
            {
                Header = "收集剪贴板图片",
                IsCheckable = true,
                IsChecked = ClipboardCollectionEnabled != null && ClipboardCollectionEnabled()
            };
            clipboardItem.Click += delegate
            {
                bool enabled = clipboardItem.IsChecked;
                QueueMenuAction(delegate
                {
                    if (SetClipboardCollectionRequested != null)
                        SetClipboardCollectionRequested(enabled);
                });
            };
            menu.Items.Add(clipboardItem);
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "收起截图绳", CollapseRequested);
            AddMenuItem(menu, "退出 PicShot", ExitRequested);
            ShowMenu(menu, screen, false);
        }

        void ShowItemMenu(ImageRecord item, Point screen)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = item.Name, IsEnabled = false });
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "复制图片", delegate { if (CopyRequested != null) CopyRequested(item); });
            AddMenuItem(menu, item.RopePinned == true ? "取消固定并收回袜夹" : "固定在绳上",
                delegate { if (PinRequested != null) PinRequested(item); });
            if (item.RopePinned != true)
                AddMenuItem(menu, "收回袜夹",
                    delegate { if (ArchiveRequested != null) ArchiveRequested(item); });
            if (item.RopePinned.HasValue)
                AddMenuItem(menu, "恢复自动分类",
                    delegate { if (ResetPlacementRequested != null) ResetPlacementRequested(item); });
            AddMenuItem(menu, "打开图片", delegate { if (OpenRequested != null) OpenRequested(item); });
            AddMenuItem(menu, "定位文件", delegate { if (LocateRequested != null) LocateRequested(item); });
            AddMenuItem(menu, "保存副本", delegate { if (SaveRequested != null) SaveRequested(item); });
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "删除图片", delegate { if (DeleteRequested != null) DeleteRequested(item); });
            ShowMenu(menu, screen, true);
        }

        void ShowMenu(ContextMenu menu, Point screen, bool photoMenu)
        {
            if (closed) return;
            CloseMenu();
            CancelPress();
            activeMenu = menu;
            menu.PlacementTarget = canvas;
            menu.Placement = PlacementMode.RelativePoint;
            Point local = canvas.PointFromScreen(screen);
            menu.HorizontalOffset = local.X;
            menu.VerticalOffset = local.Y;
            bool outsideClick = false;
            MouseButtonEventHandler onOutside = delegate(object sender, MouseButtonEventArgs args)
            {
                if (!outsideClick)
                    canvas.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                    {
                        if (activeMenu == menu) menu.IsOpen = false;
                    }));
                outsideClick = true;
                if (args.ChangedButton != MouseButton.Left) return;
                // WPF can dispatch the same down event to the photo underneath the popup.
                photoMenuDismissalDown = args.Timestamp;
                CancelPress();
                suppressClick = true;
            };
            if (photoMenu)
            {
                photoMenuOutsideDown = onOutside;
                menu.AddHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, onOutside, true);
            }
            menu.Closed += delegate
            {
                if (photoMenu)
                    menu.RemoveHandler(Mouse.PreviewMouseDownOutsideCapturedElementEvent, onOutside);
                bool dismissed = activeMenu == menu;
                if (dismissed)
                {
                    activeMenu = null;
                    photoMenuOutsideDown = null;
                }
                menu.PlacementTarget = null;
                if (!dismissed || closed) return;
                if (outsideClick && PhotoMenuOutsideClicked != null) PhotoMenuOutsideClicked();
                else if (MenuDismissed != null) MenuDismissed();
            };
            menu.IsOpen = true;
        }

        void CloseMenu()
        {
            if (activeMenu == null) return;
            ContextMenu menu = activeMenu;
            activeMenu = null;
            photoMenuOutsideDown = null;
            menu.IsOpen = false;
            menu.PlacementTarget = null;
        }

        void QueueMenuAction(Action action)
        {
            CloseMenu();
            if (closed || canvas.Dispatcher.HasShutdownStarted || canvas.Dispatcher.HasShutdownFinished) return;
            canvas.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (closed) return;
                action();
            }));
        }

        void AddMenuItem(ContextMenu menu, string text, Action action)
        {
            var item = new MenuItem { Header = text };
            if (action != null) item.Click += delegate { QueueMenuAction(action); };
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
            DrawNail(drawing);
            drawing.PushTransform(new TranslateTransform(4, 39));
            drawing.DrawDrawing(versionLabel);
            drawing.Pop();
            drawing.PushClip(new RectangleGeometry(new Rect(NailWidth, 0,
                Math.Max(0, width - NailWidth - 88), ExpandedHeight)));
            int foreground = foregroundPhoto == null ? -1 : items.IndexOf(foregroundPhoto);
            for (int index = FirstVisibleIndex(); index < EndVisibleIndex(); index++)
            {
                double center = CardCenter(index);
                if (index != foreground && center - CardWidth / 2 < width - 88 &&
                    center + CardWidth / 2 > NailWidth)
                    DrawCard(drawing, items[index], index);
            }
            if (foreground >= 0) DrawCard(drawing, items[foreground], foreground);
            drawing.Pop();
            DrawBasket(drawing);
        }

        Rect NailHitRect
        {
            get { return new Rect(0, 0, 42, 38); }
        }

        static DrawingGroup CreateVersionLabel(string version)
        {
            var text = new FormattedText(
                "v" + version,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brush(72, 92, 111),
                1.0);
            var label = new DrawingGroup();
            using (var drawing = label.Open())
            {
                var glyphs = text.BuildGeometry(new Point(0, 0));
                drawing.DrawGeometry(null,
                    new Pen(Brush(248, 251, 253, 225), 1.5)
                    {
                        LineJoin = PenLineJoin.Round
                    }, glyphs);
                drawing.DrawGeometry(Brush(72, 92, 111), null, glyphs);
            }
            double scale = Math.Min(1, (NailWidth - 8) / Math.Max(1, label.Bounds.Width));
            label.Transform = new ScaleTransform(scale, scale);
            label.Freeze();
            return label;
        }

        Rect BasketRect
        {
            get { return new Rect(width - 84, RopeY(width - 33) + 12, 72, 82); }
        }

        Rect BasketHitRect
        {
            get
            {
                Rect area = BasketRect;
                return new Rect(area.X, area.Y - 20, area.Width, area.Height + 20);
            }
        }

        bool HitsBasket(Point point)
        {
            Rect area = BasketRect;
            if (!BasketHitRect.Contains(point)) return false;
            var local = new Point(point.X - area.X, point.Y - area.Y);
            return BasketMask.Contains(local, area.Width, area.Height) ||
                (archiveCount > 0 && new Rect(44, 21, 26, 16).Contains(local)) ||
                BasketHook(area).StrokeContains(BasketHookHitPen, point);
        }

        double MaxScrollOffset()
        {
            double span = (items.Count - 1) * HangerStep + CardWidth;
            return Math.Max(0, span - (width - NailWidth - 108));
        }

        double SlotCenter(int index)
        {
            return CardOrigin() + index * HangerStep + CardWidth / 2;
        }

        double CardCenter(int index)
        {
            return (photoCenters == null ? SlotCenter(index) : photoCenters[index]) - scrollPosition;
        }

        double CardOrigin()
        {
            double span = (items.Count - 1) * HangerStep + CardWidth;
            double available = Math.Max(0, width - NailWidth - 108);
            return NailWidth + Math.Max(0, (available - span) / 2);
        }

        int FirstVisibleIndex()
        {
            if (photoCenters != null) return 0;
            return Math.Max(0, (int)Math.Floor((scrollPosition - CardOrigin() - CardWidth) / HangerStep) + 1);
        }

        int EndVisibleIndex()
        {
            if (photoCenters != null) return items.Count;
            return Math.Min(items.Count,
                Math.Max(0, (int)Math.Ceiling((scrollPosition + width - 88 - CardOrigin()) / HangerStep)));
        }

        void DrawNail(DrawingContext drawing)
        {
            double ropeY = RopeY(NailRopeX);
            drawing.DrawImage(hoveredNail ? NailHover : Nail, new Rect(0, ropeY - 21, 42, 42));

            var tie = new StreamGeometry();
            using (var shape = tie.Open())
            {
                shape.BeginFigure(new Point(12, ropeY - 5), false, false);
                shape.BezierTo(new Point(7, ropeY - 4), new Point(8, ropeY - 1),
                    new Point(14, ropeY + 1), true, false);
                shape.BezierTo(new Point(21, ropeY + 3), new Point(27, ropeY),
                    new Point(NailRopeX, ropeY), true, false);
            }
            tie.Freeze();
            drawing.DrawGeometry(null, new Pen(Brush(143, 157, 170), RopeThickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            }, tie);
            drawing.PushTransform(new TranslateTransform(0, -0.8));
            drawing.DrawGeometry(null, new Pen(Brush(223, 232, 238, 175), 0.7), tie);
            drawing.Pop();
        }

        void DrawBasket(DrawingContext drawing)
        {
            Rect area = BasketRect;
            drawing.DrawGeometry(null, new Pen(Brush(139, 162, 180), 1.8)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
            }, BasketHook(area));
            if (hoveredArchive) drawing.PushOpacity(0.88);
            drawing.DrawImage(SceneArtwork.BasketIcon, area);
            if (hoveredArchive) drawing.Pop();
            if (archiveCount > 0)
            {
                double countX = area.X + 57;
                double countY = area.Y + 29;
                drawing.DrawRoundedRectangle(Brush(242, 248, 252),
                    new Pen(Brush(145, 167, 184), 0.8),
                    new Rect(countX - 13, countY - 8, 26, 16), 4, 4);
                var count = new FormattedText(
                    archiveCount > 99 ? "99+" : archiveCount.ToString(),
                    CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                        FontWeights.SemiBold, FontStretches.Normal),
                    10.5, Brush(63, 87, 107), 1.0);
                drawing.DrawText(count, new Point(countX - count.Width / 2, countY - count.Height / 2));
            }
        }

        Geometry BasketHook(Rect area)
        {
            double clipX = area.X + 21;
            double ropeY = RopeY(clipX);
            var hook = new StreamGeometry();
            using (var shape = hook.Open())
            {
                shape.BeginFigure(new Point(clipX + 3, ropeY + 2), false, false);
                shape.BezierTo(new Point(clipX + 5, ropeY - 7), new Point(clipX - 6, ropeY - 7),
                    new Point(clipX - 5, ropeY + 1), true, false);
                shape.BezierTo(new Point(clipX - 7, area.Y + 12), new Point(clipX - 9, area.Y + 21),
                    new Point(clipX - 12, area.Y + 33), true, false);
            }
            return hook;
        }

        Rect CardRect(ImageRecord item, int index)
        {
            double cardWidth = CardWidth;
            double cardHeight = CardHeight;
            double center = CardCenter(index);
            double ropeY = RopeY(center);
            double maxCardHeight = Math.Max(38,
                ExpandedHeight - ropeY - CardDrop - CardBottomPadding);
            BitmapSource thumbnail = item.Thumbnail;
            if (thumbnail != null &&
                thumbnail.PixelWidth > 0 && thumbnail.PixelHeight > 0)
            {
                double scale = Math.Min((CardWidth - 10) / thumbnail.PixelWidth,
                    (Math.Min(CardHeight, maxCardHeight) - 10) / thumbnail.PixelHeight);
                cardWidth = Math.Max(82, thumbnail.PixelWidth * scale + 10);
                cardHeight = Math.Min(maxCardHeight,
                    Math.Max(38, thumbnail.PixelHeight * scale + 10));
            }
            else
                cardHeight = Math.Min(cardHeight, maxCardHeight);
            return new Rect(center - cardWidth / 2,
                ropeY + CardDrop, cardWidth, cardHeight);
        }

        double RopeY(double x)
        {
            double span = Math.Max(1, width - NailRopeX);
            double t = Math.Max(0, Math.Min(1, (x - NailRopeX) / span));
            if (t <= 0 || t >= 1) return RopeTop;

            double naturalSag = 4 * NaturalSag * t * (1 - t);
            double loadShape = 0;
            for (int index = 0; index < items.Count; index++)
            {
                double center = CardCenter(index);
                double hangerT = Math.Max(0, Math.Min(1, (center - NailRopeX) / span));
                loadShape += PointLoadShape(t, hangerT);
            }

            double loadSag = Math.Min(MaxLoadSag, ItemSag * 4 * loadShape);
            return RopeTop + naturalSag + loadSag;
        }

        static double PointLoadShape(double position, double loadPosition)
        {
            if (position <= loadPosition)
                return position * (1 - loadPosition);
            return loadPosition * (1 - position);
        }

        Geometry RopePath(double offset)
        {
            var path = new StreamGeometry();
            using (StreamGeometryContext context = path.Open())
            {
                context.BeginFigure(new Point(NailRopeX, RopeY(NailRopeX) + offset), false, false);
                var points = new List<Point>();
                for (int x = (int)NailRopeX + 16; x < width; x += 16)
                    points.Add(new Point(x, RopeY(x) + offset));
                points.Add(new Point(width, RopeY(width) + offset));
                context.PolyLineTo(points, true, false);
            }
            path.Freeze();
            return path;
        }

        void DrawCard(DrawingContext drawing, ImageRecord item, int index)
        {
            double center = CardCenter(index);
            double ropeY = RopeY(center);
            var card = CardRect(item, index);
            bool moving = IsReordering && item == pressedItem;
            drawing.DrawRoundedRectangle(Brush(30, 41, 56, 30), null,
                new Rect(card.X + 1.5, card.Y + 3.5, card.Width, card.Height), 8, 8);
            drawing.DrawRoundedRectangle(Brush(255, 255, 255, 247),
                new Pen(moving ? Brush(0, 95, 184) :
                    item == hoveredItem ? Brush(159, 186, 215) : Brush(218, 225, 233), moving ? 2 : 1),
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
                item.RopePinned == true ? Brush(209, 226, 238) : clipFinish, clipPen,
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
