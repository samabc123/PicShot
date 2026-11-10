using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class MainWindow : Window
    {
        const double CollapsedHeight = 6;
        const double ExpandedHeight = 132;
        readonly StateStore store;
        readonly ImageLibrary library;
        readonly ImagePlacement placement;
        readonly Border surface = new Border();
        readonly TransparentOverlay overlay;
        readonly ArchiveWindow archive;
        readonly StackPanel cards = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Border hitBar = new Border();
        readonly Border rope = new Border();
        readonly Button handle = new Button();
        readonly TextBlock hint = new TextBlock();
        readonly TextBlock notice = new TextBlock();
        readonly DispatcherTimer collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        readonly DispatcherTimer hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly DispatcherTimer updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        readonly AppUpdates updates = new AppUpdates();
        readonly TextBlock count = new TextBlock();
        ImageRecord dragItem;
        Point dragStart;
        bool dragging;
        bool expanded;
        bool retracting;
        bool updateCheckBusy;
        bool updateFeedbackRequested;
        UpdateWindow updateWindow;
        uint clipboardSequence;
        bool clipboardBusy;
        bool specialFolderPromptShown;
        ImageRecord pendingRopeReveal;

        public MainWindow(StateStore store, ImageLibrary library, ImagePlacement placement)
        {
            this.store = store;
            this.library = library;
            this.placement = placement;
            Title = "PicShot · 截图绳";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            Left = area.Left;
            Top = area.Top;
            Width = 1;
            Height = 1;
            Opacity = 0;

            BuildSurface();
            overlay = new TransparentOverlay(area.Left, area.Top, area.Width, (int)CollapsedHeight);
            overlay.CaptureRequested = CaptureRegion;
            overlay.ImportRequested = ImportFiles;
            overlay.AddFolderRequested = AddFolder;
            overlay.ClipboardCollectionEnabled = delegate { return store.State.CollectClipboard; };
            overlay.SetClipboardCollectionRequested = SetClipboardCollection;
            overlay.CollapseRequested = Collapse;
            overlay.ExpandRequested = Expand;
            overlay.CopyRequested = Copy;
            overlay.PinRequested = ToggleRopePin;
            overlay.ArchiveRequested = ArchiveImage;
            overlay.ResetPlacementRequested = ResetPlacement;
            overlay.OpenRequested = Open;
            overlay.LocateRequested = Locate;
            overlay.SaveRequested = Save;
            overlay.DeleteRequested = Delete;
            overlay.DragRequested = StartDrag;
            overlay.OpenArchiveRequested = ToggleArchive;
            overlay.CheckUpdatesRequested = delegate { CheckForUpdates(true); };
            overlay.SettingsRequested = ShowSpecialFolderSettings;
            overlay.ExitRequested = Close;
            archive = new ArchiveWindow(Copy, ToggleRopePin, ResetPlacement, Open, Locate, Save, Delete, CreateDataObject);
            overlay.Show();
            library.Changed += RefreshCards;
            library.FolderImageDiscovered += OnFolderImageDiscovered;
            clipboardSequence = WindowsNative.ClipboardSequence;
            Loaded += delegate
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowInitialSpecialFolderSettings));
            };
            collapseTimer.Tick += delegate
            {
                collapseTimer.Stop();
                if (!IsCursorInShelf() && !dragging) Collapse();
            };
            hoverTimer.Tick += delegate
            {
                var cursor = Forms.Cursor.Position;

                if (retracting)
                {
                    if (IsCursorAtTop(cursor) || overlay.IsInteractivePoint(cursor)) Expand();
                }
                else if (!expanded && IsCursorAtTop(cursor))
                    Expand();
                else if (expanded)
                {
                    if (IsCursorInShelf(cursor))
                        collapseTimer.Stop();
                    else
                        ScheduleCollapse();
                }
            };
            refreshTimer.Tick += delegate
            {
                library.RefreshAll();
                ReadClipboard();
            };
            updateTimer.Tick += delegate
            {
                updateTimer.Stop();
                CheckForUpdates(false);
            };
            hoverTimer.Start();
            refreshTimer.Start();
            updateTimer.Start();
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                string error;
                if (library.TryFlush(out error)) return;
                if (MessageBox.Show(
                    "图片路径记录尚未保存，本次新增的路径在退出后可能丢失（原图不会删除）。\n" +
                    error + "\n\n仍要退出吗？",
                    "PicShot 尚未保存",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                    args.Cancel = true;
            };
            Closed += delegate
            {
                collapseTimer.Stop();
                hoverTimer.Stop();
                refreshTimer.Stop();
                updateTimer.Stop();
                archive.CloseForShutdown();
                overlay.Close();
                updates.Dispose();
                library.Changed -= RefreshCards;
                library.FolderImageDiscovered -= OnFolderImageDiscovered;
                library.Dispose();
            };
            RefreshCards();
        }

        void BuildSurface()
        {
            surface.Background = Brushes.Transparent;
            surface.BorderBrush = Brushes.Transparent;
            surface.BorderThickness = new Thickness(0);
            surface.CornerRadius = new CornerRadius(0);
            surface.Padding = new Thickness(0);
            surface.ClipToBounds = true;
            surface.Width = Forms.Screen.PrimaryScreen.WorkingArea.Width;
            surface.Height = CollapsedHeight;
            surface.MouseLeave += delegate { ScheduleCollapse(); };

            var root = new Grid();
            hitBar.Height = CollapsedHeight;
            hitBar.Background = Brushes.Transparent;
            hitBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            hitBar.VerticalAlignment = VerticalAlignment.Top;
            hitBar.MouseEnter += delegate { Expand(); };
            hitBar.MouseMove += delegate { Expand(); };
            hitBar.MouseLeave += delegate { ScheduleCollapse(); };
            root.Children.Add(hitBar);

            rope.Height = 1;
            rope.Margin = new Thickness(0, 15, 0, 0);
            rope.Background = Brush("#90AEB5");
            rope.Opacity = 1;
            rope.VerticalAlignment = VerticalAlignment.Top;
            root.Children.Add(rope);

            handle.Width = 162;
            handle.Height = 18;
            handle.Content = "···";
            handle.FontSize = 10;
            handle.Foreground = Brush("#87979A");
            handle.Background = Brush("#F5FCFA");
            handle.BorderBrush = Brush("#D5E7E3");
            handle.BorderThickness = new Thickness(1);
            handle.Padding = new Thickness(0);
            handle.HorizontalAlignment = HorizontalAlignment.Center;
            handle.VerticalAlignment = VerticalAlignment.Top;
            handle.Margin = new Thickness(0, 6, 0, 0);
            handle.Cursor = Cursors.Hand;
            handle.ToolTip = "截图绳菜单";
            handle.Template = FlatButtonTemplate(9);
            handle.Click += delegate { ShowShelfMenu(handle); };
            handle.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                ShowShelfMenu(handle);
            };
            root.Children.Add(handle);

            cards.VerticalAlignment = VerticalAlignment.Top;
            cards.HorizontalAlignment = HorizontalAlignment.Left;
            cards.Margin = new Thickness(0, 0, 0, 0);
            root.Children.Add(cards);

            hint.Text = "近七天的截图和下载图片会挂在这里";
            hint.FontSize = 11;
            hint.Foreground = Brush("#71818A");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Top;
            hint.Margin = new Thickness(0, 42, 0, 0);
            root.Children.Add(hint);

            notice.Foreground = Brush("#356E61");
            notice.FontSize = 10;
            notice.HorizontalAlignment = HorizontalAlignment.Center;
            notice.VerticalAlignment = VerticalAlignment.Top;
            notice.Margin = new Thickness(0, 24, 0, 0);
            notice.Visibility = Visibility.Collapsed;
            root.Children.Add(notice);

            surface.Child = root;
            Content = new Border { Background = Brushes.Transparent };
        }

        void RefreshCards()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                cards.Children.Clear();
                var available = library.Items.Where(item => item.IsAvailable).ToArray();
                DateTime now = DateTime.UtcNow;
                var recent = available.Where(item => placement.IsRecent(item, now))
                    .OrderByDescending(item => item.RopePinned == true)
                    .ThenByDescending(item => item.IsTransient ? DateTime.MaxValue :
                        item.AddedUtc > item.LastWriteUtc ? item.AddedUtc : item.LastWriteUtc)
                    .ThenByDescending(item => item.AddedUtc).ToArray();
                var archived = available.Where(item => !placement.IsRecent(item, now)).ToArray();
                count.Text = available.Length + " 张";
                hint.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                cards.Visibility = recent.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                overlay.SetItems(recent);
                if (pendingRopeReveal != null)
                {
                    overlay.RevealItem(pendingRopeReveal);
                    pendingRopeReveal = null;
                }
                overlay.SetArchiveCount(archived.Length);
                archive.SetItems(archived);
            }));
        }

        void ToggleArchive()
        {
            if (archive.IsVisible)
                archive.Hide();
            else
            {
                Expand();
                archive.Show();
                archive.Activate();
            }
        }

        async void CheckForUpdates(bool manual)
        {
            if (updateWindow != null && updateWindow.IsVisible)
            {
                if (manual) updateWindow.Activate();
                return;
            }
            if (manual) updateFeedbackRequested = true;
            if (updateCheckBusy)
                return;
            updateCheckBusy = true;
            try
            {
                UpdateRelease release = await updates.CheckAsync(CancellationToken.None);
                if (release == null)
                {
                    if (updateFeedbackRequested)
                        MessageBox.Show(
                            "当前已经是最新版本。",
                            "PicShot 更新",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    return;
                }

                if (updateWindow != null && updateWindow.IsVisible)
                {
                    updateWindow.Activate();
                    return;
                }
                updateWindow = new UpdateWindow(release, updates, InstallUpdate)
                {
                    Owner = this
                };
                updateWindow.Closed += delegate { updateWindow = null; };
                updateWindow.Show();
                updateWindow.Activate();
            }
            catch (Exception error)
            {
                if (updateFeedbackRequested)
                    MessageBox.Show(
                        "检查更新失败：" + error.Message,
                        "PicShot 更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
            finally
            {
                updateCheckBusy = false;
                updateFeedbackRequested = false;
            }
        }

        bool InstallUpdate(string work)
        {
            string error;
            if (!library.TryFlush(out error))
            {
                MessageBox.Show(
                    "图片路径记录尚未保存，暂时不能更新。" + Environment.NewLine + error,
                    "PicShot 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            AppUpdates.Install(work);
            Close();
            return true;
        }

        FrameworkElement CreateCard(ImageRecord item, int index)
        {
            var hanger = new Grid
            {
                Width = 128,
                Height = 121,
                Margin = new Thickness(2, 0, 2, 0),
                RenderTransformOrigin = new Point(0.5, 0),
                ToolTip = item.IsTransient ? "临时截图" : item.SourcePath
            };
            var stringLine = new Border
            {
                Width = 2,
                Height = 12,
                Background = Brush("#9D8066"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0)
            };
            hanger.Children.Add(stringLine);

            var peg = new Border
            {
                Width = 17,
                Height = 20,
                Background = Brush("#D6A36B"),
                BorderBrush = Brush("#A9794C"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            };
            hanger.Children.Add(peg);

            var card = new Border
            {
                Width = 120,
                Height = 91,
                Margin = new Thickness(0, 17, 0, 0),
                Padding = new Thickness(4),
                Background = Brush("#F3F0E8"),
                BorderBrush = Brush("#C8C0B0"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Cursor = Cursors.Hand
            };
            var grid = new Grid();
            var media = new Border
            {
                Background = Brush("#20272E"),
                ClipToBounds = true
            };
            var mediaGrid = new Grid();
            var image = new Image
            {
                Source = item.IsAvailable ? item.Thumbnail : null,
                Stretch = Stretch.Uniform,
                Opacity = item.IsAvailable ? 1 : 0.35
            };
            mediaGrid.Children.Add(image);
            if (!item.IsAvailable)
            {
                mediaGrid.Children.Add(new TextBlock
                {
                    Text = "离线",
                    FontSize = 11,
                    Foreground = Brush("#D86565"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var pin = new Button
            {
                Content = "钉",
                Width = 22,
                Height = 17,
                Padding = new Thickness(0),
                FontSize = 10,
                Background = Brush("#DCEFE8"),
                Foreground = Brush("#2C6B58"),
                BorderThickness = new Thickness(0),
                Template = FlatButtonTemplate(3),
                ToolTip = item.RopePinned == true ? "取消固定并收回袜夹" : "固定在绳上",
                IsEnabled = item.IsAvailable,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2)
            };
            pin.Click += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                ToggleRopePin(item);
            };
            mediaGrid.Children.Add(pin);

            var delete = new Button
            {
                Content = "×",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Background = Brush("#D76B66"),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Template = FlatButtonTemplate(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                ToolTip = "删除图片"
            };
            delete.Click += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                Delete(item);
            };
            mediaGrid.Children.Add(delete);
            media.Child = mediaGrid;
            grid.Children.Add(media);

            var name = new TextBlock
            {
                Text = item.IsAvailable ? item.Name : "离线 · " + item.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 8,
                Foreground = item.IsAvailable ? Brush("#5A5147") : Brush("#B42318"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 1, 1, 0)
            };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
            Grid.SetRow(media, 0);
            Grid.SetRow(name, 1);
            grid.Children.Add(name);
            card.Child = grid;
            hanger.Children.Add(card);
            hanger.MouseEnter += delegate
            {
                card.BorderBrush = Brush("#89AFA5");
            };
            hanger.MouseLeave += delegate
            {
                card.BorderBrush = Brush("#C8C0B0");
            };

            hanger.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ChangedButton != MouseButton.Left) return;
                dragItem = item;
                dragStart = args.GetPosition(hanger);
                dragging = false;
                hanger.CaptureMouse();
            };
            hanger.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (dragItem != item || args.LeftButton != MouseButtonState.Pressed || dragging) return;
                Point current = args.GetPosition(hanger);
                if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true;
                hanger.ReleaseMouseCapture();
                try { DragDrop.DoDragDrop(hanger, CreateDataObject(item), DragDropEffects.Copy); }
                catch { }
                finally { dragging = false; dragItem = null; }
            };
            hanger.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (dragItem != item) return;
                hanger.ReleaseMouseCapture();
                dragItem = null;
                if (!dragging) Copy(item);
                dragging = false;
            };
            hanger.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                ShowContextMenu(hanger, item);
            };
            return hanger;
        }

        ContextMenu ShowShelfMenu(FrameworkElement target)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("截图", CaptureRegion));
            menu.Items.Add(MenuItem("导入图片", ImportFiles));
            menu.Items.Add(MenuItem("添加监听目录", AddFolder));
            menu.Items.Add(MenuItem("设置", ShowSpecialFolderSettings));
            menu.Items.Add(MenuItem("检查更新", delegate { CheckForUpdates(true); }));
            menu.Items.Add(new Separator());

            var clipboardMenu = new MenuItem
            {
                Header = "收集剪贴板图片",
                IsCheckable = true,
                IsChecked = store.State.CollectClipboard
            };
            clipboardMenu.Click += delegate { SetClipboardCollection(clipboardMenu.IsChecked); };
            menu.Items.Add(clipboardMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("收起截图绳", Collapse));

            target.ContextMenu = menu;
            menu.IsOpen = true;
            return menu;
        }

        ContextMenu ShowContextMenu(FrameworkElement target, ImageRecord item)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("复制图片", delegate { Copy(item); }));
            menu.Items.Add(MenuItem(item.RopePinned == true ? "取消固定并收回袜夹" : "固定在绳上",
                delegate { ToggleRopePin(item); }));
            if (item.RopePinned != true)
                menu.Items.Add(MenuItem("收回袜夹", delegate { ArchiveImage(item); }));
            if (item.RopePinned.HasValue)
                menu.Items.Add(MenuItem("恢复自动分类", delegate { ResetPlacement(item); }));
            menu.Items.Add(MenuItem("打开图片", delegate { Open(item); }));
            menu.Items.Add(MenuItem("定位文件", delegate { Locate(item); }));
            menu.Items.Add(MenuItem("保存副本", delegate { Save(item); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("删除图片", delegate { Delete(item); }));
            target.ContextMenu = menu;
            menu.IsOpen = true;
            return menu;
        }

        MenuItem MenuItem(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += delegate { action(); };
            return item;
        }

        DataObject CreateDataObject(ImageRecord item)
        {
            var data = new DataObject();
            if (item.IsTransient)
            {
                data.SetImage(item.Thumbnail);
                return data;
            }
            using (var stream = File.OpenRead(item.EffectivePath))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                data.SetImage(image);
            }
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.EffectivePath });
            return data;
        }

        void Copy(ImageRecord item)
        {
            try
            {
                Clipboard.SetDataObject(CreateDataObject(item), true);
                clipboardSequence = WindowsNative.ClipboardSequence;
                ShowNotice("已复制：" + item.Name);
            }
            catch (Exception error) { ShowError(error); }
        }

        void ToggleRopePin(ImageRecord item)
        {
            bool pin = item.RopePinned != true;
            if (pin)
            {
                pendingRopeReveal = item;
                archive.Hide();
            }
            library.SetRopePlacement(item, pin);
            Expand();
        }

        void ArchiveImage(ImageRecord item)
        {
            library.SetRopePlacement(item, false);
        }

        void ResetPlacement(ImageRecord item)
        {
            library.SetRopePlacement(item, null);
        }

        void Open(ImageRecord item)
        {
            if (item.IsTransient)
            {
                ShowError(new InvalidOperationException("这张截图只保存在当前截图栏中，请使用“保存副本”主动保存。"));
                return;
            }
            try { Process.Start(new ProcessStartInfo(item.EffectivePath) { UseShellExecute = true }); }
            catch (Exception error) { ShowError(error); }
        }

        void Locate(ImageRecord item)
        {
            if (item.IsTransient)
            {
                ShowError(new InvalidOperationException("这张截图只保存在当前截图栏中，当前没有可定位的文件。"));
                return;
            }
            try { Process.Start("explorer.exe", "/select,\"" + item.EffectivePath + "\""); }
            catch (Exception error) { ShowError(error); }
        }

        void Save(ImageRecord item)
        {
            var picker = new SaveFileDialog
            {
                Title = "保存图片副本",
                FileName = item.Name,
                Filter = "图片文件|*" + Path.GetExtension(item.Name),
                OverwritePrompt = true
            };
            if (picker.ShowDialog() == true)
            {
                try
                {
                    if (item.IsTransient)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(item.Thumbnail));
                        using (var stream = File.Create(picker.FileName)) encoder.Save(stream);
                    }
                    else File.Copy(item.EffectivePath, picker.FileName, true);
                }
                catch (Exception error) { ShowError(error); }
            }
        }

        void Delete(ImageRecord item)
        {
            if (!item.IsTransient && MessageBox.Show(
                "将永久删除原文件：\n" + item.SourcePath + "\n\n此操作不可撤销。继续吗？",
                "删除原文件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            string error;
            bool deleted = library.Delete(item, out error);
            if (deleted)
                ShowNotice("已删除：" + item.Name);
            else
                ShowError(new IOException("原文件删除失败，图片仍保留在列表中：" + Environment.NewLine + error));
        }

        void Expand()
        {
            collapseTimer.Stop();
            if (retracting)
            {
                retracting = false;
                overlay.CancelRetract();
                return;
            }
            if (expanded) return;
            expanded = true;
            Height = ExpandedHeight;
            surface.Height = ExpandedHeight;
            overlay.SetExpanded(true);
        }

        void ScheduleCollapse()
        {
            if (!expanded || retracting || dragging || collapseTimer.IsEnabled) return;
            collapseTimer.Start();
        }

        bool IsCursorInShelf()
        {
            return IsCursorInShelf(Forms.Cursor.Position);
        }

        bool IsCursorInShelf(System.Drawing.Point cursor)
        {
            if (overlay.IsMenuOpen) return true;
            if (archive.IsVisible)
            {
                if (archive.IsContextMenuOpen) return true;
                Point local = archive.PointFromScreen(new Point(cursor.X, cursor.Y));
                if (local.X >= 0 && local.X < archive.ActualWidth &&
                    local.Y >= 0 && local.Y < archive.ActualHeight)
                    return true;
            }
            return IsCursorAtTop(cursor) || overlay.IsInteractivePoint(cursor);
        }

        static bool IsCursorAtTop(System.Drawing.Point cursor)
        {
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            return cursor.X >= area.Left && cursor.X < area.Right &&
                cursor.Y >= area.Top && cursor.Y <= area.Top + 2;
        }

        void Collapse()
        {
            collapseTimer.Stop();
            if (!expanded || retracting) return;
            archive.Hide();
            if (SystemParameters.ClientAreaAnimation)
            {
                retracting = true;
                overlay.Retract(FinishCollapse);
                return;
            }
            FinishCollapse();
        }

        void CollapseImmediately()
        {
            collapseTimer.Stop();
            if (retracting)
            {
                retracting = false;
                overlay.CancelRetract();
            }
            if (expanded) FinishCollapse();
        }

        void FinishCollapse()
        {
            archive.Hide();
            retracting = false;
            expanded = false;
            Height = CollapsedHeight;
            surface.Height = CollapsedHeight;
            overlay.SetExpanded(false);
        }

        void StartDrag(ImageRecord item)
        {
            dragging = true;
            try
            {
                DragDrop.DoDragDrop(surface, CreateDataObject(item), DragDropEffects.Copy);
            }
            catch { }
            finally { dragging = false; }
        }

        void ShowNotice(string text)
        {
            notice.Text = text;
            notice.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(async delegate
            {
                await System.Threading.Tasks.Task.Delay(1200);
                if (notice.Text == text) notice.Visibility = Visibility.Collapsed;
            }));
        }

        Button ToolButton(string text, Action action, string background, string foreground)
        {
            var button = new Button
            {
                Content = text,
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brush(background),
                Foreground = Brush(foreground),
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Cursor = Cursors.Hand,
                Template = FlatButtonTemplate(5)
            };
            button.Click += delegate { action(); };
            return button;
        }

        static ControlTemplate FlatButtonTemplate(double radius)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            content.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("ContentTemplate")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            content.SetBinding(ContentPresenter.MarginProperty, new System.Windows.Data.Binding("Padding")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
            });
            border.AppendChild(content);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        void ImportFiles()
        {
            var picker = new OpenFileDialog
            {
                Title = "导入图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp",
                Multiselect = true
            };
            if (picker.ShowDialog() == true)
            {
                library.AddFiles(picker.FileNames);
                Expand();
            }
        }

        async void CaptureRegion()
        {
            CollapseImmediately();
            overlay.Hide();
            Hide();
            await System.Threading.Tasks.Task.Delay(180);
            try
            {
                BitmapSource image = await CaptureOverlay.ShowAsync();
                if (image == null) return;
                library.AddTransient(image, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            }
            finally
            {
                Show();
                overlay.Show();
                Activate();
                CollapseImmediately();
            }
        }

        void AddFolder()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择要纳入截图绳的图片文件夹" })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                try { library.AddWatcher(picker.SelectedPath, true, true); }
                catch (UnauthorizedAccessException error) { ShowError(error); return; }
                catch (IOException error) { ShowError(error); return; }
                ShowNotice("已开始监听文件夹");
            }
        }

        void ShowInitialSpecialFolderSettings()
        {
            if (specialFolderPromptShown ||
                (store.State.WeChatPathDecided && store.State.QqPathDecided))
                return;
            specialFolderPromptShown = true;
            ShowSpecialFolderSettings(true);
        }

        void ShowSpecialFolderSettings()
        {
            ShowSpecialFolderSettings(false);
        }

        void ShowSpecialFolderSettings(bool initial)
        {
            var settings = new SpecialFolderSettingsWindow(
                initial,
                store.State.WeChatFolder,
                store.State.WeChatPathDecided,
                store.State.QqFolder,
                store.State.QqPathDecided)
            {
                Owner = this
            };
            settings.ShowDialog();
            if (settings.WeChatChanged)
                ApplySpecialFolder(
                    settings.WeChatFolder,
                    settings.WeChatPathDecided,
                    store.State.WeChatFolder,
                    true);
            if (settings.QqChanged)
                ApplySpecialFolder(
                    settings.QqFolder,
                    settings.QqPathDecided,
                    store.State.QqFolder,
                    false);
            store.Save();
        }

        void ApplySpecialFolder(string folder, bool decided, string previousFolder, bool weChat)
        {
            if (weChat)
            {
                store.State.WeChatFolder = string.IsNullOrWhiteSpace(folder) ? null : FileRules.Normalize(folder);
                store.State.WeChatPathDecided = decided;
                folder = store.State.WeChatFolder;
            }
            else
            {
                store.State.QqFolder = string.IsNullOrWhiteSpace(folder) ? null : FileRules.Normalize(folder);
                store.State.QqPathDecided = decided;
                folder = store.State.QqFolder;
            }

            if (!string.IsNullOrWhiteSpace(previousFolder) &&
                !FileRules.SamePath(previousFolder, placement.DownloadsFolder) &&
                (string.IsNullOrWhiteSpace(folder) || !FileRules.SamePath(previousFolder, folder)))
                library.RemoveSpecialWatcher(previousFolder);

            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                library.AddWatcher(folder, true, false, true);
        }

        void OnFolderImageDiscovered(ImageRecord item)
        {
            if (!placement.IsRecent(item)) return;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                    Expand();
                }));
            }
            catch (InvalidOperationException) { }
        }

        void SetClipboardCollection(bool enabled)
        {
            store.State.CollectClipboard = enabled;
            store.Save();
            clipboardSequence = WindowsNative.ClipboardSequence;
        }

        void ReadClipboard()
        {
            if (clipboardBusy || !store.State.CollectClipboard) return;
            uint sequence = WindowsNative.ClipboardSequence;
            if (sequence == 0 || sequence == clipboardSequence) return;
            clipboardBusy = true;
            bool processed = false;
            try
            {
                if (Forms.Clipboard.ContainsFileDropList())
                {
                    var paths = Forms.Clipboard.GetFileDropList().Cast<string>().Where(FileRules.IsImage).ToArray();
                    if (paths.Length > 0)
                        processed = library.AddFiles(paths) > 0;
                }
                if (!processed && Forms.Clipboard.ContainsImage())
                {
                    using (var image = Forms.Clipboard.GetImage())
                    {
                        if (image != null)
                        {
                            using (var stream = new MemoryStream())
                            {
                                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                                stream.Position = 0;
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = stream;
                                bitmap.EndInit();
                                bitmap.Freeze();
                                library.AddTransient(bitmap, "clipboard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                                processed = true;
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (processed) clipboardSequence = sequence;
                clipboardBusy = false;
            }
        }

        void ShowError(Exception error)
        {
            MessageBox.Show(error.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        static SolidColorBrush Brush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
    }
}
