using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        const double CollapsedHeight = 14;
        const double ExpandedHeight = 132;

        readonly StateStore store;
        readonly ImageLibrary library;
        readonly Border surface = new Border();
        readonly StackPanel cards = new StackPanel { Orientation = Orientation.Horizontal };
        readonly ScrollViewer scroller = new ScrollViewer();
        readonly TextBlock hint = new TextBlock();
        readonly TextBlock notice = new TextBlock();
        readonly DispatcherTimer collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly CheckBox localCopy = new CheckBox { Content = "保存本地副本", Foreground = Brush("#D7E3F2"), FontSize = 11 };
        readonly CheckBox collectClipboard = new CheckBox { Content = "收集剪贴板", Foreground = Brush("#D7E3F2"), FontSize = 11, Margin = new Thickness(10, 0, 0, 0) };
        ImageRecord dragItem;
        Point dragStart;
        bool dragging;
        bool expanded;
        uint clipboardSequence;
        bool clipboardBusy;

        public MainWindow(StateStore store, ImageLibrary library)
        {
            this.store = store;
            this.library = library;
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
            Width = area.Width;
            Height = CollapsedHeight;

            BuildSurface();
            library.Changed += RefreshCards;
            library.FolderImageDiscovered += OnFolderImageDiscovered;
            localCopy.IsChecked = store.State.CopyToLibrary;
            collectClipboard.IsChecked = store.State.CollectClipboard;
            clipboardSequence = WindowsNative.ClipboardSequence;
            localCopy.Checked += delegate { store.State.CopyToLibrary = true; store.Save(); };
            localCopy.Unchecked += delegate { store.State.CopyToLibrary = false; store.Save(); };
            collectClipboard.Checked += delegate { SetClipboardCollection(true); };
            collectClipboard.Unchecked += delegate { SetClipboardCollection(false); };
            collapseTimer.Tick += delegate { if (!IsMouseOver && !dragging) Collapse(); };
            refreshTimer.Tick += delegate
            {
                library.RefreshAll();
                ReadClipboard();
            };
            refreshTimer.Start();
            Closed += delegate
            {
                collapseTimer.Stop();
                refreshTimer.Stop();
                library.Changed -= RefreshCards;
                library.FolderImageDiscovered -= OnFolderImageDiscovered;
                library.Dispose();
            };
            RefreshCards();
        }

        void BuildSurface()
        {
            surface.Background = Brush("#F8FAFC");
            surface.BorderBrush = Brush("#CBD5E1");
            surface.BorderThickness = new Thickness(0, 0, 0, 1);
            surface.CornerRadius = new CornerRadius(0, 0, 9, 9);
            surface.Padding = new Thickness(12, 8, 12, 9);
            surface.MouseEnter += delegate { Expand(); };
            surface.MouseLeave += delegate { ScheduleCollapse(); };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var toolbar = new DockPanel();
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock { Text = "截图绳", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("#18212D") });
            brand.Children.Add(new TextBlock { Text = "  单击复制 · 拖拽取用 · 钉在桌面", FontSize = 11, Foreground = Brush("#667085"), VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(brand, Dock.Left);
            toolbar.Children.Add(brand);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(localCopy);
            actions.Children.Add(collectClipboard);
            actions.Children.Add(ToolButton("导入", ImportFiles, "#E8F1FF", "#175CD3"));
            actions.Children.Add(ToolButton("目录", AddFolder, "#E8F1FF", "#175CD3"));
            actions.Children.Add(ToolButton("截图", CaptureRegion, "#EAF8F0", "#137A46"));
            actions.Children.Add(ToolButton("收起", Collapse, "#F2F4F7", "#475467"));
            toolbar.Children.Add(actions);
            root.Children.Add(toolbar);

            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            scroller.Content = cards;
            Grid.SetRow(scroller, 1);
            root.Children.Add(scroller);

            hint.Text = "把鼠标移到屏幕顶部，截图会挂在这里";
            hint.FontSize = 12;
            hint.Foreground = Brush("#667085");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            notice.Foreground = Brush("#175CD3");
            notice.FontSize = 11;
            notice.HorizontalAlignment = HorizontalAlignment.Center;
            notice.VerticalAlignment = VerticalAlignment.Bottom;
            notice.Visibility = Visibility.Collapsed;
            Grid.SetRow(notice, 1);
            root.Children.Add(notice);

            surface.Child = root;
            Content = surface;
        }

        void RefreshCards()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                cards.Children.Clear();
                foreach (var item in library.Items.Take(50))
                    cards.Children.Add(CreateCard(item));
                hint.Visibility = cards.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                scroller.Visibility = cards.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }));
        }

        Border CreateCard(ImageRecord item)
        {
            var card = new Border
            {
                Width = 146,
                Height = 83,
                Margin = new Thickness(4, 0, 4, 0),
                Padding = new Thickness(7),
                Background = Brushes.White,
                BorderBrush = Brush("#D7DEE8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                ToolTip = item.SourcePath
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });

            var image = new Image
            {
                Source = item.IsAvailable ? item.Thumbnail : null,
                Stretch = Stretch.Uniform,
                Opacity = item.IsAvailable ? 1 : 0.35
            };
            Grid.SetColumnSpan(image, 2);
            grid.Children.Add(image);

            var pin = new Button
            {
                Content = "钉",
                Width = 25,
                Height = 22,
                Padding = new Thickness(0),
                FontSize = 11,
                Background = Brush("#EAF8F0"),
                Foreground = Brush("#137A46"),
                BorderThickness = new Thickness(0),
                ToolTip = "钉在桌面上",
                IsEnabled = item.IsAvailable,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2)
            };
            pin.Click += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                Pin(item);
            };
            Grid.SetColumn(pin, 1);
            grid.Children.Add(pin);

            var delete = new Button
            {
                Content = "×",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Background = Brush("#FFF1F0"),
                Foreground = Brush("#B42318"),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "删除图片"
            };
            delete.Click += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                Delete(item);
            };
            Grid.SetColumn(delete, 1);
            grid.Children.Add(delete);

            var name = new TextBlock
            {
                Text = item.IsAvailable ? item.Name : "离线 · " + item.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 10,
                Foreground = item.IsAvailable ? Brush("#344054") : Brush("#B42318"),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(name, 1);
            Grid.SetColumnSpan(name, 2);
            grid.Children.Add(name);
            card.Child = grid;

            card.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ChangedButton != MouseButton.Left) return;
                dragItem = item;
                dragStart = args.GetPosition(card);
                dragging = false;
                card.CaptureMouse();
            };
            card.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (dragItem != item || args.LeftButton != MouseButtonState.Pressed || dragging) return;
                Point current = args.GetPosition(card);
                if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragging = true;
                card.ReleaseMouseCapture();
                try { DragDrop.DoDragDrop(card, CreateDataObject(item), DragDropEffects.Copy); }
                catch { }
                finally { dragging = false; dragItem = null; }
            };
            card.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (dragItem != item) return;
                card.ReleaseMouseCapture();
                dragItem = null;
                if (!dragging) Copy(item);
                dragging = false;
            };
            card.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                ShowContextMenu(card, item);
            };
            return card;
        }

        ContextMenu ShowContextMenu(FrameworkElement target, ImageRecord item)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("复制图片", delegate { Copy(item); }));
            menu.Items.Add(MenuItem("钉在桌面", delegate { Pin(item); }));
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
                ShowNotice("已复制：" + item.Name);
            }
            catch (Exception error) { ShowError(error); }
        }

        void Pin(ImageRecord item)
        {
            try
            {
                var window = new PinnedWindow(item);
                window.Show();
                ShowNotice("已钉在桌面：" + item.Name);
            }
            catch (Exception error) { ShowError(error); }
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
            string error;
            bool deleted = library.Delete(item, out error);
            if (deleted)
                ShowNotice("已删除：" + item.Name);
            else
                ShowError(new IOException("图片已从截图绳移除，但文件删除失败：" + Environment.NewLine + error));
        }

        void Expand()
        {
            collapseTimer.Stop();
            if (expanded) return;
            expanded = true;
            Height = ExpandedHeight;
        }

        void ScheduleCollapse()
        {
            if (dragging) return;
            collapseTimer.Stop();
            collapseTimer.Start();
        }

        void Collapse()
        {
            collapseTimer.Stop();
            expanded = false;
            Height = CollapsedHeight;
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
                Cursor = Cursors.Hand
            };
            button.Click += delegate { action(); };
            return button;
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
                library.AddFiles(picker.FileNames, localCopy.IsChecked == true);
                Expand();
            }
        }

        async void CaptureRegion()
        {
            Collapse();
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
                Activate();
                Collapse();
            }
        }

        void AddFolder()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择要纳入截图绳的图片文件夹" })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                try { library.AddWatcher(picker.SelectedPath, true, true, localCopy.IsChecked == true); }
                catch (UnauthorizedAccessException error) { ShowError(error); return; }
                catch (IOException error) { ShowError(error); return; }
                ShowNotice("已开始监听文件夹");
            }
        }

        void OnFolderImageDiscovered()
        {
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
            if (clipboardBusy || collectClipboard.IsChecked != true) return;
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
                    {
                        library.AddFiles(paths, localCopy.IsChecked == true);
                        processed = true;
                    }
                }
                else if (Forms.Clipboard.ContainsImage())
                {
                    using (var image = Forms.Clipboard.GetImage())
                    {
                        if (image != null)
                        {
                            Directory.CreateDirectory(store.LibraryFolder);
                            string path = Path.Combine(store.LibraryFolder, "clipboard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                            image.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            if (library.AddFile(path, false) != null)
                                processed = true;
                            else
                            {
                                try { File.Delete(path); } catch { }
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
