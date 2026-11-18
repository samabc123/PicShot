using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public ImageRecord[] SelectForRope(IEnumerable<ImageRecord> records, int capacity, DateTime utcNow)
        {
            var ordered = records.Where(item => item.IsAvailable)
                .OrderByDescending(item => item.AddedUtc > item.LastWriteUtc ? item.AddedUtc : item.LastWriteUtc)
                .ThenByDescending(item => item.AddedUtc)
                .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var pinned = ordered.Where(item => item.RopePinned == true).ToArray();
            int remaining = Math.Max(0, capacity - pinned.Length);
            // Overflow remains automatic: it can fill a vacancy until it expires.
            var automatic = ordered.Where(item => !item.RopePinned.HasValue && IsRecent(item, utcNow))
                .Take(remaining);
            return pinned.Concat(automatic).ToArray();
        }

        internal static void ApplyRopeOrder(ImageRecord[] selected, IEnumerable<string> order)
        {
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string id in order)
                if (!ranks.ContainsKey(id)) ranks.Add(id, ranks.Count);
            if (ranks.Count < 2) return;

            // Reorder only the remembered entries. New arrivals keep their default slots.
            var indices = Enumerable.Range(0, selected.Length)
                .Where(index => ranks.ContainsKey(selected[index].Id)).ToArray();
            var sorted = indices.Select(index => selected[index]).OrderBy(item => ranks[item.Id]).ToArray();
            for (int index = 0; index < indices.Length; index++)
                selected[indices[index]] = sorted[index];
        }

        public bool IsRecent(ImageRecord item)
        {
            return IsRecent(item, DateTime.UtcNow);
        }

        public bool IsRecent(ImageRecord item, DateTime utcNow)
        {
            if (!item.IsAvailable) return false;
            if (item.RopePinned.HasValue) return item.RopePinned.Value;
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
