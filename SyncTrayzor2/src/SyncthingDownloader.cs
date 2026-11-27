using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor2
{
    /// <summary>
    /// 从 Syncthing 官方 GitHub Release 下载 syncthing.exe。
    /// 源顺序：镜像加速 → API 资产端点 → 直连；下载完成后 SHA256 校验（官方提供 digest）。
    /// </summary>
    internal static class SyncthingDownloader
    {
        public sealed class ReleaseAsset
        {
            public string Name;
            public string Version;
            public string BrowserUrl;
            public long AssetId;
            public string Sha256;
            public long SizeBytes;
        }

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10
            };
            var http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SyncTrayzor2/2.0 (syncthing tray wrapper)");
            return http;
        }

        /// <summary>查询官方最新 release，返回 windows-amd64 资产信息。</summary>
        public static async Task<ReleaseAsset> FindLatestAsync(CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/syncthing/syncthing/releases/latest");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var version = tag.TrimStart('v', 'V');

            foreach (var assetEl in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var name = assetEl.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("syncthing-windows-amd64-v", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var asset = new ReleaseAsset
                {
                    Name = name,
                    Version = version,
                    BrowserUrl = assetEl.GetProperty("browser_download_url").GetString() ?? "",
                    AssetId = assetEl.GetProperty("id").GetInt64(),
                    SizeBytes = assetEl.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0
                };

                if (assetEl.TryGetProperty("digest", out var dEl))
                {
                    var digest = dEl.GetString() ?? "";
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        asset.Sha256 = digest.Substring(7).ToLowerInvariant();
                }

                if (asset.AssetId == 0 || string.IsNullOrEmpty(asset.BrowserUrl)) continue;

                Log.Info("官方最新版 Syncthing v" + version + "（"
                         + (asset.SizeBytes / 1048576.0).ToString("0.0") + " MB）");
                return asset;
            }

            throw new InvalidOperationException("官方 Release 中未找到 windows-amd64 资产");
        }

        public static async Task<string> DownloadAsync(string destinationExe,
            IProgress<(long received, long total)> progress, CancellationToken ct)
        {
            var asset = await FindLatestAsync(ct).ConfigureAwait(false);
            var errors = new System.Text.StringBuilder();

            foreach (var source in BuildSources(asset))
            {
                var zipPath = Path.Combine(Path.GetTempPath(), "syncthing-dl-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    await DownloadFileAsync(source, asset, zipPath, progress, ct).ConfigureAwait(false);
                    VerifyChecksum(zipPath, asset.Sha256);
                    var result = ExtractExe(zipPath, destinationExe, asset.Version);
                    progress?.Report((asset.SizeBytes, asset.SizeBytes));
                    return result;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn("下载源不可用：" + source + " -> " + ex.Message);
                    errors.AppendLine(source + " -> " + ex.Message);
                }
                finally
                {
                    TryDelete(zipPath);
                }
            }

            throw new InvalidOperationException("所有下载源均失败，请手动下载 syncthing.exe。\n" + errors);
        }

        /// <summary>镜像加速优先（对慢速网络友好），API 端点与直连兜底。</summary>
        private static string[] BuildSources(ReleaseAsset a)
        {
            return new[]
            {
                "https://ghfast.top/" + a.BrowserUrl,
                "https://api.github.com/repos/syncthing/syncthing/releases/assets/" + a.AssetId,
                a.BrowserUrl
            };
        }

        private static async Task DownloadFileAsync(string source, ReleaseAsset asset, string zipPath,
            IProgress<(long, long)> progress, CancellationToken ct)
        {
            // 单个源最多 5 分钟，超时换下一家
            using var perSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perSource.CancelAfter(TimeSpan.FromMinutes(5));

            using var req = new HttpRequestMessage(HttpMethod.Get, source);
            if (source.Contains("api.github.com"))
                req.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, perSource.Token)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? asset.SizeBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(perSource.Token).ConfigureAwait(false);
            await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long received = 0;
            long lastReport = 0;
            int read;

            while ((read = await src.ReadAsync(buffer, perSource.Token).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), perSource.Token).ConfigureAwait(false);
                received += read;

                if (received - lastReport >= (1 << 20))
                {
                    lastReport = received;
                    progress?.Report((received, total));
                }
            }

            if (asset.SizeBytes > 0 && received != asset.SizeBytes)
                throw new IOException("下载不完整（" + received + "/" + asset.SizeBytes + " 字节）");
        }

        private static void VerifyChecksum(string zipPath, string expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256))
            {
                Log.Warn("官方未提供 SHA256，跳过校验");
                return;
            }

            using var sha = SHA256.Create();
            using var fs = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();

            if (hash != expectedSha256)
                throw new IOException("SHA256 校验失败，下载文件已丢弃（可能被篡改或传输损坏）");

            Log.Info("下载完成，SHA256 校验通过");
        }

        private static string ExtractExe(string zipPath, string destinationExe, string version)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("syncthing.exe", StringComparison.OrdinalIgnoreCase) && e.Length > 0)
                ?? throw new IOException("压缩包中没有找到 syncthing.exe");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationExe)!);

            var tmp = destinationExe + ".new";
            TryDelete(tmp);
            entry.ExtractToFile(tmp, true);

            // 旧文件保留为 .old，替换成功后清理，避免文件被占用导致更新失败
            var bak = destinationExe + ".old";
            TryDelete(bak);
            if (File.Exists(destinationExe)) File.Move(destinationExe, bak);
            File.Move(tmp, destinationExe);
            TryDelete(bak);

            Log.Info("Syncthing v" + version + " 已就位：" + destinationExe);
            return destinationExe;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}