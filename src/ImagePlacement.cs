using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FrameDock
{
    internal sealed class ImagePlacement
    {
        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        readonly string screenshots;
        readonly string downloads;

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

        public ImagePlacement(string screenshotsFolder)
        {
            screenshots = screenshotsFolder;
            downloads = GetDownloadsFolder();
        }

        public string DownloadsFolder { get { return downloads; } }

        public bool IsRecent(ImageRecord item)
        {
            return IsRecent(item, DateTime.UtcNow);
        }

        public bool IsRecent(ImageRecord item, DateTime utcNow)
        {
            if (!item.IsAvailable) return false;
            DateTime cutoff = utcNow.AddDays(-7);
            if (item.IsTransient) return item.AddedUtc >= cutoff;
            bool inScreenshots = IsInside(item.SourcePath, screenshots);
            if (inScreenshots &&
                !FileRules.SamePath(Path.GetDirectoryName(item.SourcePath), screenshots))
                return false;
            if (!inScreenshots && !IsInside(item.SourcePath, downloads))
                return false;
            return item.LastWriteUtc >= cutoff || item.AddedUtc >= cutoff;
        }

        public static bool IsInside(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
            try
            {
                string root = FileRules.Normalize(folder);
                if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    root += Path.DirectorySeparatorChar;
                return FileRules.Normalize(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
        }

        static string GetDownloadsFolder()
        {
            IntPtr pointer = IntPtr.Zero;
            try
            {
                Guid id = DownloadsId;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer) == 0)
                    return Marshal.PtrToStringUni(pointer);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
