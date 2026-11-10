using System;
using System.Collections.Generic;
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
    internal sealed class ArchiveWindow : Window
    {
        readonly Action<ImageRecord> copy;
        readonly Action<ImageRecord> pin;
        readonly Action<ImageRecord> resetPlacement;
        readonly Action<ImageRecord> open;
        readonly Action<ImageRecord> locate;
        readonly Action<ImageRecord> save;
        readonly Action<ImageRecord> delete;
        readonly Func<ImageRecord, DataObject> createData;
        readonly StackPanel folders = new StackPanel();
        readonly StackPanel pictures = new StackPanel();
        readonly TextBox search = new TextBox();
        readonly TextBlock titleCount = new TextBlock();
        readonly TextBlock resultCount = new TextBlock();
        readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord[] filteredItems = new ImageRecord[0];
        List<string> itemKeys = new List<string>();
        string selectedFolder;
        int shownCount;
        bool shuttingDown;
        ImageRecord dragItem;
        Point dragStart;

        public bool IsContextMenuOpen { get; private set; }

        public ArchiveWindow(
            Action<ImageRecord> copy,
            Action<ImageRecord> pin,
            Action<ImageRecord> resetPlacement,
            Action<ImageRecord> open,
            Action<ImageRecord> locate,
            Action<ImageRecord> save,
            Action<ImageRecord> delete,
            Func<ImageRecord, DataObject> createData)
        {
            this.copy = copy;
            this.pin = pin;
            this.resetPlacement = resetPlacement;
            this.open = open;
            this.locate = locate;
            this.save = save;
            this.delete = delete;
            this.createData = createData;
            Title = "图片收纳";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brush("#FFFFFF");
            var area = Forms.Screen.PrimaryScreen.WorkingArea;
            Width = Math.Min(520, Math.Max(360, area.Width - 24));
            Height = Math.Min(600, Math.Max(280, area.Height - 160));
            Left = area.Right - Width - 12;
            Top = Math.Min(area.Bottom - Height - 8, area.Top + 137);

            var frame = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#CED8DF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                SnapsToDevicePixels = true
            };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            frame.Child = layout;
            Content = frame;

            var header = new Grid { Margin = new Thickness(18, 0, 12, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock
            {
                Text = "图片收纳",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#243A4B")
            });
            titleCount.Margin = new Thickness(9, 3, 0, 0);
            titleCount.FontSize = 11;
            titleCount.Foreground = Brush("#647887");
            heading.Children.Add(titleCount);
            header.Children.Add(heading);
            var close = new Button
            {
                Content = "\u00d7",
                Width = 30,
                Height = 30,
                FontSize = 21,
                Background = Brushes.Transparent,
                Foreground = Brush("#526979"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "关闭图片收纳"
            };
            close.Click += delegate { Hide(); };
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            layout.Children.Add(header);

            var searchRow = new Grid { Margin = new Thickness(18, 4, 18, 12) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            search.FontSize = 12;
            search.Padding = new Thickness(10, 6, 10, 6);
            search.BorderBrush = Brush("#BECBD3");
            search.BorderThickness = new Thickness(1);
            search.ToolTip = "搜索文件名或原路径";
            search.TextChanged += delegate
            {
                searchTimer.Stop();
                searchTimer.Start();
            };
            searchTimer.Tick += delegate
            {
                searchTimer.Stop();
                RenderPictures();
            };
            searchRow.Children.Add(search);
            resultCount.VerticalAlignment = VerticalAlignment.Center;
            resultCount.Margin = new Thickness(10, 0, 0, 0);
            resultCount.Foreground = Brush("#687D8A");
            resultCount.FontSize = 11;
            Grid.SetColumn(resultCount, 1);
            searchRow.Children.Add(resultCount);
            Grid.SetRow(searchRow, 1);
            layout.Children.Add(searchRow);

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var folderScroll = new ScrollViewer
            {
                Background = Brush("#F2F6F8"),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = folders
            };
            columns.Children.Add(folderScroll);
            var pictureScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = pictures
            };
            Grid.SetColumn(pictureScroll, 1);
            columns.Children.Add(pictureScroll);
            Grid.SetRow(columns, 2);
            layout.Children.Add(columns);

            IsVisibleChanged += delegate
            {
                if (IsVisible)
                {
                    RenderFolders();
                    RenderPictures();
                    search.Focus();
                }
                else
                {
                    IsContextMenuOpen = false;
                    searchTimer.Stop();
                }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (shuttingDown) return;
                args.Cancel = true;
                Hide();
            };
            Closed += delegate { searchTimer.Stop(); };
        }

        public void CloseForShutdown()
        {
            shuttingDown = true;
            Close();
        }

        public void SetItems(IEnumerable<ImageRecord> records)
        {
            var updated = records.ToList();
            var keys = updated.Select(item => item.Id + "\n" + item.SourcePath + "\n" +
                item.LastWriteUtc.Ticks).ToList();
            bool changed = !keys.SequenceEqual(itemKeys);
            items = updated;
            itemKeys = keys;
            if (changed && IsVisible)
            {
                RenderFolders();
                RenderPictures();
            }
        }

        void RenderFolders()
        {
            folders.Children.Clear();
            titleCount.Text = items.Count + " 张";
            var groups = items.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Max(item => item.AddedUtc > item.LastWriteUtc
                    ? item.AddedUtc : item.LastWriteUtc)).ToArray();
            if (selectedFolder != null &&
                !groups.Any(group => string.Equals(group.Key, selectedFolder, StringComparison.OrdinalIgnoreCase)))
                selectedFolder = null;
            AddFolderButton("全部图片", null, items.Count);
            foreach (var group in groups)
            {
                string name = group.Key == "#temporary" ? "本次临时图片" :
                    Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar));
                AddFolderButton(string.IsNullOrWhiteSpace(name) ? group.Key : name, group.Key, group.Count());
            }
        }

        static string GroupKey(ImageRecord item)
        {
            return item.IsTransient ? "#temporary" : item.Folder ?? "";
        }

        void AddFolderButton(string name, string path, int count)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                FontWeight = selectedFolder == path ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush("#263E50"),
                VerticalAlignment = VerticalAlignment.Center
            });
            var number = new TextBlock
            {
                Text = count.ToString(),
                FontSize = 11,
                Foreground = Brush("#687E8D"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            var button = new Button
            {
                Content = row,
                Padding = new Thickness(13, 9, 13, 9),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = selectedFolder == path ? Brush("#DBEAF1") : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = path ?? "所有原目录"
            };
            button.Click += delegate
            {
                selectedFolder = path;
                RenderFolders();
                RenderPictures();
            };
            folders.Children.Add(button);
        }

        void RenderPictures()
        {
            pictures.Children.Clear();
            shownCount = 0;
            string query = search.Text.Trim();
            filteredItems = items.Where(item =>
                (selectedFolder == null ||
                 string.Equals(GroupKey(item), selectedFolder, StringComparison.OrdinalIgnoreCase)) &&
                (query.Length == 0 ||
                 item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 item.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderByDescending(item => item.AddedUtc > item.LastWriteUtc
                    ? item.AddedUtc : item.LastWriteUtc).ToArray();
            if (filteredItems.Length == 0)
            {
                resultCount.Text = "0 张";
                pictures.Children.Add(new TextBlock
                {
                    Text = "没有匹配的图片",
                    Foreground = Brush("#607786"),
                    Margin = new Thickness(20, 22, 0, 0),
                    FontSize = 12
                });
                return;
            }
            AppendPage();
        }

        void AppendPage()
        {
            var last = pictures.Children.OfType<Button>().LastOrDefault();
            if (last != null && Equals(last.Tag, "more")) pictures.Children.Remove(last);
            int end = Math.Min(filteredItems.Length, shownCount + 60);
            for (int index = shownCount; index < end; index++)
            {
                ImageRecord item = filteredItems[index];
                pictures.Children.Add(CreatePictureRow(item));
            }
            shownCount = end;
            resultCount.Text = shownCount < filteredItems.Length ?
                shownCount + " / " + filteredItems.Length : filteredItems.Length + " 张";
            if (shownCount < filteredItems.Length)
            {
                var more = new Button
                {
                    Content = "继续浏览",
                    Tag = "more",
                    Margin = new Thickness(14, 12, 14, 12),
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = Brush("#EEF5F8"),
                    Foreground = Brush("#315A72"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                more.Click += delegate { AppendPage(); };
                pictures.Children.Add(more);
            }
        }

        Button CreatePictureRow(ImageRecord item)
        {
            var row = new Grid { Height = 68, Margin = new Thickness(11, 0, 11, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var preview = new Border
            {
                Width = 52,
                Height = 49,
                Background = Brush("#EDF2F5"),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center
            };
            var thumbnail = item.Thumbnail;
            if (thumbnail != null)
                preview.Child = new Image
                {
                    Source = thumbnail,
                    Stretch = Stretch.Uniform,
                    Width = 48,
                    Height = 45
                };
            row.Children.Add(preview);
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock
            {
                Text = item.Name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = Brush("#243A4B")
            });
            details.Children.Add(new TextBlock
            {
                Text = item.IsTransient ? "临时图片 · 关闭后消失" : item.Folder ?? "",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brush("#718593"),
                FontSize = 10,
                Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetColumn(details, 1);
            row.Children.Add(details);
            var button = new Button
            {
                Content = row,
                Background = Brushes.White,
                BorderBrush = Brush("#E6EDF0"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Cursor = Cursors.Hand,
                ToolTip = item.SourcePath
            };
            button.Click += delegate { copy(item); };
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                dragStart = args.GetPosition(button);
                dragItem = item;
            };
            button.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (dragItem != item || args.LeftButton != MouseButtonState.Pressed) return;
                var current = args.GetPosition(button);
                if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragItem = null;
                try { DragDrop.DoDragDrop(button, createData(item), DragDropEffects.Copy); }
                catch (IOException) { }
            };
            button.PreviewMouseLeftButtonUp += delegate { dragItem = null; };
            var menu = new ContextMenu();
            AddMenuItem(menu, "复制图片", delegate { copy(item); });
            AddMenuItem(menu, "固定在绳上", delegate { pin(item); });
            if (item.RopePinned.HasValue)
                AddMenuItem(menu, "恢复自动分类", delegate { resetPlacement(item); });
            AddMenuItem(menu, "打开图片", delegate { open(item); });
            AddMenuItem(menu, "定位文件", delegate { locate(item); });
            AddMenuItem(menu, "保存副本", delegate { save(item); });
            menu.Items.Add(new Separator());
            AddMenuItem(menu, item.IsTransient ? "移除临时图片" : "删除原文件",
                delegate { delete(item); });
            menu.Opened += delegate { IsContextMenuOpen = true; };
            menu.Closed += delegate { IsContextMenuOpen = false; };
            button.ContextMenu = menu;
            return button;
        }

        static void AddMenuItem(ContextMenu menu, string label, Action action)
        {
            var option = new MenuItem { Header = label };
            option.Click += delegate { action(); };
            menu.Items.Add(option);
        }

        static Brush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
