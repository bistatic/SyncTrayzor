using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SyncTrayzor2
{
    /// <summary>Syncthing 子进程的生命周期管理：启动、输出采集、退出监测、优雅停止。</summary>
    internal sealed class SyncthingProcess : IDisposable
    {
        private readonly object _gate = new();
        private readonly object _fileGate = new();
        private Process _process;
        private StreamWriter _logWriter;
        private readonly List<DateTime> _recentStarts = new();

        public string ExePath { get; private set; } = "";
        public string HomePath { get; private set; } = "";
        public string Version { get; private set; } = "";
        public bool IsVersion2 { get; private set; } = true;
        public List<string> ActiveArguments { get; private set; } = new();
        public bool Stopping { get; set; }
        public int Pid => _process != null && !_process.HasExited ? _process.Id : 0;
        public bool IsRunning
        {
            get
            {
                try { return _process != null && !_process.HasExited; }
                catch { return false; }
            }
        }

        public event Action<string> OutputLine;
        public event Action<int> Exited;

        public bool Start(AppConfig cfg, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (IsRunning) return false;

                var home = SyncthingLocator.DetectHome(cfg.HomePath);
                var exe = SyncthingLocator.Detect(cfg.SyncthingPath, home);
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    error = "未找到 syncthing.exe，请在设置中指定路径。";
                    Log.Error(error);
                    return false;
                }

                if (!PruneAndCheckRestartBudget(out var suppressedIn))
                {
                    error = "Syncthing 在一分钟内反复退出，已暂停自动重启（" + suppressedIn + " 秒后重试）。请查看日志。";
                    Log.Warn(error);
                    return false;
                }

                Version = SyncthingLocator.GetVersion(exe);
                IsVersion2 = SyncthingLocator.IsVersion2(Version);
                ExePath = exe;
                HomePath = home;

                var args = SyncthingLocator.BuildArguments(cfg, exe, home, Version, out var notes);
                foreach (var note in notes) Log.Info(note);
                ActiveArguments = args;

                try
                {
                    Directory.CreateDirectory(home);
                }
                catch (Exception ex)
                {
                    Log.Warn("创建 Syncthing 数据目录失败: " + ex.Message);
                }

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppPaths.BaseDir,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                Log.Info("启动 Syncthing v" + (string.IsNullOrEmpty(Version) ? "?" : Version)
                         + " (" + (IsVersion2 ? "2.x 语法" : "1.x 语法") + ")");
                Log.Info("可执行文件: " + exe);
                Log.Info("命令行参数: " + string.Join(" ", args));
                Log.Info("数据目录: " + home);

                try
                {
                    _recentStarts.Add(DateTime.UtcNow);
                    _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _process.OutputDataReceived += (s, e) => OnOutput(e.Data);
                    _process.ErrorDataReceived += (s, e) => OnOutput(e.Data);
                    _process.Exited += (s, e) => OnProcessExited();
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    TrySetPriority(_process, cfg.Priority);
                    return true;
                }
                catch (Exception ex)
                {
                    error = "启动 Syncthing 失败: " + ex.Message;
                    Log.Error(error, ex);
                    return false;
                }
            }
        }

        private bool PruneAndCheckRestartBudget(out int secondsUntilRetry)
        {
            secondsUntilRetry = 0;
            _recentStarts.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 60);
            if (_recentStarts.Count >= 4)
            {
                var oldest = _recentStarts[0];
                secondsUntilRetry = Math.Max(1, 60 - (int)(DateTime.UtcNow - oldest).TotalSeconds);
                return false;
            }
            return true;
        }

        private void TrySetPriority(Process p, string priority)
        {
            try
            {
                switch ((priority ?? "Normal").ToLowerInvariant())
                {
                    case "idle": p.PriorityClass = ProcessPriorityClass.Idle; break;
                    case "belownormal": p.PriorityClass = ProcessPriorityClass.BelowNormal; break;
                    case "abovenormal": p.PriorityClass = ProcessPriorityClass.AboveNormal; break;
                    case "high": p.PriorityClass = ProcessPriorityClass.High; break;
                    default: p.PriorityClass = ProcessPriorityClass.Normal; break;
                }
            }
            catch { }
        }

        private void OnOutput(string line)
        {
            if (line == null) return;
            try
            {
                lock (_fileGate)
                {
                    if (_logWriter == null)
                    {
                        Directory.CreateDirectory(AppPaths.LogDir);
                        _logWriter = new StreamWriter(AppPaths.SyncthingLogFile, true, new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    _logWriter.WriteLine(line);
                }
            }
            catch { }

            try { OutputLine?.Invoke(line); } catch { }
        }

        private void OnProcessExited()
        {
            int code = -1;
            try { code = _process?.ExitCode ?? -1; } catch { }

            lock (_fileGate)
            {
                try { _logWriter?.Flush(); _logWriter?.Dispose(); } catch { }
                _logWriter = null;
            }

            Log.Info("Syncthing 进程已退出，退出码 " + code + (Stopping ? "（主动停止）" : "（异常退出）"));
            try { Exited?.Invoke(code); } catch { }
        }

        /// <summary>等待进程退出；超时则强制结束进程树。</summary>
        public bool WaitForExit(int timeoutMs, out bool killed)
        {
            killed = false;
            try
            {
                var p = _process;
                if (p == null) return true;
                if (p.WaitForExit(timeoutMs)) return true;

                killed = true;
                Log.Warn("Syncthing 未在 " + timeoutMs + "ms 内退出，强制结束进程树");
                try { p.Kill(true); } catch { }
                try { p.WaitForExit(5000); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("停止 Syncthing 失败", ex);
                return false;
            }
        }

        public void Kill()
        {
            try
            {
                Stopping = true;
                if (_process != null && !_process.HasExited)
                {
                    Log.Warn("强制结束 Syncthing 进程");
                    _process.Kill(true);
                }
            }
            catch (Exception ex)
            {
                Log.Error("结束 Syncthing 进程失败", ex);
            }
        }

        public void Dispose()
        {
            lock (_fileGate)
            {
                try { _logWriter?.Dispose(); } catch { }
                _logWriter = null;
            }
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }
}