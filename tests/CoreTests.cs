using System;
using System.IO;

namespace FrameDock.Tests
{
    internal static class CoreTests
    {
        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "FrameDock-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string image = Path.Combine(root, "sample.png");
                using (var bitmap = new System.Drawing.Bitmap(8, 8))
                {
                    bitmap.SetPixel(0, 0, System.Drawing.Color.Red);
                    bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (!FileRules.IsImage(image)) throw new Exception("png extension was not recognized");
                if (!FileRules.SamePath(image, Path.Combine(root, ".", "sample.png"))) throw new Exception("path normalization failed");
                if (!FileRules.IsInside(image, root)) throw new Exception("directory containment failed");
                string duplicate = Path.Combine(root, "duplicate.png");
                File.Copy(image, duplicate);
                if (ImageLoader.Fingerprint(image) != ImageLoader.Fingerprint(duplicate))
                    throw new Exception("identical image fingerprint mismatch");
                Console.WriteLine("FrameDock core tests passed.");
                return 0;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
