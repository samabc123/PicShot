using Microsoft.Win32;
using System;
using System.Collections.Generic;
using DrawingRectangle = System.Drawing.Rectangle;
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
        const double CollapsedHeight = 6;
        const double ExpandedHeight = 132;

        readonly StateStore store;
        readonly ImageLibrary library;
        readonly Border surface = new Border();
        readonly StackPanel cards = new StackPanel { Orientation = Orientation.Horizontal };
        readonly Border hitBar = new Border();
        readonly Border rope = new Border();
        readonly Button handle = new Button();
        readonly TextBlock hint = new TextBlock();
        readonly TextBlock notice = new TextBlock();
        readonly DispatcherTimer collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        readonly CheckBox localCopy = new CheckBox { Content = "副本", Foreground = Brush("#B8C5D1"), FontSize = 11, ToolTip = "导入外部图片时保存本地副本" };
        readonly CheckBox collectClipboard = new CheckBox { Content = "剪贴板", Foreground = Brush("#B8C5D1"), FontSize = 11, Margin = new Thickness(10, 0, 0, 0), ToolTip = "自动收集剪贴板中的图片" };
        readonly TextBlock count = new TextBlock();
        ImageRecord dragItem;
        Point dragStart;
        bool dragging;
        bool expanded;
        uint clipboardSequence;
        bool clipboardBusy;
        bool regionUpdatePending;

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

            SourceInitialized += delegate { ScheduleWindowRegionUpdate(); };
            Loaded += delegate { ScheduleWindowRegionUpdate(); };
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
            surface.Background = Brushes.Transparent;
            surface.BorderBrush = Brushes.Transparent;
            surface.BorderThickness = new Thickness(0);
            surface.CornerRadius = new CornerRadius(0);
            surface.Padding = new Thickness(0);
            surface.MouseEnter += delegate { Expand(); };
            surface.MouseMove += delegate { Expand(); };
            surface.MouseLeave += delegate { ScheduleCollapse(); };

            var root = new Grid();
            hitBar.Height = CollapsedHeight;
            hitBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
            hitBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            hitBar.VerticalAlignment = VerticalAlignment.Top;
            hitBar.MouseEnter += delegate { Expand(); };
            hitBar.MouseMove += delegate { Expand(); };
            hitBar.MouseLeave += delegate { ScheduleCollapse(); };
            root.Children.Add(hitBar);

            rope.Height = 1;
            rope.Margin = new Thickness(0, 15, 0, 0);
            rope.Background = Brush("#90AEB5");
            rope.Opacity = 0.95;
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

            hint.Text = "截图会挂在这里";
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
            Content = surface;
        }

        void RefreshCards()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                cards.Children.Clear();
                int index = 0;
                foreach (var item in library.Items.Take(50))
                    cards.Children.Add(CreateCard(item, index++));
            count.Text = library.Items.Count + " 张";
                hint.Visibility = cards.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                cards.Visibility = cards.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                ScheduleWindowRegionUpdate();
            }));
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
            hanger.RenderTransform = new RotateTransform(index % 3 == 0 ? -1.5 : index % 3 == 1 ? 1.2 : 0.4);

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
                ToolTip = "钉在桌面上",
                IsEnabled = item.IsAvailable,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 2, 2)
            };
            pin.Click += delegate(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                Pin(item);
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
            menu.Items.Add(new Separator());

            var copyMenu = new MenuItem
            {
                Header = "导入时保存副本",
                IsCheckable = true,
                IsChecked = localCopy.IsChecked == true
            };
            copyMenu.Click += delegate { localCopy.IsChecked = copyMenu.IsChecked; };
            menu.Items.Add(copyMenu);

            var clipboardMenu = new MenuItem
            {
                Header = "收集剪贴板图片",
                IsCheckable = true,
                IsChecked = collectClipboard.IsChecked == true
            };
            clipboardMenu.Click += delegate { collectClipboard.IsChecked = clipboardMenu.IsChecked; };
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
            ScheduleWindowRegionUpdate();
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
            ScheduleWindowRegionUpdate();
        }

        void ScheduleWindowRegionUpdate()
        {
            if (regionUpdatePending || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            regionUpdatePending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(delegate
            {
                regionUpdatePending = false;
                ApplyWindowRegion();
            }));
        }

        void ApplyWindowRegion()
        {
            if (!IsLoaded || !IsVisible) return;

            var source = PresentationSource.FromVisual(this);
            double scaleX = source == null || source.CompositionTarget == null
                ? 1
                : source.CompositionTarget.TransformToDevice.M11;
            double scaleY = source == null || source.CompositionTarget == null
                ? 1
                : source.CompositionTarget.TransformToDevice.M22;

            var regions = new List<DrawingRectangle>();
            AddRegion(regions, hitBar, scaleX, scaleY);

            if (expanded)
            {
                AddRegion(regions, rope, scaleX, scaleY);
                AddRegion(regions, handle, scaleX, scaleY);
                foreach (UIElement child in cards.Children)
                    AddRegion(regions, child, scaleX, scaleY);
                AddRegion(regions, hint, scaleX, scaleY);
                AddRegion(regions, notice, scaleX, scaleY);
            }

            if (regions.Count > 0)
                WindowsNative.SetWindowRegion(this, regions);
        }

        void AddRegion(List<DrawingRectangle> regions, UIElement element, double scaleX, double scaleY)
        {
            if (element == null || element.Visibility != Visibility.Visible || element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0)
                return;

            try
            {
                Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
                int left = (int)Math.Floor(bounds.Left * scaleX);
                int top = (int)Math.Floor(bounds.Top * scaleY);
                int right = (int)Math.Ceiling(bounds.Right * scaleX);
                int bottom = (int)Math.Ceiling(bounds.Bottom * scaleY);
                regions.Add(DrawingRectangle.FromLTRB(left, top, right, bottom));
            }
            catch (InvalidOperationException) { }
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
