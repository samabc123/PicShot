using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace FrameDock
{
    // A fixed logical stage keeps overlapping objects stable across DPI and window sizes.
    internal sealed class BasketScene : Grid
    {
        internal const int PageSize = 15;
        static readonly DrawingImage Back = BasketArtwork(false);
        static readonly DrawingImage Front = BasketArtwork(true);
        static readonly string[] Accents = { "#6F9EA8", "#D99186", "#91AB87", "#A197BC", "#C7AC73", "#789DC2" };
        static readonly DrawingImage[] Socks = Accents.Select(SockArtwork).ToArray();
        static readonly double[,] Poses = {
            { 157, 116, -54 }, { 269, 82, 39 }, { 386, 111, -67 }, { 498, 100, 55 }, { 576, 148, -27 },
            { 123, 192, 63 }, { 252, 168, -82 }, { 357, 181, 32 }, { 465, 195, -43 }, { 580, 227, 64 },
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
            var content = new Grid { IsHitTestVisible = false, RenderTransform = new TranslateTransform() };
            content.Children.Add(new Image
            {
                Source = Socks[seed % (uint)Socks.Length], Stretch = Stretch.Fill,
                IsHitTestVisible = false
            });
            // Keep the name in the semantic tree; the shared caption displays it without pile clutter.
            content.Children.Add(new TextBlock { Text = name, Visibility = Visibility.Collapsed });
            var button = new SockObject
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
            static readonly Geometry Outline = Geometry.Parse(
                "M24,8 L58,8 58,23 55,23 54,42 C55,51 50,55 44,59 L23,72 C15,76 5,74 4,67 C2,61 6,56 12,52 L23,44 C26,42 26,38 26,34 L26,23 24,23 Z");
            protected override HitTestResult HitTestCore(PointHitTestParameters hit)
            {
                var p = new Point(hit.HitPoint.X * 64 / Width, hit.HitPoint.Y * 80 / Height);
                var content = Content as FrameworkElement;
                double lift = content == null ? 0 : content.RenderTransform.Value.OffsetY * 80 / Height;
                var lifted = new Point(p.X, p.Y - lift);
                return Outline.FillContains(p) || Outline.FillContains(lifted)
                    ? new PointHitTestResult(this, hit.HitPoint) : null;
            }
            protected override void OnRender(DrawingContext drawing)
            {
                base.OnRender(drawing);
                // Retain the original hover footprint while only the artwork lifts.
                drawing.PushTransform(new ScaleTransform(Width / 64, Height / 80));
                drawing.DrawGeometry(Brushes.Transparent, null, Outline);
                drawing.Pop();
            }
        }

        sealed class BasketLip : FrameworkElement
        {
            static readonly Geometry Body = Geometry.Parse(
                "M106,255 C157,310 265,346 400,346 C535,346 643,310 694,255 L646,411 C619,445 520,465 400,465 C280,465 181,445 154,411 Z");
            static readonly Geometry Rim = Geometry.Parse(
                "M106,255 C157,310 265,346 400,346 C535,346 643,310 694,255");
            protected override void OnRender(DrawingContext drawing)
            {
                drawing.DrawImage(Front, new Rect(0, 0, 800, 480));
            }
            protected override HitTestResult HitTestCore(PointHitTestParameters hit)
            {
                return Body.FillContains(hit.HitPoint) || Rim.StrokeContains(new Pen(Brushes.Black, 15), hit.HitPoint)
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

        static Brush Shade(string start, string end, bool horizontal)
        {
            var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(start),
                (Color)ColorConverter.ConvertFromString(end), horizontal ? 0 : 90);
            brush.Freeze();
            return brush;
        }

        static void Shape(DrawingContext drawing, string path, Brush fill, string edge, double width)
        {
            drawing.DrawGeometry(fill, edge == null ? null : new Pen(Ink(edge), width), Geometry.Parse(path));
        }

        static DrawingImage SockArtwork(string accent)
        {
            var group = new DrawingGroup();
            using (var d = group.Open())
            {
                d.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 80));
                const string body = "M28,17 L55,17 L54,42 C55,51 50,55 44,59 L23,72 C15,76 5,74 4,67 C2,61 6,56 12,52 L23,44 C26,42 26,38 26,34 Z";
                Shape(d, body, Shade("#FCFDFD", "#CEDAD9", true), "#829899", 0.7);
                d.PushClip(Geometry.Parse(body));
                Shape(d, "M0,52 L14,50 C24,53 27,64 26,79 L0,79 Z", Ink(accent), null, 0);
                Shape(d, "M57,40 C46,39 41,47 44,59 L62,64 L62,40 Z", Ink(accent), null, 0);
                var stitches = new Pen(Ink("#8CA09E"), 0.55)
                {
                    DashStyle = new DashStyle(new double[] { 1.2, 2 }, 0)
                };
                d.DrawGeometry(null, stitches, Geometry.Parse("M14,51 C24,54 27,65 25,74 M55,41 C46,41 42,49 45,59"));
                for (int y = 27; y < 45; y += 4)
                    d.DrawLine(new Pen(Ink("#E3EBE7"), 0.55), new Point(30, y), new Point(50, y + 1));
                Shape(d, "M30,25 Q28,39 30,43", null, "#FFFFFF", 1.8);
                d.Pop();
                d.DrawRoundedRectangle(Ink(accent), new Pen(Ink("#778F90"), 0.65), new Rect(24, 8, 34, 15), 3, 3);
                d.DrawLine(new Pen(Ink("#F4FBF8"), 0.8), new Point(28, 10), new Point(54, 10));
                for (int x = 29; x <= 53; x += 4)
                    d.DrawLine(new Pen(Ink("#E6F1EB"), 0.6), new Point(x, 13), new Point(x, 20));
            }
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        static DrawingImage BasketArtwork(bool front)
        {
            var group = new DrawingGroup();
            using (var d = group.Open())
            {
                d.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 800, 480));
                if (!front)
                {
                    for (int i = 7; i > 0; i--)
                    {
                        d.PushOpacity(0.018);
                        d.DrawEllipse(Brushes.Black, null, new Point(405, 440), 225 + i * 9, 14 + i * 3);
                        d.Pop();
                    }
                    Shape(d, "M130,246 C126,86 218,61 260,175 M670,246 C674,86 582,61 540,175",
                        null, "#90A7A8", 13);
                    Shape(d, "M130,246 C126,86 218,61 260,175 M670,246 C674,86 582,61 540,175",
                        null, "#E3ECE9", 7);
                    d.DrawEllipse(Shade("#EBF2EE", "#A0B5B2", false),
                        new Pen(Ink("#819A98"), 1.5), new Point(400, 255), 297, 96);
                    d.DrawEllipse(Shade("#647F7C", "#C3D0C5", false), null, new Point(400, 255), 280, 80);
                    d.DrawEllipse(null, new Pen(Ink("#F6FCF5"), 3), new Point(400, 252), 290, 91);
                }
                else
                {
                    const string body = "M106,255 C157,310 265,346 400,346 C535,346 643,310 694,255 L646,411 C619,445 520,465 400,465 C280,465 181,445 154,411 Z";
                    Shape(d, body, Shade("#EBF2EE", "#A1B7B3", false), "#839E99", 1.2);
                    d.PushClip(Geometry.Parse(body));
                    for (int x = 100; x < 720; x += 20)
                    {
                        double end = 400 + (x - 400) * 0.81;
                        Shape(d, "M" + x + ",270 L" + end.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",468",
                            null, "#8EA9A2", 5);
                        Shape(d, "M" + (x + 3) + ",270 L" + (end + 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",468",
                            null, "#E4EFE7", 3);
                    }
                    for (int y = 296; y < 480; y += 17)
                    {
                        var geometry = Geometry.Parse("M100," + y + " Q400," + (y + 85) + " 700," + y);
                        d.DrawGeometry(null, new Pen(Ink("#8CA69D"), 5), geometry);
                        d.PushTransform(new TranslateTransform(0, -2));
                        d.DrawGeometry(null, new Pen(Ink("#E8F0E7"), 4), geometry);
                        d.Pop();
                    }
                    d.Pop();
                    Shape(d, "M106,255 C157,310 265,346 400,346 C535,346 643,310 694,255",
                        null, "#91AAA3", 15);
                    Shape(d, "M106,253 C157,308 265,344 400,344 C535,344 643,308 694,253",
                        null, "#E4EEE6", 9);
                    Shape(d, "M111,252 C164,306 267,340 400,340 C533,340 636,306 689,252",
                        null, "#FAFCF7", 2.5);
                    d.DrawRoundedRectangle(Shade("#F9FCF7", "#E2EAE0", false),
                        new Pen(Ink("#93AAA0"), 1), new Rect(358, 387, 84, 43), 5, 5);
                    for (int y = 401; y < 420; y += 9)
                        d.DrawLine(new Pen(Ink("#A4B7AB"), 2), new Point(380, y), new Point(420, y));
                }
            }
            var result = new DrawingImage(group);
            result.Freeze();
            return result;
        }
    }
}
