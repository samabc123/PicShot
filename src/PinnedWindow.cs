using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameDock
{
    internal sealed class PinnedWindow : Window
    {
        readonly Image image = new Image { Stretch = Stretch.Uniform };
        readonly ImageRecord record;
        Point dragOrigin;

        public PinnedWindow(ImageRecord record)
        {
            this.record = record;
            Title = record.Name;
            Width = 420;
            Height = 320;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 31, 38));
            image.Source = record.Thumbnail;
            var grid = new Grid();
            grid.Children.Add(image);
            var close = new Button { Content = "×", Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), FontSize = 18 };
            close.Click += delegate { Close(); };
            grid.Children.Add(close);
            Content = grid;
            MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                dragOrigin = args.GetPosition(this);
                if (args.ClickCount == 2) Close();
                else DragMove();
            };
        }
    }
}
