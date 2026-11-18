using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace FrameDock
{
    // A fixed logical stage keeps overlapping objects stable across DPI and window sizes.
    internal sealed class BasketScene : Grid
    {
        internal const int PageSize = 15;
        static readonly ImageSource Back = SceneArtwork.Load("basket-back");
        static readonly BitmapSource Front = SceneArtwork.Load("basket-front");
        static readonly BitmapSource[] Socks = Enumerable.Range(0, 6)
            .Select(i => SceneArtwork.Load("sock-" + i)).ToArray();
        static readonly SceneArtwork.Mask[] SockMasks = Socks.Select(image => new SceneArtwork.Mask(image)).ToArray();
        static readonly SceneArtwork.Mask FrontMask = new SceneArtwork.Mask(Front);
        static readonly double[,] Poses = {
            { 157, 116, -54 }, { 269, 82, 39 }, { 386, 111, -67 }, { 498, 100, 55 }, { 576, 148, -27 },
            { 123, 192, 63 }, { 252, 168, -82 }, { 357, 181, 32 }, { 465, 195, -43 }, { 552, 216, 64 },
            { 177, 250, -32 }, { 288, 252, 76 }, { 403, 247, -62 }, { 504, 270, 32 }, { 357, 284, 99 }
        };
        readonly Canvas stage = new Canvas { Width = 800, Height = 480 };
        internal readonly Canvas Items = new Canvas { Width = 800, Height = 480 };
        readonly List<Button> entries = new List<Button>();
        readonly TextBlock selectedName = new TextBlock();
        readonly TextBlock selectedInfo = new TextBlock();
        readonly TextBlock pageLabel = new TextBlock();
        readonly Button previous;
        readonly Button next;
        readonly Button openSelection;
        readonly Border caption;
        readonly TextBlock empty = new TextBlock();
        Button selected;
        int page;
        bool filtering;

        internal BasketScene()
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            var view = new Viewbox { Stretch = Stretch.Uniform, Child = stage };
            stage.Children.Add(new Image { Source = Back, Width = 800, Height = 480, IsHitTestVisible = false });
            stage.Children.Add(Items);
            // Basket lips share the objects' parent so a selected sock can lift above them.
            Items.Children.Add(new BasketLip { Width = 800, Height = 480 });
            Panel.SetZIndex(Items.Children[0], 80);
            Children.Add(view);

            var label = new Grid { Margin = new Thickness(18, 8, 14, 8) };
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            label.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            selectedName.FontSize = 16;
            selectedName.FontWeight = FontWeights.SemiBold;
            selectedName.Foreground = Ink("#283D40");
            selectedName.TextTrimming = TextTrimming.CharacterEllipsis;
            selectedInfo.FontSize = 11;
            selectedInfo.Foreground = Ink("#526B70");
            selectedInfo.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(selectedName);
            text.Children.Add(selectedInfo);
            label.Children.Add(text);
            openSelection = Tool("\uE76C", "打开选中的袜夹", delegate
            {
                if (selected != null) selected.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            openSelection.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(openSelection, 1);
            label.Children.Add(openSelection);
            caption = new Border
            {
                Child = label, Background = Ink("#F8FCFC"), CornerRadius = new CornerRadius(6),
                MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 240
            };
            Grid.SetRow(caption, 1);
            Children.Add(caption);

            var pages = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center
            };
            previous = Tool("\uE76B", "上一篮袜夹", delegate { ChangePage(page - 1); });
            next = Tool("\uE76C", "下一篮袜夹", delegate { ChangePage(page + 1); });
            pageLabel.FontSize = 11;
            pageLabel.Foreground = Ink("#3C555A");
            pageLabel.Background = Ink("#F4F9F9");
            pageLabel.Padding = new Thickness(0, 8, 0, 8);
            pageLabel.MinWidth = 60;
            pageLabel.TextAlignment = TextAlignment.Center;
            pageLabel.VerticalAlignment = VerticalAlignment.Center;
            pages.Children.Add(previous);
            pages.Children.Add(pageLabel);
            pages.Children.Add(next);
            Grid.SetRow(pages, 2);
            Children.Add(pages);
            empty.FontSize = 14;
            empty.Foreground = Ink("#3C555A");
            empty.TextAlignment = TextAlignment.Center;
            empty.Width = 400;
            empty.IsHitTestVisible = false;
            Canvas.SetLeft(empty, 200);
            Canvas.SetTop(empty, 215);
            Panel.SetZIndex(empty, 90);
            Items.Children.Add(empty);
            PreviewKeyDown += Navigate;
            Reset(false);
        }

        internal void Reset(bool hasQuery)
        {
            foreach (var button in entries) Items.Children.Remove(button);
            entries.Clear();
            selected = null;
            filtering = hasQuery;
            // Preserve the current page during live library refreshes; Finish clamps it.
        }

        internal void Add(string name, string key, int count, string kind, string hint, Action open)
        {
            uint seed = StableSeed(key);
            int appearance = (int)(seed % (uint)Socks.Length);
            var content = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
            content.Children.Add(new Image
            {
                Source = Socks[appearance], Stretch = Stretch.Fill,
                IsHitTestVisible = false
            });
            // Keep the name in the semantic tree; the shared caption displays it without pile clutter.
            content.Children.Add(new TextBlock { Text = name, Visibility = Visibility.Collapsed });
            var button = new SockObject(SockMasks[appearance])
            {
                Width = 104, Height = 134, Content = content, Tag = key,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Cursor = Cursors.Hand, ToolTip = name + "\n" + count + " 张 · " + kind + "\n" + hint,
                FocusVisualStyle = null,
                Effect = new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#30494D"), BlurRadius = 12,
                    ShadowDepth = 5, Direction = 285, Opacity = 0.26
                },
                Template = ObjectTemplate()
            };
            System.Windows.Automation.AutomationProperties.SetName(button, name + "，" + count + " 张，" + kind);
            button.Click += delegate { Select(button, name, count, kind); open(); };
            button.MouseEnter += delegate { Select(button, name, count, kind); Lift(button, true); };
            button.MouseLeave += delegate { if (!button.IsKeyboardFocused) Lift(button, false); };
            button.GotKeyboardFocus += delegate { Select(button, name, count, kind); Lift(button, true); };
            button.LostKeyboardFocus += delegate { if (!button.IsMouseOver) Lift(button, false); };
            entries.Add(button);
            Items.Children.Add(button);
        }

        internal void Finish(bool resetPage)
        {
            if (resetPage) page = 0;
            page = Math.Max(0, Math.Min(page, Math.Max(0, (entries.Count - 1) / PageSize)));
            LayoutObjects();
        }

        internal void ChangePage(int value)
        {
            if (value < 0 || value > Math.Max(0, (entries.Count - 1) / PageSize)) return;
            page = value;
            selected = null;
            LayoutObjects();
        }

        void LayoutObjects()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var button = entries[i];
                var lift = (TranslateTransform)((FrameworkElement)button.Content).RenderTransform;
                lift.BeginAnimation(TranslateTransform.YProperty, null);
                lift.Y = 0;
                bool visible = i / PageSize == page;
                button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible) continue;
                uint seed = StableSeed((string)button.Tag);
                int slot = i % PageSize;
                int row = slot / 5;
                double angle = Poses[slot, 2] + (int)(seed % 17) - 8;
                double size = 0.83 + row * 0.085;
                double x = Poses[slot, 0] + (int)((seed >> 6) % 13) - 6;
                double y = Poses[slot, 1] + (int)((seed >> 12) % 13) - 6;
                Canvas.SetLeft(button, x);
                Canvas.SetTop(button, y);
                button.RenderTransformOrigin = new Point(0.5, 0.55);
                var transforms = new TransformGroup();
                transforms.Children.Add(new ScaleTransform(size, size));
                transforms.Children.Add(new RotateTransform(angle));
                transforms.Children.Add(new TranslateTransform());
                button.RenderTransform = transforms;
                Panel.SetZIndex(button, 10 + slot);
            }
            int pages = Math.Max(1, (entries.Count + PageSize - 1) / PageSize);
            pageLabel.Text = pages > 1 ? (page + 1) + " / " + pages : "";
            pageLabel.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            previous.Visibility = next.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            previous.IsEnabled = page > 0;
            next.IsEnabled = page + 1 < pages;
            empty.Text = entries.Count == 0 ? filtering ? "没有匹配的袜夹" : "篮子还是空的" : "";
            selectedName.Text = entries.Count == 0 ? empty.Text : "收纳篮";
            selectedInfo.Text = entries.Count == 0 ? "0 个袜夹" : entries.Count + " 个袜夹";
            caption.ToolTip = null;
            openSelection.IsEnabled = false;
        }

        void Select(Button button, string name, int count, string kind)
        {
            selected = button;
            selectedName.Text = name;
            selectedInfo.Text = count + " 张 · " + kind;
            caption.ToolTip = button.ToolTip;
            openSelection.IsEnabled = true;
        }

        void Lift(Button button, bool raised)
        {
            var content = button.Content as FrameworkElement;
            if (content == null) return;
            var translate = content.RenderTransform as TranslateTransform;
            if (translate == null) return;
            int index = entries.IndexOf(button) % PageSize;
            Panel.SetZIndex(button, raised ? 100 : 10 + index);
            double target = raised ? -24 : 0;
            if (!SystemParameters.ClientAreaAnimation)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = target;
                return;
            }
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        void Navigate(object sender, KeyEventArgs args)
        {
            if (args.Key != Key.Left && args.Key != Key.Right && args.Key != Key.Up && args.Key != Key.Down) return;
            var current = Keyboard.FocusedElement as Button;
            int index = entries.IndexOf(current);
            if (index < 0) return;
            int offset = args.Key == Key.Left ? -1 : args.Key == Key.Right ? 1 : args.Key == Key.Up ? -5 : 5;
            int target = Math.Max(0, Math.Min(entries.Count - 1, index + offset));
            if (target / PageSize != page) ChangePage(target / PageSize);
            entries[target].Focus();
            args.Handled = true;
        }

        static ControlTemplate ObjectTemplate()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        }

        sealed class SockObject : Button
        {
            readonly SceneArtwork.Mask mask;

            internal SockObject(SceneArtwork.Mask mask) { this.mask = mask; }

            protected override HitTestResult HitTestCore(PointHitTestParameters hit)
            {
                var content = Content as FrameworkElement;
                double lift = content == null ? 0 : content.RenderTransform.Value.OffsetY;
                var p = hit.HitPoint;
                return mask.Contains(p, Width, Height) ||
                    mask.Contains(new Point(p.X, p.Y - lift), Width, Height)
                    ? new PointHitTestResult(this, hit.HitPoint) : null;
            }
            protected override void OnRender(DrawingContext drawing)
            {
                base.OnRender(drawing);
                // Retain the original hover footprint while only the artwork lifts.
                drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Width, Height));
            }
        }

        sealed class BasketLip : FrameworkElement
        {
            protected override void OnRender(DrawingContext drawing)
            {
                drawing.DrawImage(Front, new Rect(0, 0, 800, 480));
            }
            protected override HitTestResult HitTestCore(PointHitTestParameters hit)
            {
                return FrontMask.Contains(hit.HitPoint, Width, Height)
                    ? new PointHitTestResult(this, hit.HitPoint) : null;
            }
        }

        static uint StableSeed(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value) { hash ^= c; hash *= 16777619; }
                return hash;
            }
        }

        static Button Tool(string glyph, string label, Action action)
        {
            var button = new Button
            {
                Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Width = 34, Height = 32, FontSize = 14, Background = Ink("#F4F9F9"),
                BorderThickness = new Thickness(0), Foreground = Ink("#344F54"),
                Cursor = Cursors.Hand, ToolTip = label
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.Click += delegate { action(); };
            return button;
        }

        static Brush Ink(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

    }
}
