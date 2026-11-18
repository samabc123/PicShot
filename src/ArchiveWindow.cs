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
using Microsoft.Win32;

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
        readonly StackPanel pictures = new StackPanel();
        readonly BasketScene basketScene = new BasketScene();
        readonly Canvas sockTiles;
        readonly ScrollViewer overview;
        readonly Grid detailPane = new Grid();
        readonly Border frame;
        readonly Grid searchRow;
        readonly TextBlock headingText = new TextBlock();
        bool showingOverview = true;
        readonly TextBox search = new TextBox();
        readonly TextBlock titleCount = new TextBlock();
        readonly TextBlock resultCount = new TextBlock();
        readonly TextBlock collectionTitle = new TextBlock();
        readonly StackPanel nameEditor = new StackPanel();
        readonly Button importCollection;
        readonly Button renameCollection;
        readonly Button deleteCollection;
        readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        ImageLibrary library;
        ContextMenu activeMenu;
        int interactionDepth;
        const string CollectionPrefix = "#collection:";
        List<ImageRecord> items = new List<ImageRecord>();
        ImageRecord[] filteredItems = new ImageRecord[0];
        List<string> itemKeys = new List<string>();
        string selectedFolder;
        int shownCount;
        bool shuttingDown;
        ImageRecord dragItem;
        ImageRecord lastClickedItem;
        Point dragStart;
        Point? keyboardAnchor;
        long openingUntil;
        string basketQuery = "";

        public Action MenuDismissed;
        public bool IsContextMenuOpen { get; private set; }
        public bool IsInteractionActive
        {
            get { return interactionDepth > 0 || nameEditor.Children.Count > 0 || IsContextMenuOpen; }
        }
        public bool IsOpeningTransitionActive
        {
            get { return System.Diagnostics.Stopwatch.GetTimestamp() < openingUntil; }
        }
        public bool IsKeyboardNavigationActive
        {
            get
            {
                if (!IsVisible || !IsKeyboardFocusWithin || !keyboardAnchor.HasValue) return false;
                if ((Mouse.GetPosition(this) - keyboardAnchor.Value).Length <= 1) return true;
                keyboardAnchor = null;
                return false;
            }
        }
        public void EndOpeningTransition()
        {
            openingUntil = 0;
        }

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
            Title = "收纳篮";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            sockTiles = basketScene.Items;
            PositionInWorkArea();

            frame = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                SnapsToDevicePixels = true
            };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            frame.Child = layout;
            Content = frame;

            var header = new Grid { Margin = new Thickness(18, 0, 12, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new Image
            {
                Source = SceneArtwork.BasketIcon, Width = 28, Height = 32,
                Margin = new Thickness(0, 0, 8, 0)
            });
            headingText.Text = "收纳篮";
            headingText.FontSize = 16;
            headingText.FontWeight = FontWeights.SemiBold;
            headingText.Foreground = Brush("#243A4B");
            headingText.VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(headingText);
            titleCount.Margin = new Thickness(9, 3, 0, 0);
            titleCount.FontSize = 11;
            titleCount.Foreground = Brush("#647887");
            heading.Children.Add(titleCount);
            header.Children.Add(new Border
            {
                Child = heading, Background = Brush("#F8FCFC"), CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center
            });
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
                ToolTip = "关闭收纳篮"
            };
            System.Windows.Automation.AutomationProperties.SetName(close, "关闭收纳篮");
            close.Click += delegate { Hide(); };
            var headerTools = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Background = Brush("#F8FCFC")
            };
            headerTools.Children.Add(IconButton("\uE710", "新建袜夹", delegate { EditCollection(null); }));
            headerTools.Children.Add(close);
            Grid.SetColumn(headerTools, 1);
            header.Children.Add(headerTools);
            layout.Children.Add(header);

            searchRow = new Grid { Margin = new Thickness(18, 4, 18, 12) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            search.FontSize = 12;
            search.Padding = new Thickness(10, 6, 10, 6);
            search.BorderBrush = Brush("#BECBD3");
            search.BorderThickness = new Thickness(1);
            search.ToolTip = "搜索袜夹名称、文件名或原路径";
            System.Windows.Automation.AutomationProperties.SetName(search, "搜索收纳篮");
            search.TextChanged += delegate
            {
                searchTimer.Stop();
                searchTimer.Start();
            };
            searchTimer.Tick += delegate
            {
                searchTimer.Stop();
                if (showingOverview) RenderSocks();
                else RenderPictures();
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

            nameEditor.Margin = new Thickness(18, 0, 18, 8);
            Grid.SetRow(nameEditor, 2);
            layout.Children.Add(nameEditor);
            var content = new Grid();
            overview = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = basketScene,
                Background = Brushes.Transparent,
                Focusable = false
            };
            content.Children.Add(overview);
            detailPane.Visibility = Visibility.Collapsed;
            var picturePane = new DockPanel();
            var toolbar = new DockPanel { Margin = new Thickness(12, 0, 8, 4), LastChildFill = true };
            var back = IconButton("\uE72B", "返回收纳篮", ShowOverview);
            DockPanel.SetDock(back, Dock.Left);
            toolbar.Children.Add(back);
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            importCollection = IconButton("\uE8B5", "导入图片到此袜夹", ImportCollection);
            renameCollection = IconButton("\uE70F", "重命名袜夹", delegate { EditCollection(SelectedCollection()); });
            deleteCollection = IconButton("\uE74D", "删除袜夹（保留原文件）", DeleteCollection);
            tools.Children.Add(importCollection);
            tools.Children.Add(renameCollection);
            tools.Children.Add(deleteCollection);
            DockPanel.SetDock(tools, Dock.Right);
            toolbar.Children.Add(tools);
            collectionTitle.FontSize = 12;
            collectionTitle.Foreground = Brush("#263E50");
            collectionTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            collectionTitle.VerticalAlignment = VerticalAlignment.Center;
            toolbar.Children.Add(collectionTitle);
            DockPanel.SetDock(toolbar, Dock.Top);
            picturePane.Children.Add(toolbar);
            var pictureScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = pictures
            };
            picturePane.Children.Add(pictureScroll);
            detailPane.Children.Add(picturePane);
            content.Children.Add(detailPane);
            Grid.SetRow(content, 3);
            layout.Children.Add(content);

            IsVisibleChanged += delegate
            {
                if (IsVisible)
                {
                    PositionInWorkArea();
                    openingUntil = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(System.Diagnostics.Stopwatch.Frequency * 1.6);
                    ShowOverview();
                    search.Focus();
                }
                else
                {
                    lastClickedItem = null;
                    openingUntil = 0;
                    keyboardAnchor = null;
                    CloseMenu();
                    nameEditor.Children.Clear();
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
            MouseEnter += delegate { EndOpeningTransition(); };
            PreviewMouseDown += delegate { keyboardAnchor = null; };
            Deactivated += delegate { keyboardAnchor = null; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                keyboardAnchor = Mouse.GetPosition(this);
                if (args.Key != Key.Escape || nameEditor.Children.Count > 0 || IsContextMenuOpen) return;
                if (showingOverview) Hide();
                else ShowOverview();
                args.Handled = true;
            };
        }

        public void PositionInWorkArea()
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Max(280, Math.Min(880, area.Width - 32));
            Height = Math.Max(260, Math.Min(720, area.Height - 48));
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            if (showingOverview && searchRow != null)
                searchRow.Width = Math.Max(200, Math.Min(440, Width - 50));
        }

        public void CloseForShutdown()
        {
            shuttingDown = true;
            Close();
        }

        public void AttachLibrary(ImageLibrary value)
        {
            library = value;
        }

        public void SetItems(IEnumerable<ImageRecord> records)
        {
            var updated = records.ToList();
            var keys = updated.Select(item => item.Id + "\n" + item.SourcePath + "\n" +
                item.LastWriteUtc.Ticks + "\n" + item.IsAvailable + "\n" + item.RopePinned).ToList();
            if (library != null)
            {
                keys.AddRange(library.Collections.Select(group => group.Id + "\n" + group.Name + "\n" +
                    string.Join(",", group.ImageIds)));
                keys.AddRange(library.Items.Select(item => item.Id + "\n" + item.SourcePath + "\n" +
                    item.LastWriteUtc.Ticks + "\n" + item.IsAvailable + "\n" + item.RopePinned));
            }
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
            var groups = items.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Key).ToArray();
            var collections = library == null ? new ImageCollectionState[0] : library.Collections.ToArray();
            if (selectedFolder != null && !collections.Any(group => CollectionPrefix + group.Id == selectedFolder) &&
                !groups.Any(key => string.Equals(key, selectedFolder, StringComparison.OrdinalIgnoreCase)))
                selectedFolder = null;
            if (!showingOverview && selectedFolder == null) ShowOverview();
        }

        void ShowOverview()
        {
            CloseMenu();
            showingOverview = true;
            selectedFolder = null;
            importCollection.IsEnabled = renameCollection.IsEnabled = deleteCollection.IsEnabled = false;
            nameEditor.Children.Clear();
            search.Clear();
            headingText.Text = "收纳篮";
            frame.Background = Brushes.Transparent;
            frame.BorderThickness = new Thickness(0);
            searchRow.MaxWidth = 440;
            searchRow.HorizontalAlignment = HorizontalAlignment.Center;
            searchRow.Width = Math.Max(200, Math.Min(440, Width - 50));
            overview.Visibility = Visibility.Visible;
            detailPane.Visibility = Visibility.Collapsed;
            RenderSocks();
        }

        void OpenFolder(string key)
        {
            showingOverview = false;
            selectedFolder = key;
            nameEditor.Children.Clear();
            search.Clear();
            overview.Visibility = Visibility.Collapsed;
            detailPane.Visibility = Visibility.Visible;
            headingText.Text = "袜夹";
            frame.Background = Brush("#FFFFFF");
            frame.BorderBrush = Brush("#CED8DF");
            frame.BorderThickness = new Thickness(1);
            searchRow.MaxWidth = double.PositiveInfinity;
            searchRow.Width = double.NaN;
            searchRow.HorizontalAlignment = HorizontalAlignment.Stretch;
            RenderPictures();
        }

        void RenderSocks()
        {
            string query = search.Text.Trim();
            bool resetPage = query != basketQuery;
            basketQuery = query;
            basketScene.Reset(query.Length > 0);
            int total = 0;
            int visible = 0;
            var collections = library == null ? new ImageCollectionState[0] : library.Collections.ToArray();
            foreach (var group in collections)
            {
                total++;
                var members = library.Items.Where(item => group.ImageIds.Contains(item.Id)).ToArray();
                if (!MatchesSock(group.Name, "", members, query)) continue;
                AddSockTile(group.Name, CollectionPrefix + group.Id, members.Length, "自建", group.Name);
                visible++;
            }
            var groups = items.GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Max(item => item.AddedUtc > item.LastWriteUtc ?
                    item.AddedUtc : item.LastWriteUtc)).ToArray();
            foreach (var group in groups)
            {
                total++;
                string name = group.Key == "#temporary" ? "本次临时图片" :
                    Path.GetFileName(group.Key.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name)) name = group.Key;
                if (!MatchesSock(name, group.Key, group, query)) continue;
                AddSockTile(name, group.Key, group.Count(), "原目录", group.Key);
                visible++;
            }
            titleCount.Text = total + " 个袜夹";
            resultCount.Text = query.Length == 0 ? "" : visible + " / " + total;
            basketScene.Finish(resetPage);
        }

        static bool MatchesSock(string name, string path, IEnumerable<ImageRecord> members, string query)
        {
            return query.Length == 0 || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                members.Any(item => item.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.SourcePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        void AddSockTile(string name, string key, int count, string kind, string hint)
        {
            basketScene.Add(name, key, count, kind, hint, delegate { OpenFolder(key); });
        }

        static string GroupKey(ImageRecord item)
        {
            return item.IsTransient ? "#temporary" : item.Folder ?? "";
        }

        void RenderPictures()
        {
            if (showingOverview) { RenderSocks(); return; }
            if (activeMenu != null) activeMenu.IsOpen = false;
            pictures.Children.Clear();
            shownCount = 0;
            string query = search.Text.Trim();
            var collection = SelectedCollection();
            collectionTitle.Text = collection != null ? collection.Name :
                selectedFolder == "#temporary" ? "本次临时图片" :
                selectedFolder == null ? "全部图片" :
                Path.GetFileName(selectedFolder.TrimEnd(Path.DirectorySeparatorChar));
            collectionTitle.ToolTip = collectionTitle.Text;
            titleCount.Text = "";
            importCollection.IsEnabled = renameCollection.IsEnabled = deleteCollection.IsEnabled = collection != null;
            IEnumerable<ImageRecord> source = collection == null ? items :
                library.Items.Where(item => collection.ImageIds.Contains(item.Id));
            filteredItems = source.Where(item =>
                (collection != null || selectedFolder == null ||
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
                    Text = query.Length > 0 ? "没有匹配的图片" :
                        collection == null ? "暂无图片" : "袜夹为空",
                    Foreground = Brush("#607786"),
                    Margin = new Thickness(20, 22, 0, 0),
                    FontSize = 12
                });
                if (collection != null && query.Length == 0)
                {
                    var import = new Button
                    {
                        Content = "导入图片", HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(20, 12, 0, 0), Padding = new Thickness(12, 6, 12, 6)
                    };
                    import.Click += delegate { ImportCollection(); };
                    pictures.Children.Add(import);
                }
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
                Text = !item.IsAvailable ? "原文件不可用" :
                    item.IsTransient ? "临时图片 · 关闭后消失" : item.Folder ?? "",
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
            bool doubleClick = false;
            bool cancelClick = false;
            button.Click += delegate
            {
                if (cancelClick) return;
                bool shouldOpen = doubleClick;
                doubleClick = false;
                lastClickedItem = shouldOpen ? null : item;
                if (shouldOpen) open(item);
                else copy(item);
            };
            button.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                doubleClick = args.ClickCount == 2 && lastClickedItem == item;
                cancelClick = false;
                dragStart = args.GetPosition(button);
                dragItem = item;
            };
            button.PreviewKeyDown += delegate
            {
                cancelClick = false;
                doubleClick = false;
                lastClickedItem = null;
            };
            button.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (dragItem != item || args.LeftButton != MouseButtonState.Pressed) return;
                var current = args.GetPosition(button);
                if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragItem = null;
                cancelClick = true;
                doubleClick = false;
                lastClickedItem = null;
                try { DragDrop.DoDragDrop(button, createData(item), DragDropEffects.Copy); }
                catch (IOException) { }
            };
            button.PreviewMouseLeftButtonUp += delegate { dragItem = null; };
            var menu = new ContextMenu();
            AddMenuItem(menu, "复制图片", delegate { copy(item); });
            AddMenuItem(menu, item.RopePinned == true ? "取消固定并收回袜夹" : "固定在绳上",
                delegate { pin(item); });
            if (item.RopePinned.HasValue)
                AddMenuItem(menu, "恢复自动分类", delegate { resetPlacement(item); });
            AddMenuItem(menu, "打开图片", delegate { open(item); });
            AddMenuItem(menu, "定位文件", delegate { locate(item); });
            AddMenuItem(menu, "保存副本", delegate { save(item); });
            if (library != null && !item.IsTransient)
            {
                var addTo = new MenuItem { Header = "添加到袜夹" };
                foreach (var group in library.Collections)
                {
                    string id = group.Id;
                    var option = new MenuItem { Header = group.Name, IsEnabled = !group.ImageIds.Contains(item.Id) };
                    option.Click += delegate
                    {
                        InvokeMenuAction(menu, delegate { library.AddToCollection(id, item); });
                    };
                    addTo.Items.Add(option);
                }
                var create = new MenuItem { Header = "新建袜夹…" };
                create.Click += delegate
                {
                    InvokeMenuAction(menu, delegate { EditCollection(null, item); });
                };
                addTo.Items.Add(create);
                menu.Items.Add(addTo);
                var selected = SelectedCollection();
                if (selected != null)
                    AddMenuItem(menu, "从此袜夹移出（保留原文件）",
                        delegate { library.RemoveFromCollection(selected.Id, item); });
            }
            menu.Items.Add(new Separator());
            AddMenuItem(menu, item.IsTransient ? "移除临时图片" : "删除原文件",
                delegate { delete(item); });
            menu.Opened += delegate
            {
                lastClickedItem = null;
                doubleClick = false;
                activeMenu = menu;
                IsContextMenuOpen = true;
            };
            menu.Closed += delegate
            {
                if (activeMenu != menu) return;
                activeMenu = null;
                IsContextMenuOpen = false;
                if (IsVisible && !shuttingDown && MenuDismissed != null) MenuDismissed();
            };
            button.ContextMenu = menu;
            return button;
        }

        void CloseMenu()
        {
            ContextMenu menu = activeMenu;
            activeMenu = null;
            IsContextMenuOpen = false;
            if (menu != null) menu.IsOpen = false;
        }

        void InvokeMenuAction(ContextMenu menu, Action action)
        {
            if (activeMenu == menu)
            {
                activeMenu = null;
                IsContextMenuOpen = false;
            }
            menu.IsOpen = false;
            action();
        }

        void AddMenuItem(ContextMenu menu, string label, Action action)
        {
            var option = new MenuItem { Header = label };
            option.Click += delegate { InvokeMenuAction(menu, action); };
            menu.Items.Add(option);
        }

        ImageCollectionState SelectedCollection()
        {
            return library == null ? null :
                library.Collections.FirstOrDefault(group => CollectionPrefix + group.Id == selectedFolder);
        }

        void EditCollection(ImageCollectionState collection)
        {
            EditCollection(collection, null);
        }

        void EditCollection(ImageCollectionState collection, ImageRecord initialItem)
        {
            if (library == null) return;
            nameEditor.Children.Clear();
            var name = new TextBox
            {
                Text = collection == null ? "" : collection.Name, MaxLength = 40,
                Padding = new Thickness(5), ToolTip = "袜夹名称", FontSize = 12
            };
            System.Windows.Automation.AutomationProperties.SetName(name, "袜夹名称");
            var errorText = new TextBlock
            {
                Foreground = Brush("#B42318"), FontSize = 11, TextWrapping = TextWrapping.Wrap
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            Action submit = delegate
            {
                try
                {
                    if (collection == null)
                    {
                        var created = library.CreateCollection(name.Text);
                        if (initialItem != null) library.AddToCollection(created.Id, initialItem);
                        selectedFolder = CollectionPrefix + created.Id;
                    }
                    else library.RenameCollection(collection.Id, name.Text);
                    nameEditor.Children.Clear();
                    RenderFolders();
                    OpenFolder(selectedFolder);
                }
                catch (ArgumentException error) { errorText.Text = error.Message; }
            };
            actions.Children.Add(IconButton("\uE73E", "保存袜夹名称", submit));
            actions.Children.Add(IconButton("\uE711", "取消", delegate { nameEditor.Children.Clear(); }));
            nameEditor.Children.Add(new TextBlock
            {
                Text = collection == null ? "新建袜夹" : "重命名", FontSize = 11,
                Margin = new Thickness(0, 0, 0, 5), Foreground = Brush("#526979")
            });
            nameEditor.Children.Add(name);
            nameEditor.Children.Add(errorText);
            nameEditor.Children.Add(actions);
            name.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter) { args.Handled = true; submit(); }
                if (args.Key == Key.Escape) { args.Handled = true; nameEditor.Children.Clear(); }
            };
            name.Focus();
            name.SelectAll();
        }

        void ImportCollection()
        {
            var collection = SelectedCollection();
            if (collection == null) return;
            interactionDepth++;
            try
            {
                var picker = new OpenFileDialog
                {
                    Title = "导入到袜夹：" + collection.Name, Multiselect = true,
                    Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp"
                };
                if (picker.ShowDialog(this) != true) return;
                var result = library.ImportToCollection(collection.Id, picker.FileNames);
                search.Clear();
                RenderFolders();
                RenderPictures();
                if (result.Failed > 0)
                    MessageBox.Show(this, "新增 " + result.Added + " 张，已存在 " + result.AlreadyPresent +
                        " 张，无法读取 " + result.Failed + " 张。请检查原文件是否存在或格式是否受支持。",
                        "导入结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { interactionDepth--; }
        }

        void DeleteCollection()
        {
            var collection = SelectedCollection();
            if (collection == null) return;
            interactionDepth++;
            try
            {
                if (MessageBox.Show(this, "删除袜夹“" + collection.Name + "”？\n仅移除分类，所有原文件都会保留。",
                    "删除袜夹", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
                library.DeleteCollection(collection.Id);
                ShowOverview();
            }
            finally { interactionDepth--; }
        }

        static Button IconButton(string glyph, string label, Action action)
        {
            var button = new Button
            {
                Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14, Width = 30, Height = 32, ToolTip = label,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Brush("#315A72"), Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += delegate { action(); };
            return button;
        }

        static Brush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
