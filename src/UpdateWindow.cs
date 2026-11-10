using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FrameDock
{
    internal sealed class UpdateWindow : Window
    {
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly TextBlock status = new TextBlock
        {
            Foreground = Brush("#65717F"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 8)
        };
        readonly ProgressBar progress = new ProgressBar
        {
            Height = 5,
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 16)
        };
        readonly Button install;
        readonly Button later;
        readonly AppUpdates updates;
        readonly UpdateRelease release;
        readonly Func<string, bool> apply;
        string downloaded;
        bool busy;
        bool handingOff;

        public UpdateWindow(
            UpdateRelease release,
            AppUpdates updates,
            Func<string, bool> apply)
        {
            this.release = release;
            this.updates = updates;
            this.apply = apply;
            Title = "PicShot 更新";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var body = new StackPanel { Margin = new Thickness(24) };
            body.Children.Add(new TextBlock
            {
                Text = "发现 PicShot 新版本",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#283343")
            });
            body.Children.Add(new TextBlock
            {
                Text = AppUpdates.CurrentVersion + "  →  " + release.Version,
                FontSize = 14,
                Foreground = Brush("#65717F"),
                Margin = new Thickness(0, 8, 0, 16)
            });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = String.IsNullOrWhiteSpace(release.Notes)
                        ? "新版本已准备好。"
                        : release.Notes,
                    FontSize = 13,
                    LineHeight = 22,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("#343D49")
                }
            });
            var releaseLink = new Hyperlink(new Run("查看完整更新说明"))
            {
                NavigateUri = new Uri(release.Url)
            };
            releaseLink.RequestNavigate += delegate(object sender, System.Windows.Navigation.RequestNavigateEventArgs args)
            {
                try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception error) { status.Text = "无法打开发布页：" + error.Message; }
                args.Handled = true;
            };
            var releaseText = new TextBlock { Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
            releaseText.Inlines.Add(releaseLink);
            body.Children.Add(releaseText);
            status.Text = "更新将重启程序，保留原图路径和设置；未另存的临时截图会消失。";
            body.Children.Add(status);
            body.Children.Add(progress);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            later = Button("稍后", delegate
            {
                Close();
            });
            later.Margin = new Thickness(0, 0, 8, 0);
            install = Button("下载并安装", StartInstall);
            actions.Children.Add(later);
            actions.Children.Add(install);
            body.Children.Add(actions);

            Content = new Border
            {
                Background = Brush("#FFFFFF"),
                BorderBrush = Brush("#D7DEE5"),
                BorderThickness = new Thickness(1),
                Child = body
            };
            Closed += delegate
            {
                cancellation.Cancel();
                if (!handingOff && downloaded != null)
                    AppUpdates.Discard(downloaded);
            };
        }

        async void StartInstall(object sender, RoutedEventArgs args)
        {
            if (busy)
                return;
            busy = true;
            install.IsEnabled = false;
            try
            {
                if (downloaded == null)
                {
                    progress.Visibility = Visibility.Visible;
                    later.Content = "取消";
                    status.Text = "正在下载更新…";
                    downloaded = await updates.DownloadAsync(
                        release,
                        new Progress<int>(delegate(int value)
                        {
                            progress.Value = value;
                            status.Text = value < 100
                                ? "正在下载更新 " + value + "%"
                                : "正在校验安装包…";
                        }),
                        cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                later.IsEnabled = false;
                status.Text = "正在准备重启…";
                handingOff = true;
                if (apply(downloaded))
                {
                    Close();
                }
                else
                {
                    handingOff = false;
                    later.IsEnabled = true;
                    status.Text = "当前有内容尚未保存，更新暂未安装。";
                }
            }
            catch (OperationCanceledException)
            {
                if (IsVisible)
                    status.Text = "下载已取消。";
            }
            catch (Exception error)
            {
                status.Text = "更新未完成：" + error.Message;
                install.Content = downloaded == null ? "重试下载" : "重试安装";
            }
            finally
            {
                busy = false;
                install.IsEnabled = true;
                if (!handingOff)
                    later.Content = "稍后";
            }
        }

        static Button Button(string text, RoutedEventHandler handler)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 84,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 4, 0, 0)
            };
            button.Click += handler;
            return button;
        }

        static Brush Brush(string value)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
            brush.Freeze();
            return brush;
        }
    }
}
