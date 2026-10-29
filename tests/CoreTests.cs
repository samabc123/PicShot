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
                File.WriteAllBytes(image, new byte[] { 1, 2, 3 });
                if (!FileRules.IsImage(image)) throw new Exception("png extension was not recognized");
                if (!FileRules.SamePath(image, Path.Combine(root, ".", "sample.png"))) throw new Exception("path normalization failed");
                if (!FileRules.IsInside(image, root)) throw new Exception("directory containment failed");
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
