using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SyncTrayzor2
{
    internal sealed class SystemStatus
    {
        [JsonPropertyName("myID")] public string MyId { get; set; }
        [JsonPropertyName("uptime")] public long Uptime { get; set; }
        [JsonPropertyName("goroutines")] public int Goroutines { get; set; }
    }

    internal sealed class ConnectionInfo
    {
        [JsonPropertyName("connected")] public bool Connected { get; set; }
        [JsonPropertyName("paused")] public bool Paused { get; set; }
        [JsonPropertyName("address")] public string Address { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("inBytesTotal")] public long InBytesTotal { get; set; }
        [JsonPropertyName("outBytesTotal")] public long OutBytesTotal { get; set; }
    }

    internal sealed class ConnectionsResponse
    {
        [JsonPropertyName("connections")] public Dictionary<string, ConnectionInfo> Connections { get; set; } = new();
    }

    internal sealed class FolderConfig
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; }
        [JsonPropertyName("path")] public string Path { get; set; }
        [JsonPropertyName("paused")] public bool Paused { get; set; }
    }

    internal sealed class DbStatus
    {
        [JsonPropertyName("state")] public string State { get; set; }
        [JsonPropertyName("needBytes")] public long NeedBytes { get; set; }
        [JsonPropertyName("needFiles")] public int NeedFiles { get; set; }
        [JsonPropertyName("globalBytes")] public long GlobalBytes { get; set; }
        [JsonPropertyName("globalFiles")] public int GlobalFiles { get; set; }
        [JsonPropertyName("localBytes")] public long LocalBytes { get; set; }
        [JsonPropertyName("inSyncBytes")] public long InSyncBytes { get; set; }
        [JsonPropertyName("errors")] public int Errors { get; set; }
        [JsonPropertyName("pullErrors")] public int PullErrors { get; set; }
    }

    internal sealed class SystemErrorResponse
    {
        [JsonPropertyName("errors")] public List<string> Errors { get; set; } = new();
    }

    internal sealed class SyncthingApi : IDisposable
    {
        private readonly HttpClient _http;
        private string _baseUrl = "http://127.0.0.1:8384";
        private string _apiKey = "";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public SyncthingApi()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                AllowAutoRedirect = true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        }

        public string BaseUrl => _baseUrl;
        public string ApiKey => _apiKey;
        public bool Configured { get; private set; }

        public void Configure(string address, string apiKey)
        {
            var addr = (address ?? "127.0.0.1:8384").Trim().TrimEnd('/');
            if (!addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                addr = "http://" + addr;
            _baseUrl = addr;
            _apiKey = apiKey ?? "";
            Configured = !string.IsNullOrEmpty(_apiKey);
        }

        private HttpRequestMessage Request(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, _baseUrl + path);
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
            return req;
        }

        public async Task<bool> IsAliveAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(_baseUrl + "/rest/noauth/health", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;

                if ((int)resp.StatusCode < 500)
                {
                    // Syncthing 1.x 没有 /rest/noauth/health（返回 403/404），用根路径确认 GUI 在线
                    using var root = await _http.GetAsync(_baseUrl + "/", ct).ConfigureAwait(false);
                    return (int)root.StatusCode < 500;
                }

                return false;
            }
            catch { return false; }
        }

        public async Task<SystemStatus> GetStatusAsync(CancellationToken ct = default)
        {
            using var req = Request(HttpMethod.Get, "/rest/system/status");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<SystemStatus>(JsonOptions, ct).ConfigureAwait(false);
        }

        public async Task<ConnectionsResponse> GetConnectionsAsync(CancellationToken ct = default)
        {
            using var req = Request(HttpMethod.Get, "/rest/system/connections");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ConnectionsResponse>(JsonOptions, ct).ConfigureAwait(false);
        }

        public async Task<List<FolderConfig>> GetFoldersAsync(CancellationToken ct = default)
        {
            using var req = Request(HttpMethod.Get, "/rest/config/folders");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<List<FolderConfig>>(JsonOptions, ct).ConfigureAwait(false)
                   ?? new List<FolderConfig>();
        }

        public async Task<DbStatus> GetDbStatusAsync(string folderId, CancellationToken ct = default)
        {
            using var req = Request(HttpMethod.Get, "/rest/db/status?folder=" + Uri.EscapeDataString(folderId));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<DbStatus>(JsonOptions, ct).ConfigureAwait(false);
        }

        public async Task<List<string>> GetErrorsAsync(CancellationToken ct = default)
        {
            using var req = Request(HttpMethod.Get, "/rest/system/error");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadFromJsonAsync<SystemErrorResponse>(JsonOptions, ct).ConfigureAwait(false);
            return data?.Errors ?? new List<string>();
        }

        /// <summary>暂停/恢复全部设备。2.x 需要逐设备调用，1.x 支持全局。</summary>
        public async Task<bool> SetPausedAsync(bool paused, CancellationToken ct = default)
        {
            var verb = paused ? "pause" : "resume";

            // 先尝试全局（1.x）
            using (var req = Request(HttpMethod.Post, "/rest/system/" + verb))
            {
                try
                {
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { }
            }

            // 2.x：逐设备
            try
            {
                var conns = await GetConnectionsAsync(ct).ConfigureAwait(false);
                var ok = false;
                foreach (var device in conns.Connections.Keys)
                {
                    using var req = Request(HttpMethod.Post,
                        "/rest/system/" + verb + "?device=" + Uri.EscapeDataString(device));
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    ok |= resp.IsSuccessStatusCode;
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log.Error("切换暂停状态失败", ex);
                return false;
            }
        }

        public async Task<bool> ShutdownAsync(CancellationToken ct = default)
        {
            try
            {
                using var req = Request(HttpMethod.Post, "/rest/system/shutdown");
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public void Dispose() => _http.Dispose();
    }
}