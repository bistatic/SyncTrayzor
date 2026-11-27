using System;
using System.Collections.Generic;
using System.IO;

namespace SyncTrayzor2
{
    /// <summary>轻量日志：内存环形缓冲 + 文件追加，供日志窗口实时显示。</summary>
    internal static class Log
    {
        private static readonly object Gate = new();
        private static readonly Queue<string> Buffer = new();
        private const int MaxLines = 4000;
        private static bool _fileBroken;

        public static event Action<string> LineAdded;

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message) => Write("ERROR", message);

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + (ex == null ? "" : " -> " + ex.GetType().Name + ": " + ex.Message));
        }

        public static void Debug(string message) => Write("DEBUG", message);

        private static void Write(string level, string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            lock (Gate)
            {
                Buffer.Enqueue(line);
                while (Buffer.Count > MaxLines) Buffer.Dequeue();
            }

            try
            {
                if (!_fileBroken)
                {
                    RotateIfNeeded();
                    var encoding = new System.Text.UTF8Encoding(true);
                    if (!File.Exists(AppPaths.AppLogFile))
                        File.WriteAllText(AppPaths.AppLogFile, "", encoding);
                    File.AppendAllText(AppPaths.AppLogFile, line + Environment.NewLine, encoding);
                }
            }
            catch
            {
                _fileBroken = true;
            }

            try { LineAdded?.Invoke(line); } catch { }
        }

        private static void RotateIfNeeded()
        {
            var fi = new FileInfo(AppPaths.AppLogFile);
            if (fi.Exists && fi.Length > 4 * 1024 * 1024)
            {
                var backup = AppPaths.AppLogFile + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(AppPaths.AppLogFile, backup);
            }
        }

        public static string[] Snapshot()
        {
            lock (Gate)
            {
                return Buffer.ToArray();
            }
        }
    }
}