using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncTrayzor2
{
    internal sealed class AppConfig
    {
        public string SyncthingPath { get; set; } = "";
        public string HomePath { get; set; } = "";
        public List<string> ExtraFlags { get; set; } = new();

        public bool StartSyncthingAutomatically { get; set; } = true;
        public bool StartOnLogin { get; set; } = true;
        public bool StartMinimized { get; set; } = true;
        public bool CloseToTray { get; set; } = true;
        public bool StopSyncthingOnExit { get; set; } = true;

        public bool DenyUpgrade { get; set; } = true;
        public bool AutoFixFlags { get; set; } = true;
        public bool AutoDownloadSyncthing { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool MonitorConflicts { get; set; } = true;

        public string GuiAddress { get; set; } = "127.0.0.1:8384";
        public string Priority { get; set; } = "Normal";
        public int PollIntervalMs { get; set; } = 2000;

        [JsonIgnore] public string LoadedFrom { get; set; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load()
        {
            var path = AppPaths.ConfigFile;
            if (!File.Exists(path))
            {
                var fresh = new AppConfig();
                fresh.LoadedFrom = path;
                return fresh;
            }

            try
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                cfg.Normalize();
                cfg.LoadedFrom = path;
                return cfg;
            }
            catch (Exception ex)
            {
                Log.Error("读取配置文件失败，已备份并使用默认配置", ex);
                try { File.Copy(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                return new AppConfig { LoadedFrom = path };
            }
        }

        public void Normalize()
        {
            if (ExtraFlags == null) ExtraFlags = new List<string>();
            if (string.IsNullOrWhiteSpace(GuiAddress)) GuiAddress = "127.0.0.1:8384";
            if (string.IsNullOrWhiteSpace(Priority)) Priority = "Normal";
            if (PollIntervalMs < 500) PollIntervalMs = 500;
            if (PollIntervalMs > 60000) PollIntervalMs = 60000;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
                var json = JsonSerializer.Serialize(this, JsonOptions);
                var tmp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, AppPaths.ConfigFile, true);
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }
    }
}