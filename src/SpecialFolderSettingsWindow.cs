using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace FrameDock
{
    internal sealed class SpecialFolderSettingsWindow : Window
    {
        sealed class FolderRow
        {
            public string Name;
            public string Folder;
            public bool Decided;
            public bool Changed;
            public TextBlock PathText;
            public TextBlock StatusText;
        }

        readonly bool initial;
        readonly FolderRow weChat;
        readonly FolderRow qq;

        public string WeChatFolder { get { return weChat.Folder; } }
        public bool WeChatPathDecided { get { return weChat.Decided; } }
        public bool WeChatChanged { get { return weChat.Changed; } }
        public string QqFolder { get { return qq.Folder; } }
        public bool QqPathDecided { get { return qq.Decided; } }
        public bool QqChanged { get { return qq.Changed; } }

        public SpecialFolderSettingsWindow(
            bool initial,
            string weChatFolder,
            bool weChatPathDecided,
            string qqFolder,
            bool qqPathDecided)
        {
            this.initial = initial;
            weChat = new FolderRow
            {
                Name = "微信",
                Folder = weChatFolder,
                Decided = weChatPathDecided
            };
            qq = new FolderRow
            {
                Name = "QQ",
                Folder = qqFolder,
                Decided = qqPathDecided
            };

            Title = initial ? "配置微信和 QQ 截图目录" : "微信和 QQ 截图目录设置";
            Width = 590;
            Height = 300;
            MinWidth = 590;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brush("#FFFFFF");

            var frame = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#CED8DF"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(22),
                SnapsToDevicePixels = true
            };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            frame.Child = layout;
            Content = frame;

            var heading = new StackPanel();
            heading.Children.Add(new TextBlock
            {
                Text = Title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#24343B")
            });
            heading.Children.Add(new TextBlock
            {
                Text = "只记录目录路径，不复制图片文件。",
                Margin = new Thickness(0, 5, 0, 0),
                FontSize = 12,
                Foreground = Brush("#6B7B82")
            });
            Grid.SetRow(heading, 0);
            layout.Children.Add(heading);

            var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            rows.Children.Add(CreateFolderRow(weChat));
            rows.Children.Add(CreateFolderRow(qq));
            Grid.SetRow(rows, 1);
            layout.Children.Add(rows);

            var close = new Button
            {
                Content = "完成",
                Width = 92,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Brush("#EAF4F1"),
                Foreground = Brush("#24574E"),
                BorderBrush = Brush("#BCD8D0"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            close.Click += delegate { Close(); };
            Grid.SetRow(close, 2);
            layout.Children.Add(close);

            Closed += delegate
            {
                if (!this.initial) return;
                if (!weChat.Decided) Skip(weChat);
                if (!qq.Decided) Skip(qq);
            };
        }

        UIElement CreateFolderRow(FolderRow row)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            var name = new TextBlock
            {
                Text = row.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#2D3D43"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            grid.Children.Add(name);

            var pathPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            row.PathText = new TextBlock
            {
                FontSize = 12,
                Foreground = Brush("#52636A"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.StatusText = new TextBlock
            {
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 11,
                Foreground = Brush("#84939A")
            };
            pathPanel.Children.Add(row.PathText);
            pathPanel.Children.Add(row.StatusText);
            Grid.SetColumn(pathPanel, 1);
            grid.Children.Add(pathPanel);

            var choose = new Button
            {
                Content = "选择文件夹",
                Height = 28,
                Margin = new Thickness(8, 0, 8, 0),
                Background = Brush("#F3F8F7"),
                Foreground = Brush("#2E6259"),
                BorderBrush = Brush("#C9DED8"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            choose.Click += delegate { Choose(row); };
            Grid.SetColumn(choose, 2);
            grid.Children.Add(choose);

            var clear = new Button
            {
                Content = initial ? "暂不配置" : "清除路径",
                Height = 28,
                Background = Brushes.Transparent,
                Foreground = Brush("#7A5E59"),
                BorderBrush = Brush("#D8C5C0"),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            clear.Click += delegate { Skip(row); };
            Grid.SetColumn(clear, 3);
            grid.Children.Add(clear);

            RefreshRow(row);
            return grid;
        }

        void Choose(FolderRow row)
        {
            using (var picker = new Forms.FolderBrowserDialog
            {
                Description = "选择 " + row.Name + " 截图所在的文件夹",
                SelectedPath = Directory.Exists(row.Folder) ? row.Folder : null
            })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                row.Folder = picker.SelectedPath;
                row.Decided = true;
                row.Changed = true;
                RefreshRow(row);
            }
        }

        void Skip(FolderRow row)
        {
            row.Folder = null;
            row.Decided = true;
            row.Changed = true;
            RefreshRow(row);
        }

        void RefreshRow(FolderRow row)
        {
            if (row.PathText == null) return;
            row.PathText.Text = string.IsNullOrWhiteSpace(row.Folder) ? "未提供目录" : row.Folder;
            row.StatusText.Text = row.Decided
                ? (string.IsNullOrWhiteSpace(row.Folder) ? "已选择暂不配置" : "已配置")
                : "尚未决定";
        }

        static SolidColorBrush Brush(string color)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(color);
        }
    }
}
