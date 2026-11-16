using System.Windows;
using System.Windows.Media;

namespace FrameDock
{
    internal static class CollectionArtwork
    {
        public static readonly DrawingImage Sock = CreateSock();
        public static readonly DrawingImage Basket = CreateBasket();

        static Brush Color(string hex)
        {
            return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
        }

        static void Shape(DrawingContext drawing, string path, string fill, string stroke, double thickness)
        {
            drawing.DrawGeometry(fill == null ? null : Color(fill),
                stroke == null ? null : new Pen(Color(stroke), thickness), Geometry.Parse(path));
        }

        static DrawingImage CreateSock()
        {
            var group = new DrawingGroup();
            using (var drawing = group.Open())
            {
                drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 80));
                const string body = "M28,17 L55,17 L54,42 C55,51 50,55 44,59 L23,72 C15,76 5,74 4,67 C2,61 6,56 12,52 L23,44 C26,42 26,38 26,34 Z";
                Shape(drawing, body, "#F6F9FB", "#8097A9", 1);
                drawing.PushClip(Geometry.Parse(body));
                Shape(drawing, "M0,52 L14,50 C24,53 27,64 26,79 L0,79 Z", "#BFD3E0", null, 0);
                Shape(drawing, "M57,40 C46,39 41,47 44,59 L62,64 L62,40 Z", "#D2E1EA", null, 0);
                var stitches = new Pen(Color("#97B0C2"), 0.8)
                {
                    DashStyle = new DashStyle(new double[] { 1.5, 2.5 }, 0)
                };
                drawing.DrawGeometry(null, stitches, Geometry.Parse(
                    "M14,51 C24,54 27,65 25,74 M55,41 C46,41 42,49 45,59"));
                drawing.DrawLine(new Pen(Color("#FFFFFF"), 1.4), new Point(29, 25), new Point(28, 39));
                drawing.Pop();
                drawing.DrawRoundedRectangle(Color("#DEEAF2"), new Pen(Color("#8BA4B7"), 0.9),
                    new Rect(24, 8, 34, 15), 3, 3);
                drawing.DrawLine(new Pen(Color("#FAFDFF"), 1), new Point(28, 10), new Point(54, 10));
                for (int x = 29; x <= 53; x += 4)
                    drawing.DrawLine(new Pen(Color("#A8BFCF"), 0.8), new Point(x, 13), new Point(x, 20));
            }
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        static DrawingImage CreateBasket()
        {
            var group = new DrawingGroup();
            using (var drawing = group.Open())
            {
                drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 72, 82));
                Shape(drawing, "M16,40 C16,11 56,11 56,40", null, "#8BA4B7", 3.5);
                Shape(drawing, "M16,39 C16,13 56,13 56,39", null, "#E4EEF4", 1.5);
                drawing.PushTransform(new TranslateTransform(6, 24));
                drawing.PushTransform(new RotateTransform(-18, 14, 20));
                drawing.DrawImage(Sock, new Rect(0, 0, 29, 36));
                drawing.Pop();
                drawing.Pop();
                drawing.PushTransform(new RotateTransform(15, 46, 48));
                drawing.DrawImage(Sock, new Rect(34, 27, 30, 38));
                drawing.Pop();
                const string body = "M6,43 L66,43 L61,71 Q60,76 55,76 L17,76 Q12,76 11,71 Z";
                Shape(drawing, body, "#DEE9EF", "#8097A9", 1.1);
                drawing.PushClip(Geometry.Parse(body));
                var fiber = new Pen(Color("#B4C9D5"), 1.1);
                var light = new Pen(Color("#F5F9FB"), 1);
                for (int y = 50; y < 76; y += 6)
                {
                    drawing.DrawLine(fiber, new Point(8, y), new Point(64, y));
                    drawing.DrawLine(light, new Point(8, y + 1.5), new Point(64, y + 1.5));
                }
                for (int x = 15; x < 65; x += 8)
                    drawing.DrawLine(fiber, new Point(x, 44), new Point(x - (x - 36) * 0.16, 77));
                drawing.Pop();
                drawing.DrawRoundedRectangle(Color("#EDF4F8"), new Pen(Color("#8BA4B7"), 1),
                    new Rect(4, 40, 64, 7), 3, 3);
                drawing.DrawLine(new Pen(Color("#FFFFFF"), 1), new Point(9, 42), new Point(63, 42));
                drawing.DrawRoundedRectangle(Color("#F9FCFD"), new Pen(Color("#AEC3D0"), 0.8),
                    new Rect(25, 54, 22, 14), 2, 2);
                drawing.DrawLine(new Pen(Color("#B1C5D0"), 1), new Point(30, 59), new Point(42, 59));
                drawing.DrawLine(new Pen(Color("#B1C5D0"), 1), new Point(32, 63), new Point(40, 63));
            }
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }
    }
}
