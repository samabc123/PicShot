using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FrameDock
{
    internal sealed class UpdateRelease
    {
        public string Version;
        public string Notes;
        public string Url;
        public string DownloadUrl;
        public string Sha256;
        public long Size;
    }

    internal sealed class AppUpdates : IDisposable
    {
        public const string Repository = "https://github.com/samabc123/PicShot";
        public const string Endpoint = "https://api.github.com/repos/samabc123/PicShot/releases/latest";
        public static readonly string CurrentVersion = GetCurrentVersion();
        static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
        static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);
        static readonly string Cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrameDock", "Updates");
        readonly HttpClient client;
        readonly string currentVersion;
        readonly TimeSpan metadataTimeout;

        public AppUpdates()
            : this(new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }), CurrentVersion, MetadataTimeout)
        {
        }

        internal AppUpdates(HttpClient client, string currentVersion, TimeSpan metadataTimeout)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            this.client = client;
            this.currentVersion = currentVersion;
            this.metadataTimeout = metadataTimeout;
            client.Timeout = TimeSpan.FromMinutes(10);
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PicShot/" + CurrentVersion);
        }

        public async Task<UpdateRelease> CheckAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                using (var response = await RequestMetadata(
                    HttpMethod.Get, Endpoint, "application/vnd.github+json", cancellation).ConfigureAwait(false))
                    return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false), currentVersion);
            }
            catch (HttpRequestException)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            try
            {
                return await CheckReleasePage(cancellation).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new IOException(
                    "GitHub API 和官方发布页均无法访问，请检查网络或系统代理后重试。详情：" + error.Message,
                    error);
            }
        }

        async Task<HttpResponseMessage> RequestMetadata(
            HttpMethod method, string url, string accept, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var request = new HttpRequestMessage(method, url))
            {
                timeout.CancelAfter(metadataTimeout);
                request.Headers.Accept.ParseAdd(accept);
                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException error)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw new HttpRequestException("请求 GitHub 超时，请稍后重试。", error);
                }
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException("GitHub 返回 HTTP " + status + "。");
                }
                return response;
            }
        }

        async Task<UpdateRelease> CheckReleasePage(CancellationToken cancellation)
        {
            UpdateRelease release;
            using (var response = await RequestMetadata(
                HttpMethod.Get, Repository + "/releases/latest", "text/html", cancellation).ConfigureAwait(false))
            {
                release = ParseReleasePage(
                    response.RequestMessage == null ? null : response.RequestMessage.RequestUri,
                    currentVersion);
            }
            if (release == null) return null;

            string tag = new Uri(release.Url).Segments.Last();
            using (var response = await RequestMetadata(
                HttpMethod.Get, Repository + "/releases/expanded_assets/" + tag,
                "text/html", cancellation).ConfigureAwait(false))
            {
                release.Sha256 = ParseAssetDigest(
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false), release.DownloadUrl);
            }
            using (var response = await RequestMetadata(
                HttpMethod.Head, release.DownloadUrl, "application/octet-stream", cancellation).ConfigureAwait(false))
            {
                if (response.RequestMessage == null ||
                    response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException("更新下载未使用安全连接。");
                release.Size = response.Content.Headers.ContentLength ?? 0;
                if (release.Size <= 0)
                    throw new InvalidDataException("未能确认安装包大小，请稍后重试。");
            }
            return release;
        }

        internal static UpdateRelease ParseReleasePage(Uri uri, string current)
        {
            const string prefix = "/samabc123/PicShot/releases/tag/";
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || !String.IsNullOrEmpty(uri.UserInfo) ||
                !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
                !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("无法确认 GitHub 正式版本，请前往发布页查看。");
            string tag = uri.AbsolutePath.Substring(prefix.Length);
            string version = ParseVersion(tag);
            if (new Version(version) <= new Version(current)) return null;
            return new UpdateRelease
            {
                Version = version,
                Url = Repository + "/releases/tag/" + tag,
                DownloadUrl = Repository + "/releases/download/" + tag +
                    "/PicShot-" + version + "-windows-x64.zip",
                Notes = "已通过 GitHub 官方发布页检测到新版本。完整更新说明请查看发布页。"
            };
        }

        internal static string ParseAssetDigest(string html, string downloadUrl)
        {
            if (String.IsNullOrEmpty(html) || html.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("GitHub 安装包信息无效。");
            string expectedPath = new Uri(downloadUrl).AbsolutePath;
            string digest = null;
            // Keep the download link and digest in the same asset row; never use a page-wide hash.
            foreach (Match row in Regex.Matches(html, @"<li\b[^>]*>(.*?)</li\s*>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase, PatternTimeout))
            {
                bool matchesAsset = Regex.Matches(row.Groups[1].Value, @"<a\b[^>]*>",
                    RegexOptions.IgnoreCase, PatternTimeout).Cast<Match>()
                    .Any(link => HtmlAttribute(link.Value, "href") == expectedPath);
                if (!matchesAsset) continue;
                var hashes = Regex.Matches(row.Groups[1].Value, @"<clipboard-copy\b[^>]*>",
                    RegexOptions.IgnoreCase, PatternTimeout).Cast<Match>()
                    .Select(copy => HtmlAttribute(copy.Value, "value")).ToArray();
                if (digest != null || hashes.Length != 1 ||
                    !Regex.IsMatch(hashes[0], @"\Asha256:[a-fA-F0-9]{64}\z"))
                    throw new InvalidDataException("安装包校验信息缺失或不明确，已停止自动更新。");
                digest = hashes[0].Substring(7);
            }
            if (digest == null)
                throw new InvalidDataException("新版本缺少有效的 Windows 安装包或校验信息。");
            return digest;
        }

        static string HtmlAttribute(string tag, string name)
        {
            Match match = Regex.Match(tag, @"\s" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')",
                RegexOptions.IgnoreCase, PatternTimeout);
            return match.Success ? WebUtility.HtmlDecode(
                match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "";
        }

        static string ParseVersion(string tag)
        {
            Version parsed;
            if (tag == null || !Regex.IsMatch(tag, @"\A[vV]?[0-9]+\.[0-9]+\.[0-9]+\z") ||
                !Version.TryParse(tag.TrimStart('v', 'V'), out parsed))
                throw new InvalidDataException("发布版本号无效。");
            return tag.TrimStart('v', 'V');
        }

        internal static UpdateRelease Parse(string json, string current)
        {
            var release = new JavaScriptSerializer().Deserialize<ReleaseData>(json);
            if (release == null)
                throw new InvalidDataException("GitHub 返回了空的发布信息，无法确认最新版本。");
            if (release.draft || release.prerelease)
                throw new InvalidDataException("GitHub 未返回可用的正式版本。");

            string version = ParseVersion(release.tag_name);
            if (new Version(version) <= new Version(current))
                return null;

            string file = "PicShot-" + version + "-windows-x64.zip";
            AssetData asset = (release.assets ?? new AssetData[0])
                .SingleOrDefault(item => item.name == file);
            string expected = Repository + "/releases/download/" + release.tag_name + "/" + file;
            if (asset == null ||
                asset.browser_download_url != expected ||
                asset.size <= 0 ||
                !Regex.IsMatch(asset.digest ?? "", @"\Asha256:[a-fA-F0-9]{64}\z"))
                throw new InvalidDataException("新版本缺少有效的 Windows 安装包或校验信息。");

            return new UpdateRelease
            {
                Version = version,
                Notes = release.body ?? "",
                Url = Repository + "/releases/tag/" + release.tag_name,
                DownloadUrl = expected,
                Size = asset.size,
                Sha256 = asset.digest.Substring(7)
            };
        }

        public async Task<string> DownloadAsync(
            UpdateRelease release,
            IProgress<int> progress,
            CancellationToken cancellation)
        {
            string work = Path.Combine(Cache, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string archive = Path.Combine(work, "update.zip");
                using (var response = await client.GetAsync(
                    release.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage == null ||
                        response.RequestMessage.RequestUri.Scheme != "https")
                        throw new InvalidDataException("更新下载未使用安全连接。");

                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(archive))
                    {
                        byte[] buffer = new byte[65536];
                        long total = 0;
                        int length;
                        while ((length = await input.ReadAsync(
                            buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            total += length;
                            if (total > release.Size)
                                throw new InvalidDataException("更新包大小不符合发布信息。");
                            await output.WriteAsync(
                                buffer, 0, length, cancellation).ConfigureAwait(false);
                            progress.Report((int)(total * 100 / release.Size));
                        }
                        if (total != release.Size)
                            throw new InvalidDataException("更新包未下载完整。");
                    }
                }

                await Task.Run(delegate
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (var input = File.OpenRead(archive))
                    using (var hash = SHA256.Create())
                    {
                        string actual = BitConverter.ToString(hash.ComputeHash(input))
                            .Replace("-", "");
                        if (!String.Equals(actual, release.Sha256,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("更新包校验失败。");
                    }
                    Extract(archive, Path.Combine(work, "contents"), release.Version);
                }, cancellation).ConfigureAwait(false);
                return work;
            }
            catch
            {
                Discard(work);
                throw;
            }
        }

        internal static void Extract(string archive, string destination, string version)
        {
            string root = Path.GetFullPath(destination)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string appRoot = root + "app" + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    string file = Path.GetFullPath(Path.Combine(root, name));
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(":") ||
                        name.Split('/').Any(part => part == ".."))
                        throw new InvalidDataException("更新包包含无效路径。");
                    if (!name.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ||
                        String.IsNullOrEmpty(entry.Name))
                        continue;
                    if (!file.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新文件必须位于 app 目录。");
                    if (name.StartsWith("app/data/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包不能覆盖用户数据。");
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    entry.ExtractToFile(file);
                }
            }

            string executable = Path.Combine(root, "app", "PicShot.exe");
            if (!File.Exists(executable) ||
                FileVersionInfo.GetVersionInfo(executable).ProductVersion != version ||
                !File.Exists(Path.Combine(root, "app", "update.ps1")))
                throw new InvalidDataException("更新包不完整或版本不匹配。");
        }

        public static void Install(string work)
        {
            string script = Path.Combine(work, "contents", "app", "update.ps1");
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                    Quote(script) + " -WorkPath " + Quote(work) +
                    " -TargetPath " + Quote(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\')) +
                    " -ParentProcessId " + Process.GetCurrentProcess().Id
            };
            using (var process = Process.Start(start))
            {
                if (process == null)
                    throw new IOException("无法启动更新安装程序。");
            }
        }

        public static void Discard(string work)
        {
            string path = Path.GetFullPath(work);
            string cacheRoot = Path.GetFullPath(Cache).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!path.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string GetCurrentVersion()
        {
            var attribute = (AssemblyInformationalVersionAttribute)
                Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .SingleOrDefault();
            return attribute == null ? "0.0.0" : attribute.InformationalVersion;
        }

        static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        public void Dispose()
        {
            client.Dispose();
        }

        public sealed class ReleaseData
        {
            public string tag_name { get; set; }
            public string body { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public AssetData[] assets { get; set; }
        }

        public sealed class AssetData
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
    }
}
