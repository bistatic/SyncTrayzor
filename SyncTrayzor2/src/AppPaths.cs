using System;
using System.IO;

namespace SyncTrayzor2
{
    /// <summary>程序使用的目录与文件路径。优先使用程序目录（便携），不可写时回退到 %APPDATA%。</summary>
    internal static class AppPaths
    {
        public static string BaseDir { get; private set; }
        public static string DataDir { get; private set; }
        public static string LogDir { get; private set; }
        public static string ConfigFile { get; private set; }

        public static string SyncthingLogFile => Path.Combine(LogDir, "syncthing.log");
        public static string AppLogFile => Path.Combine(LogDir, "synctrayzor2.log");
        public static bool IsPortable { get; private set; }

        static AppPaths()
        {
            var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var dataDir = Path.Combine(exeDir, "data");
            if (CanWrite(dataDir))
            {
                BaseDir = exeDir;
                IsPortable = true;
            }
            else
            {
                BaseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SyncTrayzor2");
                IsPortable = false;
            }

            DataDir = Path.Combine(BaseDir, "data");
            LogDir = Path.Combine(BaseDir, "logs");
            ConfigFile = Path.Combine(DataDir, "config.json");
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把路径缩短为最后 N 段，便于在菜单/状态栏显示。</summary>
        public static string Shorten(string path, int segments = 3)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= segments) return path;

            var tail = new string[segments];
            Array.Copy(parts, parts.Length - segments, tail, 0, segments);
            return "...\\" + string.Join("\\", tail);
        }
    }
}