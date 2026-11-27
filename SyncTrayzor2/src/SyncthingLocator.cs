using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncTrayzor2
{
    /// <summary>Syncthing 可执行文件定位、版本识别、命令行参数构建。核心：1.x 单横线 / 2.x 双横线自适应。</summary>
    internal static class SyncthingLocator
    {
        public static string Detect(string configured, string homePath)
        {
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var baseDir = AppPaths.BaseDir;
            var candidates = new List<string>
            {
                Path.Combine(baseDir, "data", "syncthing.exe"),
                Path.Combine(baseDir, "syncthing.exe"),
            };

            if (!string.IsNullOrWhiteSpace(homePath))
                candidates.Add(Path.Combine(homePath, "syncthing.exe"));

            // 便携版 SyncTrayzor 目录结构
            var parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                candidates.Add(Path.Combine(parent, "syncthing.exe"));
                candidates.Add(Path.Combine(parent, "data", "syncthing.exe"));
            }

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var p = Path.Combine(dir.Trim(), "syncthing.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }

            return "";
        }

        public static string DetectHome(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            var baseDir = AppPaths.BaseDir;
            var portableHome = Path.Combine(baseDir, "data", "syncthing");
            if (Directory.Exists(portableHome)) return portableHome;

            var parent = Directory.GetParent(baseDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var p2 = Path.Combine(parent, "data", "syncthing");
                if (Directory.Exists(p2)) return p2;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncthing");
        }

        /// <summary>运行 syncthing --version，返回如 "2.1.7"；失败返回空串。</summary>
        public static string GetVersion(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return "";
            try
            {
                var psi = new ProcessStartInfo(exePath, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(8000))
                {
                    try { p.Kill(true); } catch { }
                    return "";
                }

                var m = Regex.Match(output, @"syncthing\s+v?(\d+\.\d+\.\d+[^\s""]*)", RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;

                var m2 = Regex.Match(output, @"(\d+\.\d+\.\d+)");
                return m2.Success ? m2.Groups[1].Value : "";
            }
            catch (Exception ex)
            {
                Log.Error("检测 Syncthing 版本失败", ex);
                return "";
            }
        }

        public static bool IsVersion2(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return true; // 未知时按新版本处理
            var major = version.Split('.')[0];
            return int.TryParse(major, out var v) && v >= 2;
        }

        /// <summary>
        /// 构建启动参数。2.x 使用 "serve" 子命令 + 双横线长参数；1.x 使用单横线长参数。
        /// 同时把用户遗留的单横线长参数自动修正（AutoFixFlags）。
        /// </summary>
        public static List<string> BuildArguments(AppConfig cfg, string exePath, string home, string version, out List<string> notes)
        {
            notes = new List<string>();
            var v2 = IsVersion2(version);
            var args = new List<string>();

            if (v2) args.Add("serve");

            void Add(string name, string value = null)
            {
                var prefix = v2 ? "--" : "-";
                args.Add(value == null ? prefix + name : prefix + name + "=" + QuoteIfNeeded(value));
            }

            Add("no-browser");
            Add("no-console");
            Add("no-restart");
            if (cfg.DenyUpgrade) Add("no-upgrade");

            if (!string.IsNullOrWhiteSpace(home))
                Add("home", home);

            if (!string.IsNullOrWhiteSpace(cfg.GuiAddress))
                Add("gui-address", NormalizeAddress(cfg.GuiAddress));

            foreach (var raw in cfg.ExtraFlags ?? new List<string>())
            {
                foreach (var token in SplitFlags(raw))
                {
                    var fixedToken = FixFlag(token, v2, notes);
                    if (!string.IsNullOrWhiteSpace(fixedToken)) args.Add(fixedToken);
                }
            }

            return args;
        }

        private static string NormalizeAddress(string address)
        {
            address = address.Trim();
            if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return address;
            return "http://" + address;
        }

        private static string QuoteIfNeeded(string value)
        {
            if (value.Contains(' ') && !value.StartsWith("\""))
                return "\"" + value + "\"";
            return value;
        }

        public static IEnumerable<string> SplitFlags(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) yield break;
            // 支持一行多个参数（空格分隔），引号内的空格保留
            foreach (Match m in Regex.Matches(line, @"(?:""[^""]*""|\S)+"))
            {
                var token = m.Value.Trim();
                if (token.Length > 0) yield return token;
            }
        }

        /// <summary>2.x 下把 -no-browser 这类单横线长参数改成 --no-browser，避免 unknown flag -n。</summary>
        private static string FixFlag(string token, bool v2, List<string> notes)
        {
            if (!v2) return token;

            // 已经是双横线，或只是 "-h" 这类单字母短参数，原样保留
            if (token.StartsWith("--") || token.Length <= 2) return token;

            if (token[0] == '-' && token[1] != '-')
            {
                var fixedToken = "-" + token;
                notes.Add("已自动修正参数：" + token + " -> " + fixedToken);
                return fixedToken;
            }

            return token;
        }

        /// <summary>从 Syncthing 的 config.xml 读取 API Key。</summary>
        public static string ReadApiKey(string home)
        {
            try
            {
                var file = Path.Combine(home, "config.xml");
                if (!File.Exists(file)) return "";
                var text = File.ReadAllText(file);
                var m = Regex.Match(text, @"<apikey>([^<]*)</apikey>", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            catch (Exception ex)
            {
                Log.Error("读取 Syncthing API Key 失败", ex);
                return "";
            }
        }

        /// <summary>当配置里没有 API Key 时，生成一个并写回 config.xml（仅当文件存在且 Syncthing 未运行时调用）。</summary>
        public static string EnsureApiKey(string home)
        {
            var key = ReadApiKey(home);
            if (!string.IsNullOrEmpty(key)) return key;

            try
            {
                var file = Path.Combine(home, "config.xml");
                if (!File.Exists(file)) return "";

                key = Guid.NewGuid().ToString("N");
                var text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"<apikey>\s*</apikey>", RegexOptions.IgnoreCase))
                {
                    text = Regex.Replace(text, @"<apikey>\s*</apikey>", "<apikey>" + key + "</apikey>", RegexOptions.IgnoreCase);
                }
                else if (!Regex.IsMatch(text, @"<apikey>", RegexOptions.IgnoreCase))
                {
                    text = text.Replace("</configuration>", "    <apikey>" + key + "</apikey>\n</configuration>");
                }
                else
                {
                    return ReadApiKey(home);
                }

                File.WriteAllText(file, text, new UTF8Encoding(false));
                Log.Info("已为 Syncthing 生成并写入 API Key");
                return key;
            }
            catch (Exception ex)
            {
                Log.Error("写入 API Key 失败", ex);
                return "";
            }
        }
    }
}