using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    }

    internal sealed class ImageRecordState
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string AddedUtc { get; set; }
        public string LastWriteUtc { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class WatchFolderState
    {
        public string Path { get; set; }
        public bool Recursive { get; set; }
        public bool Enabled { get; set; }
    }

    internal sealed class SavedState
    {
        public bool CollectClipboard { get; set; }
        public string ScreenshotsFolder { get; set; }
        public string WeChatFolder { get; set; }
        public bool WeChatPathDecided { get; set; }
        public string QqFolder { get; set; }
        public bool QqPathDecided { get; set; }
        public List<ImageRecordState> Images { get; set; }
        public List<WatchFolderState> WatchFolders { get; set; }

        public SavedState()
        {
            CollectClipboard = false;
            Images = new List<ImageRecordState>();
            WatchFolders = new List<WatchFolderState>();
        }
    }

    internal sealed class StateStore
    {
        readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly object saveLock = new object();
        readonly string file;

        public readonly string Root;
        public readonly SavedState State;

        public StateStore()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameDock", "data");
            file = Path.Combine(Root, "state.json");
            State = Load();
        }

        SavedState Load()
        {
            SavedState value;
            if (TryLoad(file, out value))
            {
                PreserveLegacyState(file);
                SavedState backupState;
                if (TryLoad(file + ".bak", out backupState))
                    PreserveLegacyState(file + ".bak");
                return value;
            }
            string backup = file + ".bak";
            if (TryLoad(backup, out value))
            {
                PreserveLegacyState(backup);
                return value;
            }
            if (File.Exists(file))
            {
                try { File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            }
            return new SavedState();
        }

        void PreserveLegacyState(string candidate)
        {
            string snapshot = file + ".legacy";
            try
            {
                var json = serializer.DeserializeObject(File.ReadAllText(candidate, Encoding.UTF8))
                    as Dictionary<string, object>;
                object images;
                if (json == null || !json.TryGetValue("Images", out images)) return;
                var records = images as System.Collections.IEnumerable;
                if (records == null) return;
                foreach (var record in records)
                {
                    var fields = record as Dictionary<string, object>;
                    object copyPath;
                    if (fields != null && fields.TryGetValue("LocalCopyPath", out copyPath) &&
                        copyPath is string && !string.IsNullOrWhiteSpace((string)copyPath))
                    {
                        if (File.Exists(snapshot))
                        {
                            if (File.ReadAllBytes(snapshot).SequenceEqual(File.ReadAllBytes(candidate)))
                                return;
                            snapshot += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff");
                        }
                        File.Copy(candidate, snapshot, false);
                        if (!File.ReadAllBytes(snapshot).SequenceEqual(File.ReadAllBytes(candidate)))
                            throw new IOException("旧状态快照校验失败");
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                throw new IOException("旧图片副本的路径记录无法安全备份；为避免丢失恢复线索，本次没有启动新版 PicShot。", error);
            }
        }

        bool TryLoad(string candidate, out SavedState value)
        {
            value = null;
            try
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length > int.MaxValue) return false;
                value = serializer.Deserialize<SavedState>(File.ReadAllText(candidate, Encoding.UTF8));
                if (value == null || value.Images == null || value.WatchFolders == null) return false;
                value.Images = value.Images.Where(IsValidImageState).ToList();
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
            AddedUtc = ParseDate(state.AddedUtc);
            LastWriteUtc = ParseDate(state.LastWriteUtc);
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
        public DateTime AddedUtc { get; private set; }
        public DateTime LastWriteUtc { get; private set; }
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
            string path = SourcePath;
            bool exists = File.Exists(path);
            IsAvailable = exists;
            StatusText = exists ? "在线 · 原文件" : "离线 · 原文件不存在";
            if (!exists) thumbnail = null;
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

        public void MarkDiscovered()
        {
            AddedUtc = DateTime.UtcNow;
            state.AddedUtc = AddedUtc.ToString("o");
        }

        public ImageRecordState Snapshot()
        {
            if (IsTransient) return null;
            state.Id = Id;
            state.Path = SourcePath;
            state.AddedUtc = AddedUtc.ToUniversalTime().ToString("o");
            state.LastWriteUtc = LastWriteUtc.ToUniversalTime().ToString("o");
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
        }

        sealed class ValidatedFile
        {
            public string Path;
            public System.Drawing.Size Size;
        }

        readonly StateStore store;
        readonly Dispatcher dispatcher;
        readonly Dictionary<string, FileSystemWatcher> watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, PendingFile> pending = new Dictionary<string, PendingFile>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, ImageRecord> byPath = new Dictionary<string, ImageRecord>(StringComparer.OrdinalIgnoreCase);
        readonly SemaphoreSlim scanSlots = new SemaphoreSlim(4);
        readonly DispatcherTimer pendingTimer;
        readonly DispatcherTimer saveTimer;
        readonly DispatcherTimer changedTimer;
        volatile bool disposed;
        bool batching;
        bool saveFailureReported;

        public readonly ObservableCollection<ImageRecord> Items = new ObservableCollection<ImageRecord>();
        public event Action Changed;
        public event Action<ImageRecord> FolderImageDiscovered;

        public ImageLibrary(StateStore store, Dispatcher dispatcher)
        {
            this.store = store;
            this.dispatcher = dispatcher;
            pendingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
            pendingTimer.Tick += ProcessPending;
            saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(850) };
            saveTimer.Tick += delegate { Save(); };
            changedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            changedTimer.Tick += delegate { changedTimer.Stop(); NotifyChanged(); };
            foreach (var state in store.State.Images)
            {
                if (string.IsNullOrWhiteSpace(state.Id)) state.Id = Guid.NewGuid().ToString("N");
                var item = new ImageRecord(state);
                item.Refresh();
                Items.Add(item);
                byPath[item.SourcePath] = item;
            }
        }

        public void StartWatchers()
        {
            foreach (var folder in store.State.WatchFolders.Where(item => item.Enabled).ToArray())
                AddWatcher(folder.Path, folder.Recursive, false);
            pendingTimer.Start();
        }

        public ImageRecord AddFile(
            string path, bool newlyDiscovered = false, System.Drawing.Size? validatedSize = null)
        {
            if (!File.Exists(path) || !FileRules.IsImage(path)) return null;
            path = FileRules.Normalize(path);
            System.Drawing.Size size;
            try
            {
                size = validatedSize ?? ImageLoader.ReadSize(path);
            }
            catch { return null; }
            ImageRecord existing;
            byPath.TryGetValue(path, out existing);
            if (existing != null)
            {
                if (newlyDiscovered) existing.MarkDiscovered();
                existing.Refresh();
                if (!batching)
                {
                    Save();
                    NotifyChanged();
                }
                return existing;
            }

            var state = new ImageRecordState
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                AddedUtc = (newlyDiscovered ? DateTime.UtcNow : File.GetLastWriteTimeUtc(path)).ToString("o"),
                LastWriteUtc = File.GetLastWriteTimeUtc(path).ToString("o"),
                Width = size.Width,
                Height = size.Height
            };
            var record = new ImageRecord(state);
            record.Refresh();
            if (batching) Items.Add(record);
            else Items.Insert(0, record);
            byPath[path] = record;
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

        public int AddFiles(IEnumerable<string> paths, bool newlyDiscovered = true)
        {
            int count = 0;
            batching = true;
            try
            {
                foreach (string path in paths)
                    if (AddFile(path, newlyDiscovered) != null) count++;
            }
            finally
            {
                batching = false;
                Save();
                NotifyChanged();
            }
            return count;
        }

        public int ScanFolder(string folder, bool recursive)
        {
            if (!Directory.Exists(folder)) return 0;
            try
            {
                var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var paths = Directory.EnumerateFiles(folder, "*.*", option).Where(FileRules.IsImage).ToArray();
                return AddFiles(paths, false);
            }
            catch (UnauthorizedAccessException) { return 0; }
            catch (DirectoryNotFoundException) { return 0; }
            catch (IOException) { return 0; }
        }

        public void AddWatcher(string folder, bool recursive, bool persist, bool scanExisting = true)
        {
            if (!Directory.Exists(folder)) return;
            folder = FileRules.Normalize(folder);
            if (watchers.ContainsKey(folder))
            {
                if (scanExisting) ScanFolderInBackground(folder, recursive);
                return;
            }
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                Filter = "*.*",
                EnableRaisingEvents = true
            };
            watcher.Created += delegate(object sender, FileSystemEventArgs args) { Discover(args.FullPath); };
            watcher.Changed += delegate(object sender, FileSystemEventArgs args) { Discover(args.FullPath); };
            watcher.Renamed += delegate(object sender, RenamedEventArgs args) { Rename(args.OldFullPath, args.FullPath); };
            watcher.Deleted += delegate(object sender, FileSystemEventArgs args) { MarkChanged(args.FullPath); };
            watcher.Error += delegate(object sender, ErrorEventArgs args)
            {
                ScanFolderInBackground(folder, recursive);
            };
            watchers.Add(folder, watcher);
            if (persist)
            {
                var saved = store.State.WatchFolders.FirstOrDefault(item => FileRules.SamePath(item.Path, folder));
                if (saved == null)
                    store.State.WatchFolders.Add(new WatchFolderState { Path = folder, Recursive = recursive, Enabled = true });
            }
            store.Save();
            if (scanExisting) ScanFolderInBackground(folder, recursive);
            NotifyChanged();
        }

        public void ScanFolderInBackground(string folder, bool recursive)
        {
            Task.Run(delegate
            {
                var directories = new Stack<string>();
                directories.Push(folder);
                var batch = new List<ValidatedFile>(25);
                while (directories.Count > 0 && !disposed)
                {
                    string current = directories.Pop();
                    string[] paths;
                    try { paths = Directory.GetFiles(current); }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    catch (IOException) { continue; }
                    foreach (var path in paths)
                    {
                        if (disposed) return;
                        if (!FileRules.IsImage(path)) continue;
                        try { batch.Add(new ValidatedFile { Path = path, Size = ImageLoader.ReadSize(path) }); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        catch (ArgumentException) { continue; }
                        catch (InvalidOperationException) { continue; }
                        catch (OutOfMemoryException)
                        {
                            System.Diagnostics.Trace.TraceWarning("无法解码图片：" + path);
                            continue;
                        }
                        if (batch.Count < 25) continue;
                        var ready = batch.ToArray();
                        batch.Clear();
                        QueueBatch(ready);
                    }
                    if (!recursive) continue;
                    try
                    {
                        foreach (var child in Directory.GetDirectories(current))
                        {
                            try
                            {
                                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                    directories.Push(child);
                            }
                            catch (UnauthorizedAccessException) { }
                            catch (DirectoryNotFoundException) { }
                            catch (IOException) { }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (IOException) { }
                }
                if (batch.Count > 0 && !disposed) QueueBatch(batch.ToArray());
            });
        }

        void QueueBatch(ValidatedFile[] files)
        {
            while (!scanSlots.Wait(250))
                if (disposed) return;
            if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                scanSlots.Release();
                return;
            }
            try
            {
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    try
                    {
                        if (!disposed)
                        {
                            batching = true;
                            try
                            {
                                foreach (var file in files)
                                    AddFile(file.Path, false, file.Size);
                            }
                            finally { batching = false; }
                            if (!saveTimer.IsEnabled) saveTimer.Start();
                            if (!changedTimer.IsEnabled) changedTimer.Start();
                        }
                    }
                    finally { scanSlots.Release(); }
                }));
            }
            catch (InvalidOperationException) { scanSlots.Release(); }
        }

        public void RemoveWatcher(string folder)
        {
            RemoveWatcher(folder, true);
        }

        public void RemoveWatcher(string folder, bool removePersisted)
        {
            folder = FileRules.Normalize(folder);
            FileSystemWatcher watcher;
            if (watchers.TryGetValue(folder, out watcher))
            {
                watcher.Dispose();
                watchers.Remove(folder);
            }
            if (removePersisted)
                store.State.WatchFolders.RemoveAll(item => FileRules.SamePath(item.Path, folder));
            store.Save();
            NotifyChanged();
        }

        public void RemoveSpecialWatcher(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            if (store.State.WatchFolders.Any(item =>
                item.Enabled && FileRules.SamePath(item.Path, folder)))
                return;
            RemoveWatcher(folder, false);
        }

        public bool Delete(ImageRecord item, out string error)
        {
            error = null;
            if (!Items.Contains(item)) return true;
            if (!item.IsTransient && File.Exists(item.SourcePath))
            {
                try
                {
                    File.Delete(item.SourcePath);
                }
                catch (Exception exception)
                {
                    error = item.Name + ": " + exception.Message;
                    return false;
                }
            }

            Items.Remove(item);
            if (!item.IsTransient) byPath.Remove(item.SourcePath);
            store.State.Images.RemoveAll(state => state.Id == item.Id);
            Save();
            NotifyChanged();
            return true;
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
            changedTimer.Stop();
            if (saveTimer.IsEnabled)
            {
                string error;
                TryFlush(out error);
            }
            saveTimer.Stop();
            foreach (var watcher in watchers.Values) watcher.Dispose();
            watchers.Clear();
        }

        void Discover(string path)
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
                        Written = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue
                    };
                }
                catch { }
            });
        }

        void Rename(string oldPath, string newPath)
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
                    byPath.Remove(item.SourcePath);
                    item.UpdateSourcePath(newPath);
                    byPath[item.SourcePath] = item;
                    item.Refresh();
                    Save();
                    NotifyChanged();
                }
                else Discover(newPath);
            });
        }

        void MarkChanged(string path)
        {
            Dispatch(delegate
            {
                pending.Remove(path);
                ImageRecord item;
                byPath.TryGetValue(path, out item);
                if (item != null)
                {
                    item.Refresh();
                    NotifyChanged();
                }
            });
        }

        void ProcessPending(object sender, EventArgs args)
        {
            var ready = new List<string>();
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
                ready.Add(entry.Key);
            }
            if (ready.Count == 0) return;
            batching = true;
            try
            {
                foreach (string path in ready)
                {
                    var item = AddFile(path, true);
                    if (item != null && FolderImageDiscovered != null)
                        FolderImageDiscovered(item);
                }
            }
            finally { batching = false; }
            if (!saveTimer.IsEnabled) saveTimer.Start();
            if (!changedTimer.IsEnabled) changedTimer.Start();
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
            string error;
            if (TryFlush(out error)) return;
            if (!saveFailureReported)
            {
                saveFailureReported = true;
                System.Windows.MessageBox.Show(
                    "图片路径记录尚未保存，程序将尝试重试。请检查磁盘空间和写入权限。\n" +
                    error,
                    "PicShot 保存失败",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            if (!disposed)
            {
                saveTimer.Interval = TimeSpan.FromSeconds(5);
                saveTimer.Start();
            }
        }

        public bool TryFlush(out string error)
        {
            saveTimer.Stop();
            store.State.Images = Items.Where(item => !item.IsTransient).Select(item => item.Snapshot()).ToList();
            try
            {
                store.Save();
                saveFailureReported = false;
                saveTimer.Interval = TimeSpan.FromMilliseconds(850);
                error = null;
                return true;
            }
            catch (Exception failure)
            {
                error = failure.Message;
                if (!disposed)
                {
                    saveTimer.Interval = TimeSpan.FromSeconds(5);
                    saveTimer.Start();
                }
                return false;
            }
        }

        void NotifyChanged()
        {
            if (Changed != null) Changed();
        }
    }
}
