using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FrameDock
{
    internal static class FileRules
    {
        static readonly string[] extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

        public static bool IsImage(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return full;
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static bool SamePath(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInside(string path, string directory)
        {
            string root = Normalize(directory);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;
            return Normalize(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ImageRecordState
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string LocalCopyPath { get; set; }
        public string AddedUtc { get; set; }
        public string LastWriteUtc { get; set; }
        public string Fingerprint { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class WatchFolderState
    {
        public string Path { get; set; }
        public bool Recursive { get; set; }
        public bool Enabled { get; set; }
        public bool? CopyToLibrary { get; set; }
    }

    internal sealed class SavedState
    {
        public bool CollectClipboard { get; set; }
        public bool CopyToLibrary { get; set; }
        public string ScreenshotsFolder { get; set; }
        public List<ImageRecordState> Images { get; set; }
        public List<WatchFolderState> WatchFolders { get; set; }

        public SavedState()
        {
            CollectClipboard = false;
            CopyToLibrary = true;
            Images = new List<ImageRecordState>();
            WatchFolders = new List<WatchFolderState>();
        }
    }

    internal sealed class StateStore
    {
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
        readonly object saveLock = new object();
        readonly string file;

        public readonly string Root;
        public readonly string LibraryFolder;
        public readonly SavedState State;

        public StateStore()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameDock", "data");
            LibraryFolder = Path.Combine(Root, "library");
            file = Path.Combine(Root, "state.json");
            State = Load();
        }

        SavedState Load()
        {
            SavedState value;
            if (TryLoad(file, out value)) return value;
            if (File.Exists(file))
            {
                string backup = file + ".bak";
                if (TryLoad(backup, out value)) return value;
                try { File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            }
            return new SavedState();
        }

        bool TryLoad(string candidate, out SavedState value)
        {
            value = null;
            try
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length > 10 * 1024 * 1024) return false;
                value = serializer.Deserialize<SavedState>(File.ReadAllText(candidate, Encoding.UTF8));
                if (value == null || value.Images == null || value.WatchFolders == null) return false;
                value.Images = value.Images.Where(IsValidImageState).Take(100000).ToList();
                foreach (var image in value.Images)
                    if (string.IsNullOrWhiteSpace(image.LocalCopyPath) || !FileRules.IsInside(image.LocalCopyPath, LibraryFolder))
                        image.LocalCopyPath = null;
                value.WatchFolders = value.WatchFolders.Where(IsValidWatchState).Take(256).ToList();
                return true;
            }
            catch { value = null; return false; }
        }

        static bool IsValidImageState(ImageRecordState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Id) || string.IsNullOrWhiteSpace(state.Path)) return false;
            try
            {
                Path.GetFullPath(state.Path);
                if (!string.IsNullOrWhiteSpace(state.LocalCopyPath)) Path.GetFullPath(state.LocalCopyPath);
                return state.Width >= 0 && state.Width <= 100000 && state.Height >= 0 && state.Height <= 100000;
            }
            catch { return false; }
        }

        static bool IsValidWatchState(WatchFolderState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.Path)) return false;
            try { Path.GetFullPath(state.Path); return true; } catch { return false; }
        }

        public void Save()
        {
            lock (saveLock)
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(LibraryFolder);
                string temporary = file + ".tmp";
                File.WriteAllText(temporary, serializer.Serialize(State), new UTF8Encoding(false));
                if (File.Exists(file))
                {
                    try { File.Copy(file, file + ".bak", true); } catch { }
                    File.Replace(temporary, file, null);
                }
                else File.Move(temporary, file);
            }
        }
    }

    internal sealed class ImageRecord : INotifyPropertyChanged
    {
        readonly ImageRecordState state;
        readonly BitmapSource transientImage;
        BitmapSource thumbnail;
        bool available;
        string statusText;

        public ImageRecord(ImageRecordState state)
        {
            this.state = state;
            Id = state.Id;
            SourcePath = state.Path;
            LocalCopyPath = state.LocalCopyPath;
            AddedUtc = ParseDate(state.AddedUtc);
            LastWriteUtc = ParseDate(state.LastWriteUtc);
            Fingerprint = state.Fingerprint;
            Width = state.Width;
            Height = state.Height;
        }

        public ImageRecord(BitmapSource image, string name)
        {
            transientImage = image;
            Id = Guid.NewGuid().ToString("N");
            SourcePath = string.IsNullOrWhiteSpace(name) ? "capture.png" : name;
            AddedUtc = DateTime.UtcNow;
            Width = image == null ? 0 : image.PixelWidth;
            Height = image == null ? 0 : image.PixelHeight;
            thumbnail = image;
            available = image != null;
            statusText = "临时截图";
        }

        public string Id { get; private set; }
        public string SourcePath { get; private set; }
        public string LocalCopyPath { get; private set; }
        public DateTime AddedUtc { get; private set; }
        public DateTime LastWriteUtc { get; private set; }
        public string Fingerprint { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool IsTransient { get { return state == null; } }
        public string Name { get { return Path.GetFileName(SourcePath); } }
        public string Folder { get { return Path.GetDirectoryName(SourcePath); } }
        public string EffectivePath
        {
            get
            {
                if (IsTransient) return null;
                if (File.Exists(SourcePath)) return SourcePath;
                if (!string.IsNullOrEmpty(LocalCopyPath) && File.Exists(LocalCopyPath)) return LocalCopyPath;
                return SourcePath;
            }
        }
        public bool IsAvailable
        {
            get { return available; }
            private set { if (available == value) return; available = value; OnChanged("IsAvailable"); }
        }
        public string StatusText
        {
            get { return statusText; }
            private set { if (statusText == value) return; statusText = value; OnChanged("StatusText"); }
        }
        public BitmapSource Thumbnail
        {
            get
            {
                if (IsTransient) return transientImage;
                if (thumbnail == null && IsAvailable)
                {
                    try { thumbnail = ImageLoader.LoadThumbnail(EffectivePath, 280); }
                    catch { IsAvailable = false; StatusText = "无法读取"; }
                }
                return thumbnail;
            }
        }

        public void Refresh()
        {
            if (IsTransient)
            {
                IsAvailable = transientImage != null;
                StatusText = "临时截图";
                OnChanged("Thumbnail");
                return;
            }
            string path = EffectivePath;
            bool exists = File.Exists(path);
            IsAvailable = exists;
            StatusText = exists
                ? (FileRules.SamePath(path, SourcePath) ? "在线 · 原文件" : "在线 · 本地副本")
                : "离线 · 原文件不存在";
            if (exists)
            {
                DateTime currentWrite = File.GetLastWriteTimeUtc(path);
                if (currentWrite != LastWriteUtc || thumbnail == null) thumbnail = null;
                LastWriteUtc = currentWrite;
                if (Width <= 0 || Height <= 0)
                {
                    try
                    {
                        var size = ImageLoader.ReadSize(path);
                        Width = size.Width;
                        Height = size.Height;
                    }
                    catch { }
                }
            }
            OnChanged("Thumbnail");
            OnChanged("Name");
            OnChanged("Folder");
        }

        public void UpdateSourcePath(string path)
        {
            SourcePath = FileRules.Normalize(path);
            state.Path = SourcePath;
            OnChanged("SourcePath");
            OnChanged("Name");
            OnChanged("Folder");
        }

        public void SetLocalCopyPath(string path)
        {
            LocalCopyPath = string.IsNullOrWhiteSpace(path) ? null : FileRules.Normalize(path);
            state.LocalCopyPath = LocalCopyPath;
            OnChanged("LocalCopyPath");
            OnChanged("EffectivePath");
        }

        public void SetFingerprint(string value)
        {
            Fingerprint = string.IsNullOrWhiteSpace(value) ? null : value;
            state.Fingerprint = Fingerprint;
        }

        public ImageRecordState Snapshot()
        {
            if (IsTransient) return null;
            state.Id = Id;
            state.Path = SourcePath;
            state.LocalCopyPath = LocalCopyPath;
            state.AddedUtc = AddedUtc.ToUniversalTime().ToString("o");
            state.LastWriteUtc = LastWriteUtc.ToUniversalTime().ToString("o");
            state.Fingerprint = Fingerprint;
            state.Width = Width;
            state.Height = Height;
            return state;
        }

        static DateTime ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;
        }

        void OnChanged(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class ImageLoader
    {
        public static BitmapImage LoadThumbnail(string path, int maxPixels)
        {
            ValidateFile(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                image.StreamSource = stream;
                image.DecodePixelWidth = maxPixels;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }

        public static System.Drawing.Size ReadSize(string path)
        {
            ValidateFile(path);
            using (var image = System.Drawing.Image.FromFile(path))
            {
                if ((long)image.Width * image.Height > 80000000L)
                    throw new InvalidDataException("图片尺寸过大");
                return image.Size;
            }
        }

        public static string Fingerprint(string path)
        {
            ValidateFile(path);
            using (var source = System.Drawing.Image.FromFile(path))
            using (var sample = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var graphics = System.Drawing.Graphics.FromImage(sample))
            using (var stream = new MemoryStream())
            {
                graphics.Clear(System.Drawing.Color.Black);
                graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, sample.Width, sample.Height));
                sample.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                using (var sha = SHA256.Create())
                {
                    string dimensions = source.Width + "x" + source.Height + ":";
                    return dimensions + Convert.ToBase64String(sha.ComputeHash(stream.ToArray()));
                }
            }
        }

        static void ValidateFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("图片文件大小无效");
        }
    }

    internal sealed class ImageLibrary : IDisposable
    {
        sealed class PendingFile
        {
            public long Length;
            public DateTime Written;
            public int StableTicks;
            public int Attempts;
            public bool CopyToLibrary;
        }

        readonly StateStore store;
        readonly Dispatcher dispatcher;
        readonly Dictionary<string, FileSystemWatcher> watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PendingFile> pending = new Dictionary<string, PendingFile>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer pendingTimer;
        bool disposed;
        bool batching;

        public readonly ObservableCollection<ImageRecord> Items = new ObservableCollection<ImageRecord>();
        public event Action Changed;
        public event Action FolderImageDiscovered;

        public ImageLibrary(StateStore store, Dispatcher dispatcher)
        {
            this.store = store;
            this.dispatcher = dispatcher;
            pendingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            pendingTimer.Tick += ProcessPending;
            foreach (var state in store.State.Images)
            {
                if (string.IsNullOrWhiteSpace(state.Id)) state.Id = Guid.NewGuid().ToString("N");
                var item = new ImageRecord(state);
                item.Refresh();
                Items.Add(item);
            }
        }

        public void StartWatchers()
        {
            foreach (var folder in store.State.WatchFolders.Where(item => item.Enabled).ToArray())
                AddWatcher(folder.Path, folder.Recursive, false, folder.CopyToLibrary);
            pendingTimer.Start();
        }

        public ImageRecord AddFile(string path, bool copyToLibrary)
        {
            if (!File.Exists(path) || !FileRules.IsImage(path)) return null;
            path = FileRules.Normalize(path);
            string fingerprint;
            try
            {
                ImageLoader.ReadSize(path);
                fingerprint = ImageLoader.Fingerprint(path);
            }
            catch { return null; }
            var existing = Items.FirstOrDefault(candidate => FileRules.SamePath(candidate.SourcePath, path));
            if (existing == null)
            {
                foreach (var candidate in Items)
                {
                    if (!candidate.IsAvailable) continue;
                    string candidateFingerprint = candidate.Fingerprint;
                    if (string.IsNullOrWhiteSpace(candidateFingerprint))
                    {
                        try
                        {
                            candidateFingerprint = ImageLoader.Fingerprint(candidate.EffectivePath);
                            candidate.SetFingerprint(candidateFingerprint);
                        }
                        catch { continue; }
                    }
                    if (string.Equals(candidateFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        existing = candidate;
                        break;
                    }
                }
            }
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.Fingerprint))
                    existing.SetFingerprint(fingerprint);
                if (copyToLibrary && !FileRules.IsInside(path, store.LibraryFolder))
                {
                    if (string.IsNullOrEmpty(existing.LocalCopyPath) || !File.Exists(existing.LocalCopyPath))
                    {
                        string backupPath = Path.Combine(store.LibraryFolder, SafeName(Path.GetFileNameWithoutExtension(path)) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(path));
                        try
                        {
                            Directory.CreateDirectory(store.LibraryFolder);
                            CopyWithRetries(path, backupPath);
                            existing.SetLocalCopyPath(backupPath);
                        }
                        catch { }
                    }
                    else if (!FileRules.SamePath(path, existing.LocalCopyPath))
                    {
                        try { CopyWithRetries(path, existing.LocalCopyPath); }
                        catch { }
                    }
                }
                existing.Refresh();
                if (!batching) Save();
                return existing;
            }

            string localCopy = null;
            if (copyToLibrary && !FileRules.IsInside(path, store.LibraryFolder))
            {
                Directory.CreateDirectory(store.LibraryFolder);
                string name = Path.GetFileNameWithoutExtension(path);
                string extension = Path.GetExtension(path);
                localCopy = Path.Combine(store.LibraryFolder, SafeName(name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension);
                try { CopyWithRetries(path, localCopy); }
                catch { localCopy = null; }
            }

            var state = new ImageRecordState
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                LocalCopyPath = localCopy,
                AddedUtc = DateTime.UtcNow.ToString("o"),
                LastWriteUtc = File.GetLastWriteTimeUtc(path).ToString("o"),
                Fingerprint = fingerprint
            };
            var record = new ImageRecord(state);
            record.Refresh();
            Items.Insert(0, record);
            if (!batching)
            {
                Save();
                NotifyChanged();
            }
            return record;
        }

        public ImageRecord AddTransient(BitmapSource image, string name)
        {
            if (image == null) return null;
            var record = new ImageRecord(image, name);
            Items.Insert(0, record);
            NotifyChanged();
            return record;
        }

        public int AddFiles(IEnumerable<string> paths, bool copyToLibrary)
        {
            int count = 0;
            batching = true;
            try
            {
                foreach (string path in paths)
                    if (AddFile(path, copyToLibrary) != null) count++;
            }
            finally
            {
                batching = false;
                Save();
                NotifyChanged();
            }
            return count;
        }

        public int ScanFolder(string folder, bool recursive, bool copyToLibrary)
        {
            if (!Directory.Exists(folder)) return 0;
            try
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var paths = Directory.EnumerateFiles(folder, "*.*", option).Where(FileRules.IsImage).ToArray();
                return AddFiles(paths, copyToLibrary);
            }
            catch (UnauthorizedAccessException) { return 0; }
            catch (DirectoryNotFoundException) { return 0; }
            catch (IOException) { return 0; }
        }

        public void AddWatcher(string folder, bool recursive, bool persist, bool? copyToLibrary = null)
        {
            if (!Directory.Exists(folder)) return;
            folder = FileRules.Normalize(folder);
            if (watchers.ContainsKey(folder)) return;
            bool useCopyToLibrary = copyToLibrary ?? store.State.CopyToLibrary;
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                Filter = "*.*",
                EnableRaisingEvents = true
            };
            watcher.Created += delegate(object sender, FileSystemEventArgs args) { Discover(args.FullPath, useCopyToLibrary); };
            watcher.Changed += delegate(object sender, FileSystemEventArgs args) { Discover(args.FullPath, useCopyToLibrary); };
            watcher.Renamed += delegate(object sender, RenamedEventArgs args) { Rename(args.OldFullPath, args.FullPath, useCopyToLibrary); };
            watcher.Deleted += delegate(object sender, FileSystemEventArgs args) { MarkChanged(args.FullPath); };
            watcher.Error += delegate(object sender, ErrorEventArgs args)
            {
                Dispatch(delegate
                {
                    try { ScanFolder(folder, recursive, useCopyToLibrary); }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                });
            };
            watchers.Add(folder, watcher);
            if (persist)
            {
                var saved = store.State.WatchFolders.FirstOrDefault(item => FileRules.SamePath(item.Path, folder));
                if (saved == null)
                    store.State.WatchFolders.Add(new WatchFolderState { Path = folder, Recursive = recursive, Enabled = true, CopyToLibrary = copyToLibrary });
                else if (copyToLibrary.HasValue)
                    saved.CopyToLibrary = copyToLibrary;
            }
            store.Save();
            ScanFolder(folder, recursive, useCopyToLibrary);
            NotifyChanged();
        }

        public void RemoveWatcher(string folder)
        {
            folder = FileRules.Normalize(folder);
            FileSystemWatcher watcher;
            if (watchers.TryGetValue(folder, out watcher))
            {
                watcher.Dispose();
                watchers.Remove(folder);
            }
            store.State.WatchFolders.RemoveAll(item => FileRules.SamePath(item.Path, folder));
            store.Save();
            NotifyChanged();
        }

        public void Remove(ImageRecord item, bool deleteLocalCopy)
        {
            if (!Items.Remove(item)) return;
            if (deleteLocalCopy && !string.IsNullOrEmpty(item.LocalCopyPath) && FileRules.IsInside(item.LocalCopyPath, store.LibraryFolder))
            {
                try { if (File.Exists(item.LocalCopyPath)) File.Delete(item.LocalCopyPath); }
                catch { }
            }
            store.State.Images.RemoveAll(state => state.Id == item.Id);
            Save();
            NotifyChanged();
        }

        public void RefreshAll()
        {
            foreach (var item in Items) item.Refresh();
            NotifyChanged();
        }

        public void Dispose()
        {
            disposed = true;
            pendingTimer.Stop();
            foreach (var watcher in watchers.Values) watcher.Dispose();
            watchers.Clear();
        }

        void Discover(string path, bool copyToLibrary)
        {
            Dispatch(delegate
            {
                if (!FileRules.IsImage(path)) return;
                try
                {
                    var info = new FileInfo(path);
                    pending[path] = new PendingFile
                    {
                        Length = info.Exists ? info.Length : -1,
                        Written = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                        CopyToLibrary = copyToLibrary
                    };
                }
                catch { }
            });
        }

        void Rename(string oldPath, string newPath, bool copyToLibrary)
        {
            Dispatch(delegate
            {
                var item = Items.FirstOrDefault(candidate => FileRules.SamePath(candidate.SourcePath, oldPath));
                if (item != null)
                {
                    if (!FileRules.IsImage(newPath))
                    {
                        item.Refresh();
                        Save();
                        NotifyChanged();
                        return;
                    }
                    item.UpdateSourcePath(newPath);
                    item.Refresh();
                    Save();
                    NotifyChanged();
                }
                else Discover(newPath, copyToLibrary);
            });
        }

        void MarkChanged(string path)
        {
            Dispatch(delegate
            {
                pending.Remove(path);
                var item = Items.FirstOrDefault(candidate => FileRules.SamePath(candidate.SourcePath, path));
                if (item != null)
                {
                    item.Refresh();
                    NotifyChanged();
                }
            });
        }

        void ProcessPending(object sender, EventArgs args)
        {
            foreach (var entry in pending.ToArray())
            {
                FileInfo info;
                try { info = new FileInfo(entry.Key); }
                catch { pending.Remove(entry.Key); continue; }
                if (!info.Exists)
                {
                    if (++entry.Value.Attempts > 20) pending.Remove(entry.Key);
                    continue;
                }
                if (info.Length == entry.Value.Length && info.LastWriteTimeUtc == entry.Value.Written)
                    entry.Value.StableTicks++;
                else
                {
                    entry.Value.Length = info.Length;
                    entry.Value.Written = info.LastWriteTimeUtc;
                    entry.Value.StableTicks = 0;
                    entry.Value.Attempts++;
                }
                if (entry.Value.StableTicks < 2)
                {
                    if (entry.Value.Attempts > 40) pending.Remove(entry.Key);
                    continue;
                }
                pending.Remove(entry.Key);
                if (AddFile(entry.Key, entry.Value.CopyToLibrary) != null && FolderImageDiscovered != null)
                    FolderImageDiscovered();
            }
        }

        void Dispatch(Action action)
        {
            if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (dispatcher.CheckAccess())
            {
                if (!disposed) action();
                return;
            }
            try
            {
                dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!disposed && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                        action();
                }));
            }
            catch (InvalidOperationException) { }
        }

        void Save()
        {
            store.State.Images = Items.Where(item => !item.IsTransient).Select(item => item.Snapshot()).ToList();
            store.Save();
        }

        void NotifyChanged()
        {
            if (Changed != null) Changed();
        }

        static string SafeName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "image" : value;
        }

        static void CopyWithRetries(string source, string destination)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Copy(source, destination, true);
                    return;
                }
                catch (IOException error)
                {
                    last = error;
                    System.Threading.Thread.Sleep(80);
                }
                catch (UnauthorizedAccessException error)
                {
                    last = error;
                    System.Threading.Thread.Sleep(80);
                }
            }
            if (last != null) throw last;
        }
    }
}
