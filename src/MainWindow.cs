using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class MainWindow : Window
    {
        readonly StateStore store;
        readonly ImageLibrary library;
        readonly ListBox gallery = new ListBox();
        readonly TextBox search = new TextBox();
        readonly TextBlock selectedName = new TextBlock();
        readonly TextBlock selectedPath = new TextBlock();
        readonly TextBlock selectedStatus = new TextBlock();
        readonly Image selectedPreview = new Image { Stretch = Stretch.Uniform };
        readonly CheckBox localCopy = new CheckBox { Content = "导入时保存本地副本", IsChecked = true };
        readonly CheckBox collectClipboard = new CheckBox { Content = "自动收集剪贴板图片", Margin = new Thickness(0, 8, 0, 0) };
        readonly TextBlock countLabel = new TextBlock();
        readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        ICollectionView view;
        ImageRecord selected;
        uint clipboardSequence;
        bool clipboardBusy;

        public MainWindow(StateStore store, ImageLibrary library)
        {
            this.store = store;
            this.library = library;
            Title = "FrameDock · 图片工作台";
            Width = 1240;
            Height = 780;
            MinWidth = 960;
            MinHeight = 620;
            Background = Brush("#F4F6F9");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeView();
            library.Changed += RefreshView;
            library.FolderImageDiscovered += OnFolderImageDiscovered;
            collectClipboard.IsChecked = store.State.CollectClipboard;
            localCopy.IsChecked = store.State.CopyToLibrary;
            clipboardSequence = WindowsNative.ClipboardSequence;
            collectClipboard.Checked += delegate { SetClipboardCollection(true); };
            collectClipboard.Unchecked += delegate { SetClipboardCollection(false); };
            localCopy.Checked += delegate { store.State.CopyToLibrary = true; store.Save(); };
            localCopy.Unchecked += delegate { store.State.CopyToLibrary = false; store.Save(); };
            refreshTimer.Tick += delegate
            {
                library.RefreshAll();
                if (selected != null) Select(selected);
                ReadClipboard();
            };
            refreshTimer.Start();
            Closed += delegate { refreshTimer.Stop(); library.Dispose(); };
        }

        void InitializeView()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(316) });

            root.Children.Add(BuildHeader());
            Grid.SetColumnSpan(root.Children[root.Children.Count - 1], 3);
            root.Children.Add(BuildSidebar());
            Grid.SetRow(root.Children[root.Children.Count - 1], 1);
            root.Children.Add(BuildGallery());
            Grid.SetRow(root.Children[root.Children.Count - 1], 1);
            Grid.SetColumn(root.Children[root.Children.Count - 1], 1);
            root.Children.Add(BuildDetails());
            Grid.SetRow(root.Children[root.Children.Count - 1], 1);
            Grid.SetColumn(root.Children[root.Children.Count - 1], 2);
            Content = root;
        }

        UIElement BuildHeader()
        {
            var panel = new Grid { Background = Brush("#FFFFFF"), Margin = new Thickness(0, 0, 0, 1) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(410) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });

            var title = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(26, 13, 0, 0) };
            title.Children.Add(new TextBlock { Text = "FrameDock", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brush("#18212D") });
            title.Children.Add(new TextBlock { Text = "把截图、参考图和外部图片放在同一个可靠的资源库里", FontSize = 12, Foreground = Brush("#697586"), Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(title);

            search.Width = 360;
            search.Height = 34;
            search.Margin = new Thickness(0, 19, 24, 0);
            search.Padding = new Thickness(12, 0, 12, 0);
            search.VerticalContentAlignment = VerticalAlignment.Center;
            search.TextChanged += delegate { view.Refresh(); };
            search.ToolTip = "搜索文件名、路径或状态";
            search.SetValue(Grid.ColumnProperty, 1);
            panel.Children.Add(search);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 22, 0) };
            actions.Children.Add(Button("导入图片", ImportFiles, "#1677FF"));
            actions.Children.Add(Button("添加文件夹", AddFolder, "#EEF4FF", "#175CD3"));
            actions.Children.Add(Button("区域截图", CaptureRegion, "#EAF8F0", "#137A46"));
            actions.SetValue(Grid.ColumnProperty, 2);
            panel.Children.Add(actions);
            return panel;
        }

        UIElement BuildSidebar()
        {
            var panel = new Border { Background = Brush("#202936"), Padding = new Thickness(20, 26, 18, 20) };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "资源库", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#9DB0C8"), Margin = new Thickness(0, 0, 0, 14) });
            content.Children.Add(NavButton("全部图片", delegate { search.Text = ""; }));
            content.Children.Add(NavButton("最近导入", delegate { search.Text = "在线"; }));
            content.Children.Add(NavButton("离线文件", delegate { search.Text = "离线"; }));
            content.Children.Add(new Separator { Margin = new Thickness(0, 22, 0, 20), Background = Brush("#354353") });
            content.Children.Add(new TextBlock { Text = "可靠性设置", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("#9DB0C8"), Margin = new Thickness(0, 0, 0, 10) });
            content.Children.Add(localCopy);
            content.Children.Add(new TextBlock { Text = "开启后，导入外部目录的图片会在 FrameDock 数据目录保存一份副本。原文件移动或删除后，副本仍可用。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#A9B7C8"), Margin = new Thickness(0, 10, 0, 0) });
            content.Children.Add(collectClipboard);
            content.Children.Add(new TextBlock { Text = "剪贴板图片会进入同一个资源库，并保留为独立文件，避免只依赖剪贴板当前内容。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#A9B7C8"), Margin = new Thickness(0, 10, 0, 0) });
            var bottom = new TextBlock { Text = "监听的文件夹会在重启后恢复。\n图片路径始终显示在右侧详情中。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush("#7F91A6"), Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Bottom };
            content.Children.Add(new Border { Height = 1, Background = Brush("#354353"), Margin = new Thickness(0, 28, 0, 14) });
            content.Children.Add(bottom);
            panel.Child = content;
            return panel;
        }

        UIElement BuildGallery()
        {
            var panel = new Grid { Background = Brush("#F4F6F9") };
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var top = new DockPanel { Margin = new Thickness(22, 18, 20, 0) };
            countLabel.Foreground = Brush("#667085");
            countLabel.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(countLabel, Dock.Right);
            top.Children.Add(countLabel);
            top.Children.Add(new TextBlock { Text = "图片库", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brush("#18212D"), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(top);

            gallery.Margin = new Thickness(18, 0, 18, 18);
            gallery.BorderThickness = new Thickness(0);
            gallery.Background = Brushes.Transparent;
            gallery.SelectionChanged += delegate { Select(gallery.SelectedItem as ImageRecord); };
            gallery.ItemTemplate = CardTemplate();
            gallery.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
            gallery.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            gallery.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            view = CollectionViewSource.GetDefaultView(library.Items);
            view.Filter = Filter;
            gallery.ItemsSource = view;
            Grid.SetRow(gallery, 1);
            panel.Children.Add(gallery);
            RefreshView();
            return panel;
        }

        UIElement BuildDetails()
        {
            var panel = new Border { Background = Brush("#FFFFFF"), BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(20, 24, 20, 20) };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = "图片详情", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("#18212D") });
            var previewFrame = new Border { Height = 230, Margin = new Thickness(0, 18, 0, 18), Background = Brush("#F2F4F7"), BorderBrush = Brush("#E4E7EC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10) };
            previewFrame.Child = selectedPreview;
            content.Children.Add(previewFrame);
            selectedName.TextWrapping = TextWrapping.Wrap;
            selectedName.FontSize = 16;
            selectedName.FontWeight = FontWeights.SemiBold;
            selectedName.Foreground = Brush("#18212D");
            content.Children.Add(selectedName);
            selectedStatus.Foreground = Brush("#137A46");
            selectedStatus.Margin = new Thickness(0, 6, 0, 0);
            content.Children.Add(selectedStatus);
            selectedPath.TextWrapping = TextWrapping.Wrap;
            selectedPath.Foreground = Brush("#667085");
            selectedPath.FontSize = 11;
            selectedPath.Margin = new Thickness(0, 14, 0, 16);
            content.Children.Add(selectedPath);
            var actions = new WrapPanel();
            actions.Children.Add(Button("复制", CopySelected, "#18212D"));
            actions.Children.Add(Button("打开", OpenSelected, "#EEF4FF", "#175CD3"));
            actions.Children.Add(Button("置顶", PinSelected, "#EAF8F0", "#137A46"));
            actions.Children.Add(Button("定位", LocateSelected, "#F2F4F7", "#475467"));
            actions.Children.Add(Button("另存", SaveSelected, "#F2F4F7", "#475467"));
            content.Children.Add(actions);
            content.Children.Add(Button("从资源库移除", RemoveSelected, "#FFF1F0", "#B42318", new Thickness(0, 18, 0, 0)));
            panel.Child = content;
            return panel;
        }

        DataTemplate CardTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.WidthProperty, 176.0);
            border.SetValue(Border.HeightProperty, 170.0);
            border.SetValue(Border.MarginProperty, new Thickness(4));
            border.SetValue(Border.PaddingProperty, new Thickness(8));
            border.SetValue(Border.BackgroundProperty, Brush("#FFFFFF"));
            border.SetValue(Border.BorderBrushProperty, Brush("#E2E8F0"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            var stack = new FrameworkElementFactory(typeof(StackPanel));
            var image = new FrameworkElementFactory(typeof(Image));
            image.SetValue(Image.HeightProperty, 108.0);
            image.SetValue(Image.StretchProperty, Stretch.Uniform);
            image.SetBinding(Image.SourceProperty, new Binding("Thumbnail"));
            stack.AppendChild(image);
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            name.SetValue(TextBlock.MarginProperty, new Thickness(0, 8, 0, 0));
            name.SetValue(TextBlock.ForegroundProperty, Brush("#344054"));
            stack.AppendChild(name);
            var status = new FrameworkElementFactory(typeof(TextBlock));
            status.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
            status.SetValue(TextBlock.FontSizeProperty, 10.0);
            status.SetValue(TextBlock.ForegroundProperty, Brush("#667085"));
            stack.AppendChild(status);
            border.AppendChild(stack);
            return new DataTemplate { VisualTree = border };
        }

        bool Filter(object value)
        {
            if (string.IsNullOrWhiteSpace(search.Text)) return true;
            var item = value as ImageRecord;
            string query = search.Text.Trim();
            return item != null && (item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.StatusText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void RefreshView()
        {
            if (view != null) view.Refresh();
            countLabel.Text = library.Items.Count + " 张图片";
        }

        void Select(ImageRecord item)
        {
            selected = item;
            if (item == null)
            {
                selectedName.Text = "选择一张图片";
                selectedStatus.Text = "从资源库选择图片查看详情";
                selectedPath.Text = "";
                selectedPreview.Source = null;
                return;
            }
            selectedName.Text = item.Name;
            selectedStatus.Text = item.StatusText + "  ·  " + item.Width + " × " + item.Height;
            selectedPath.Text = item.SourcePath + (item.LocalCopyPath == null ? "" : "\n本地副本：" + item.LocalCopyPath);
            selectedPreview.Source = item.Thumbnail;
        }

        void ImportFiles()
        {
            var picker = new OpenFileDialog
            {
                Title = "导入图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp",
                Multiselect = true
            };
            if (picker.ShowDialog() == true) library.AddFiles(picker.FileNames, localCopy.IsChecked == true);
        }

        void AddFolder()
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择要纳入 FrameDock 的图片文件夹" })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                library.AddWatcher(picker.SelectedPath, true, true);
                MessageBox.Show(this, "文件夹已加入监听，已有图片也已扫描。\n之后保存到这个目录的新图片会自动进入资源库。", "FrameDock", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        async void CaptureRegion()
        {
            Hide();
            await System.Threading.Tasks.Task.Delay(180);
            try
            {
                BitmapSource image = await CaptureOverlay.ShowAsync();
                if (image == null) return;
                Directory.CreateDirectory(store.LibraryFolder);
                string path = Path.Combine(store.LibraryFolder, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
                WindowsNative.SavePng(image, path);
                library.AddFile(path, false);
            }
            finally { Show(); Activate(); }
        }

        void OnFolderImageDiscovered(string path)
        {
            Dispatcher.BeginInvoke(new Action(delegate { library.AddFile(path, localCopy.IsChecked == true); }));
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
                            library.AddFile(path, false);
                            processed = true;
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

        void CopySelected()
        {
            if (!EnsureSelected()) return;
            try
            {
                var data = new DataObject();
                using (var stream = File.OpenRead(selected.EffectivePath))
                {
                    var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    data.SetImage(image);
                }
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { selected.EffectivePath });
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception error) { ShowError(error); }
        }

        void OpenSelected()
        {
            if (!EnsureSelected()) return;
            try { Process.Start(new ProcessStartInfo(selected.EffectivePath) { UseShellExecute = true }); }
            catch (Exception error) { ShowError(error); }
        }

        void PinSelected()
        {
            if (!EnsureSelected()) return;
            new PinnedWindow(selected).Show();
        }

        void LocateSelected()
        {
            if (!EnsureSelected()) return;
            try { Process.Start("explorer.exe", "/select,\"" + selected.EffectivePath + "\""); }
            catch (Exception error) { ShowError(error); }
        }

        void SaveSelected()
        {
            if (!EnsureSelected()) return;
            var picker = new SaveFileDialog { Title = "保存图片副本", FileName = selected.Name, Filter = "图片文件|*" + Path.GetExtension(selected.Name) };
            if (picker.ShowDialog() == true)
                try { File.Copy(selected.EffectivePath, picker.FileName, true); }
                catch (Exception error) { ShowError(error); }
        }

        void RemoveSelected()
        {
            if (!EnsureSelected()) return;
            if (MessageBox.Show(this, "只会从 FrameDock 资源库移除记录，并删除 FrameDock 保存的本地副本。\n原始文件不会被删除。", "确认移除", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            {
                library.Remove(selected, true);
                selected = null;
                Select(null);
            }
        }

        bool EnsureSelected()
        {
            if (selected != null && selected.IsAvailable) return true;
            MessageBox.Show(this, "这张图片当前不可用，请检查右侧路径或重新导入。", "FrameDock", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        void ShowError(Exception error)
        {
            MessageBox.Show(this, error.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Button NavButton(string text, RoutedEventHandler action)
        {
            var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 36, Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brush("#E6EDF5") };
            button.Click += action;
            return button;
        }

        Button Button(string text, Action action, string background, string foreground = "#FFFFFF", Thickness? margin = null)
        {
            var button = new Button { Content = text, Height = 34, Padding = new Thickness(13, 0, 13, 0), Margin = margin ?? new Thickness(0, 0, 8, 0), Background = Brush(background), Foreground = Brush(foreground), BorderThickness = new Thickness(0), FontSize = 12, Cursor = Cursors.Hand };
            button.Click += delegate { action(); };
            return button;
        }

        static SolidColorBrush Brush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }
    }
}
