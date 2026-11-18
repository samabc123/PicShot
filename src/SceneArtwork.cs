using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameDock
{
    internal static class SceneArtwork
    {
        internal static readonly BitmapSource BasketIcon = Load("basket-icon");

        internal static BitmapSource Load(string name)
        {
            using (var stream = typeof(SceneArtwork).Assembly.GetManifestResourceStream("Artwork." + name + ".png"))
            {
                if (stream == null) throw new InvalidOperationException("Missing embedded artwork: " + name);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
                return image;
            }
        }

        // Use the rendered silhouette so woven edges and transparent spaces pick exactly what they show.
        internal sealed class Mask
        {
            readonly byte[] pixels;
            readonly int width;
            readonly int height;

            internal Mask(BitmapSource image)
            {
                width = image.PixelWidth;
                height = image.PixelHeight;
                var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
            }

            internal bool Contains(Point position, double displayWidth, double displayHeight)
            {
                if (position.X < 0 || position.Y < 0 || position.X >= displayWidth || position.Y >= displayHeight)
                    return false;
                int x = (int)(position.X * width / displayWidth);
                int y = (int)(position.Y * height / displayHeight);
                return pixels[(y * width + x) * 4 + 3] > 32;
            }
        }
    }
}
