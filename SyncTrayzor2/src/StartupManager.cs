using System;
using System.IO;
using Microsoft.Win32;

namespace SyncTrayzor2
{
    /// <summary>登录自启（HKCU Run）。</summary>
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SyncTrayzor2";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                var value = key?.GetValue(ValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
                if (key == null) return;

                if (enabled)
                {
                    var exe = Path.Combine(AppContext.BaseDirectory, "SyncTrayzor2.exe");
                    var command = "\"" + (File.Exists(exe) ? exe : Application.ExecutablePath) + "\" -minimized";
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                    Log.Info("已设置登录自启: " + command);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    Log.Info("已取消登录自启");
                }
            }
            catch (Exception ex)
            {
                Log.Error("设置登录自启失败", ex);
            }
        }
    }
}