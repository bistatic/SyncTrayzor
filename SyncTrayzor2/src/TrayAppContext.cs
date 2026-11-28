using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SyncTrayzor2
{
    /// <summary>程序主控制器：托盘、进程、REST 轮询、通知、退出流程。</summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private readonly AppConfig _cfg;
        private readonly SyncthingApi _api = new();
        private readonly SyncthingProcess _proc = new();
        private readonly SynchronizationContext _ui;

        private NotifyIcon _tray;
        private MessageWindow _messageWindow;
        private MainForm _mainForm;
        private LogForm _logForm;
        private ConflictForm _conflictForm;

        private readonly System.Windows.Forms.Timer _pollTimer = new();
        private readonly System.Windows.Forms.Timer _animTimer = new();

        private TrayState _state = TrayState.Offline;
        private int _frame;
        private string _tooltip = "SyncTrayzor2 正在启动…";
        private string _apiKey = "";
        private bool _apiReachable;
        private bool _externalInstance;
        private bool _polling;
        private bool _paused;
        private bool _exiting;
        private bool _starting;
        private DateTime _lastConflictScan = DateTime.MinValue;

        private List<FolderConfig> _folders = new();
        private readonly Dictionary<string, DbStatus> _folderStatus = new();
        private readonly Dictionary<string, bool> _deviceConnected = new();
        private readonly Dictionary<string, string> _folderPrevState = new();
        private int _lastErrorCount;
        private bool _firstPollDone;
        private bool _loggedReady;

        public TrayAppContext(bool startMinimized)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var firstRun = !System.IO.File.Exists(AppPaths.ConfigFile);
            _cfg = AppConfig.Load();
            _cfg.Normalize();

            if (StartupManager.IsEnabled() != _cfg.StartOnLogin)
                StartupManager.Set(_cfg.StartOnLogin);

            _proc.OutputLine += OnSyncthingOutput;
            _proc.Exited += OnSyncthingExited;

            CreateTray();

            _messageWindow = new MessageWindow();
            _messageWindow.Activated += ShowMainWindow;

            _pollTimer.Interval = Math.Max(500, _cfg.PollIntervalMs);
            _pollTimer.Tick += OnPollTick;
            _animTimer.Interval = 140;
            _animTimer.Tick += OnAnimTick;

            _ = StartAsync(startMinimized && !firstRun);
        }

        private void CreateTray()
        {
            var menu = new ContextMenuStrip();
            _tray = new NotifyIcon
            {
                Icon = TrayIconFactory.GetTray(TrayState.Offline),
                Text = "SyncTrayzor2",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => ShowMainWindow();
            RebuildMenu();
        }

        private async Task StartAsync(bool startMinimized)
        {
            Log.Info("配置文件: " + AppPaths.ConfigFile);

            // 1. 先看是否已有可用的 Syncthing 在运行（外部实例）
            var key = ResolveApiKey();
            _api.Configure(_cfg.GuiAddress, key);
            _apiKey = key;

            if (await _api.IsAliveAsync().ConfigureAwait(true))
            {
                _externalInstance = true;
                _apiReachable = true;
                HideMainWindowWait();
                Log.Info("检测到已在运行的 Syncthing，直接连接: " + _api.BaseUrl);
            }
            else if (_cfg.StartSyncthingAutomatically)
            {
                // 未找到运行实例：先盖遮罩再走启动/下载流程
                ShowMainWindowWait("正在准备 Syncthing…", "未查询到正在运行的 Syncthing 实例，正在查找并启动主程序…");
                await StartSyncthingAsync().ConfigureAwait(true);
            }

            _pollTimer.Start();
            _animTimer.Start();
            _ = PollAsync();

            if (!startMinimized)
                ShowMainWindow();
        }

        private string ResolveApiKey()
        {
            var home = SyncthingLocator.DetectHome(_cfg.HomePath);
            return SyncthingLocator.ReadApiKey(home);
        }

        // ---------------- Syncthing 进程 ----------------

        private async Task<bool> StartSyncthingAsync()
        {
            if (_starting) return false;
            _starting = true;
            try
            {
                _proc.Stopping = false;

                // 启动前确保 API Key 可用（配置文件已存在时）
                var home = SyncthingLocator.DetectHome(_cfg.HomePath);
                var existingKey = SyncthingLocator.EnsureApiKey(home);
                if (!string.IsNullOrEmpty(existingKey))
                {
                    _apiKey = existingKey;
                    _api.Configure(_cfg.GuiAddress, existingKey);
                }

                if (!_proc.Start(_cfg, out var error))
                {
                    // 找不到 syncthing.exe 且允许自动下载：从官方 Release 拉最新版后就位重试
                    if (!string.IsNullOrEmpty(error)
                        && error.Contains("未找到 syncthing.exe")
                        && _cfg.AutoDownloadSyncthing)
                    {
                        ShowMainWindowWait("未找到 Syncthing 主程序",
                            "未查询到正在运行的 Syncthing 实例，且安装目录中没有 syncthing.exe。\n\n正在从官方 GitHub 下载最新版本，完成后将自动启动…");

                        if (!await TryAutoDownloadAsync().ConfigureAwait(true))
                        {
                            HideMainWindowWait();
                            return false;
                        }

                        ShowMainWindowWait("正在启动 Syncthing…", "下载完成，正在启动 Syncthing 进程…");
                        if (!_proc.Start(_cfg, out error))
                        {
                            HideMainWindowWait();
                            if (!string.IsNullOrEmpty(error))
                            {
                                SetState(TrayState.Error, error);
                                Notify("Syncthing 启动失败", error, ToolTipIcon.Error);
                            }
                            return false;
                        }
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(error))
                        {
                            SetState(TrayState.Error, error);
                            Notify("Syncthing 启动失败", error, ToolTipIcon.Error);

                            if (error.Contains("未找到 syncthing.exe"))
                            {
                                ShowMainWindowWait("未找到 Syncthing 主程序",
                                    "未查询到正在运行的 Syncthing 实例，且没有可用的 syncthing.exe。\n\n请在设置中指定 Syncthing 路径，或开启自动下载。点击关闭此页面后，\n可从托盘图标菜单打开设置。");
                                ShowSettings();
                            }
                        }
                        return false;
                    }
                }

                SetState(TrayState.Idle, "Syncthing 正在启动…");

                await WaitForApiReadyAsync().ConfigureAwait(true);

                return true;
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>等待 Syncthing API 就绪，期间轮询读取生成的 API Key（覆盖首次运行场景）。</summary>
        private async Task WaitForApiReadyAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(500).ConfigureAwait(true);
                if (!_proc.IsRunning) break;

                if (string.IsNullOrEmpty(_apiKey))
                {
                    var k = SyncthingLocator.ReadApiKey(SyncthingLocator.DetectHome(_cfg.HomePath));
                    if (!string.IsNullOrEmpty(k))
                    {
                        _apiKey = k;
                        _api.Configure(_cfg.GuiAddress, k);
                        Log.Info("已读取 Syncthing API Key");
                    }
                }

                if (await _api.IsAliveAsync().ConfigureAwait(true))
                {
                    _apiReachable = true;
                    HideMainWindowWait();
                    break;
                }
            }
        }

        /// <summary>下载官方最新版 syncthing.exe 到 data 目录（带 SHA256 校验与进度提示）。</summary>
        private async Task<bool> TryAutoDownloadAsync()
        {
            try
            {
                SetState(TrayState.Offline, "未找到 syncthing.exe，正在查询官方最新版…");
                var dest = Path.Combine(AppPaths.DataDir, "syncthing.exe");

                var progress = new Progress<(long received, long total)>(v =>
                {
                    var totalMb = v.total > 0 ? (v.total / 1048576.0) : 0;
                    var text = "正在下载 Syncthing… " + (v.received / 1048576.0).ToString("0.0") + " / " + totalMb.ToString("0.0") + " MB";
                    SetState(TrayState.Offline, text);
                    UpdateMainWindowWait("正在下载 Syncthing 最新版", text + "\n\n下载完成后将自动校验并启动 Syncthing。", false);
                });

                Log.Info("开始自动下载 Syncthing（官方最新版）");
                await SyncthingDownloader.DownloadAsync(dest, progress, CancellationToken.None).ConfigureAwait(true);

                Notify("下载完成", "Syncthing 最新版已就位，正在启动…", ToolTipIcon.Info);
                Log.Info("Syncthing 下载完成，重新尝试启动");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("自动下载 Syncthing 失败", ex);
                SetState(TrayState.Error, "Syncthing 下载失败，请手动放置 syncthing.exe");
                UpdateMainWindowWait("下载 Syncthing 失败",
                    "无法下载：" + ex.Message +
                    "\n\n请从 github.com/syncthing/syncthing/releases 手动下载 syncthing.exe，\n放到本程序 data 目录后，从托盘菜单选择“重启 Syncthing”。", false);
                Notify("下载失败",
                    "无法下载 Syncthing：" + ex.Message + "\n\n请从 github.com/syncthing/syncthing/releases 手动下载 syncthing.exe 放到程序 data 目录。",
                    ToolTipIcon.Error);
                return false;
            }
        }

        private void OnSyncthingOutput(string line)
        {
            // 只转发到内存日志，避免刷屏；完整输出已写入 logs\syncthing.log
            Log.Debug("[syncthing] " + line);
        }

        private void OnSyncthingExited(int exitCode)
        {
            _apiReachable = false;

            if (_exiting) return;

            if (_proc.Stopping)
            {
                SetState(TrayState.Offline, "Syncthing 已停止");
                return;
            }

            SetState(TrayState.Error, "Syncthing 异常退出（代码 " + exitCode + "）");

            if (_cfg.StartSyncthingAutomatically)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    _ui.Post(_ => { _ = StartSyncthingAsync(); }, null);
                });
            }
        }

        private async Task StopSyncthingAsync(bool forRestart)
        {
            _proc.Stopping = true;
            try
            {
                if (_apiReachable)
                {
                    Log.Info("请求 Syncthing 优雅退出");
                    await _api.ShutdownAsync().ConfigureAwait(true);
                }

                await Task.Run(() => _proc.WaitForExit(8000, out _)).ConfigureAwait(true);
            }
            finally
            {
                if (_proc.IsRunning) _proc.Kill();
                _apiReachable = false;
                _proc.Stopping = false;
            }

            if (!forRestart)
                SetState(TrayState.Offline, "Syncthing 已停止");
        }

        // ---------------- 轮询 ----------------

        private async void OnPollTick(object sender, EventArgs e)
        {
            await PollAsync().ConfigureAwait(true);
        }

        private async Task PollAsync()
        {
            if (_polling || _exiting) return;
            _polling = true;
            try
            {
                if (!_api.Configured || string.IsNullOrEmpty(_apiKey))
                {
                    var key = ResolveApiKey();
                    if (!string.IsNullOrEmpty(key))
                    {
                        _apiKey = key;
                        _api.Configure(_cfg.GuiAddress, key);
                    }
                }

                var alive = await _api.IsAliveAsync().ConfigureAwait(true);
                if (!alive)
                {
                    _apiReachable = false;
                    if (!_proc.IsRunning && !_externalInstance)
                        SetState(TrayState.Offline, "Syncthing 未运行");
                    else
                        SetState(TrayState.Idle, "等待 Syncthing 响应…");
                    return;
                }

                _apiReachable = true;

                var status = await _api.GetStatusAsync().ConfigureAwait(true);
                var conns = await _api.GetConnectionsAsync().ConfigureAwait(true);

                if (_folders.Count == 0 || DateTime.UtcNow - _lastFolderRefresh > TimeSpan.FromSeconds(60))
                {
                    _folders = await _api.GetFoldersAsync().ConfigureAwait(true);
                    _lastFolderRefresh = DateTime.UtcNow;
                }

                var syncing = false;
                var hasError = false;
                long needBytes = 0;
                var completedFolders = new List<string>();

                foreach (var folder in _folders)
                {
                    try
                    {
                        var db = await _api.GetDbStatusAsync(folder.Id).ConfigureAwait(true);
                        if (db == null) continue;
                        _folderStatus[folder.Id] = db;

                        var st = (db.State ?? "").ToLowerInvariant();
                        if (st == "syncing" || st == "scanning") syncing = true;
                        if (db.Errors > 0 || db.PullErrors > 0) hasError = true;
                        needBytes += Math.Max(0, db.NeedBytes);

                        _folderPrevState.TryGetValue(folder.Id, out var prev);
                        if ((prev == "syncing" || prev == "scanning") && st == "idle")
                            completedFolders.Add(string.IsNullOrWhiteSpace(folder.Label) ? folder.Id : folder.Label);
                        _folderPrevState[folder.Id] = st;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("读取文件夹 " + folder.Id + " 状态失败: " + ex.Message);
                    }
                }

                var connectedCount = 0;
                var connectionEvents = new List<string>();
                foreach (var kv in conns.Connections)
                {
                    if (kv.Value.Connected) connectedCount++;
                    if (_deviceConnected.TryGetValue(kv.Key, out var wasConnected) && wasConnected != kv.Value.Connected)
                    {
                        var shortId = kv.Key.Length > 7 ? kv.Key.Substring(0, 7) : kv.Key;
                        connectionEvents.Add((kv.Value.Connected ? "设备已连接 " : "设备已断开 ") + shortId + "…");
                    }
                    _deviceConnected[kv.Key] = kv.Value.Connected;
                }

                var errors = await _api.GetErrorsAsync().ConfigureAwait(true);
                var errorCount = errors?.Count ?? 0;
                if (errorCount > _lastErrorCount && _firstPollDone)
                {
                    Notify("Syncthing 错误", errors[errors.Count - 1], ToolTipIcon.Error);
                }
                _lastErrorCount = errorCount;

                if (_cfg.ShowNotifications && _firstPollDone)
                {
                    foreach (var msg in connectionEvents)
                        Notify("设备状态", msg, ToolTipIcon.Info);
                    foreach (var name in completedFolders)
                        Notify("同步完成", "文件夹「" + name + "」已完成同步", ToolTipIcon.Info);
                }

                if (syncing)
                    SetState(TrayState.Syncing, BuildSyncingText(needBytes, connectedCount));
                else if (hasError || errorCount > 0)
                    SetState(TrayState.Error, "存在同步错误，共 " + _folders.Count + " 个文件夹");
                else if (_paused)
                    SetState(TrayState.Paused, "同步已暂停");
                else
                    SetState(TrayState.Idle, BuildIdleText(connectedCount));

                _firstPollDone = true;

                if (!_loggedReady)
                {
                    _loggedReady = true;
                    Log.Info("已连接 Syncthing：文件夹 " + _folders.Count + " 个，在线设备 " + connectedCount + " 台");
                }

                if (_cfg.MonitorConflicts && DateTime.UtcNow - _lastConflictScan > TimeSpan.FromSeconds(60))
                {
                    _lastConflictScan = DateTime.UtcNow;
                    var count = ConflictScanner.CountConflicts(_folders);
                    if (count > 0)
                        Notify("发现冲突文件", "共有 " + count + " 个冲突文件，可在托盘菜单中处理", ToolTipIcon.Warning);
                }

                RebuildMenu();
            }
            catch (Exception ex)
            {
                Log.Debug("轮询失败: " + ex.Message);
            }
            finally
            {
                _polling = false;
            }
        }

        private DateTime _lastFolderRefresh = DateTime.MinValue;
        private bool _versionProbed;
        private string _cachedVersion = "";

        private string BuildIdleText(int connected)
        {
            return "已连接 " + connected + " 台设备 · " + _folders.Count + " 个文件夹 · 空闲";
        }

        private string BuildSyncingText(long needBytes, int connected)
        {
            var size = FormatBytes(needBytes);
            return "同步中 · 剩余 " + size + " · " + connected + " 台设备在线";
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return value.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
        }

        private void SetState(TrayState state, string tooltip)
        {
            _tooltip = tooltip;
            if (_state != state)
            {
                _state = state;
                _frame = 0;
                _tray.Icon = TrayIconFactory.GetTray(state, 0);
                RebuildMenu();
            }
            _tray.Text = Truncate(tooltip, 120);
            if (_mainForm != null && !_mainForm.IsDisposed)
                _mainForm.UpdateStatus(state, tooltip);
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            if (_state != TrayState.Syncing) return;
            _frame = (_frame + 1) % 8;
            _tray.Icon = TrayIconFactory.GetTray(TrayState.Syncing, _frame);
        }

        // ---------------- 托盘菜单 ----------------

        private void RebuildMenu()
        {
            var menu = _tray.ContextMenuStrip;
            if (menu == null) return;
            menu.Items.Clear();

            var open = new ToolStripMenuItem("打开 Syncthing 界面") { Font = new Font(menu.Font, FontStyle.Bold) };
            open.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(open);

            var version = SyncthingVersion;
            var exeInfo = new ToolStripMenuItem(
                "当前：Syncthing " + (string.IsNullOrEmpty(version) ? "" : "v" + version)
                + " · " + AppPaths.Shorten(EffectiveExePath, 2))
            {
                Enabled = false,
                ToolTipText = "可执行文件：" + EffectiveExePath + "\n数据目录：" + EffectiveHomePath
            };
            menu.Items.Add(exeInfo);

            var stateItem = new ToolStripMenuItem(_tooltip) { Enabled = false };
            menu.Items.Add(stateItem);
            menu.Items.Add(new ToolStripSeparator());

            if (_folders.Count > 0)
            {
                foreach (var folder in _folders)
                {
                    var name = string.IsNullOrWhiteSpace(folder.Label) ? folder.Id : folder.Label;
                    string text;
                    if (_folderStatus.TryGetValue(folder.Id, out var db))
                    {
                        var st = (db.State ?? "unknown").ToLowerInvariant();
                        if (st == "syncing" || st == "scanning")
                            text = name + ": 同步中 " + FormatBytes(db.NeedBytes);
                        else if (db.Errors > 0 || db.PullErrors > 0)
                            text = name + ": 有错误";
                        else
                            text = name + ": 已同步";
                    }
                    else text = name + ": --";

                    var item = new ToolStripMenuItem(text) { Enabled = false };
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
            }

            var pause = new ToolStripMenuItem(_paused ? "恢复同步" : "暂停同步");
            pause.Click += async (s, e) => await TogglePauseAsync();
            menu.Items.Add(pause);

            var restart = new ToolStripMenuItem("重启 Syncthing");
            restart.Enabled = _proc.IsRunning || _externalInstance;
            restart.Click += async (s, e) => await RestartSyncthingAsync();
            menu.Items.Add(restart);

            var download = new ToolStripMenuItem("下载最新版 Syncthing…");
            download.Click += async (s, e) => await DownloadLatestAsync();
            menu.Items.Add(download);

            menu.Items.Add(new ToolStripSeparator());

            var conflicts = new ToolStripMenuItem("冲突文件…");
            conflicts.Click += (s, e) => ShowConflictWindow();
            menu.Items.Add(conflicts);

            var settings = new ToolStripMenuItem("设置…");
            settings.Click += (s, e) => ShowSettings();
            menu.Items.Add(settings);

            var logs = new ToolStripMenuItem("查看日志…");
            logs.Click += (s, e) => ShowLogWindow();
            menu.Items.Add(logs);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("退出");
            exit.Click += async (s, e) => await ExitAsync();
            menu.Items.Add(exit);
        }

        private async Task TogglePauseAsync()
        {
            if (!_apiReachable)
            {
                Notify("无法操作", "Syncthing 尚未就绪", ToolTipIcon.Warning);
                return;
            }

            var target = !_paused;
            var ok = await _api.SetPausedAsync(target).ConfigureAwait(true);
            if (ok)
            {
                _paused = target;
                SetState(target ? TrayState.Paused : TrayState.Idle, target ? "同步已暂停" : "同步已恢复");
                RebuildMenu();
            }
            else
            {
                Notify("操作失败", "无法切换暂停状态，请查看日志", ToolTipIcon.Error);
            }
        }

        private async Task RestartSyncthingAsync()
        {
            if (_externalInstance)
            {
                var r = MessageBox.Show(
                    "当前连接的是外部启动的 Syncthing 实例，SyncTrayzor2 无法管理其进程。\n\n是否仅重新连接？",
                    "SyncTrayzor2", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r == DialogResult.OK)
                {
                    _apiReachable = await _api.IsAliveAsync().ConfigureAwait(true);
                    SetState(_apiReachable ? TrayState.Idle : TrayState.Offline, _apiReachable ? "已重新连接" : "无法连接");
                }
                return;
            }

            Log.Info("用户请求重启 Syncthing");
            await StopSyncthingAsync(true).ConfigureAwait(true);
            await Task.Delay(500).ConfigureAwait(true);
            await StartSyncthingAsync().ConfigureAwait(true);
            _apiReachable = await _api.IsAliveAsync().ConfigureAwait(true);
        }

        /// <summary>手动触发：停止当前实例 → 下载官方最新版 → 重新启动。</summary>
        private async Task DownloadLatestAsync()
        {
            if (_externalInstance)
            {
                MessageBox.Show(
                    "当前连接的是外部启动的 Syncthing 实例。\n可下载最新版到本程序 data 目录，但不会接管外部进程。",
                    "SyncTrayzor2", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            }
            else if (_proc.IsRunning)
            {
                var r = MessageBox.Show(
                    "更新需要先停止当前 Syncthing 进程，是否继续？",
                    "下载最新版 Syncthing", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                Log.Info("用户请求手动更新 Syncthing，先停止当前进程");
                await StopSyncthingAsync(true).ConfigureAwait(true);
            }

            if (await TryAutoDownloadAsync().ConfigureAwait(true))
            {
                if (!_externalInstance)
                {
                    await Task.Delay(500).ConfigureAwait(true);
                    await StartSyncthingAsync().ConfigureAwait(true);
                }
            }
        }

        // ---------------- 窗口 ----------------

        private void ShowMainWindow()
        {
            if (_mainForm == null || _mainForm.IsDisposed)
            {
                _mainForm = new MainForm(this);
                _mainForm.FormClosed += (s, e) => _mainForm = null;
                // 新建窗口时若 API 未就绪，立即盖上等待遮罩
                if (!_apiReachable)
                    _mainForm.ShowWait("正在准备 Syncthing…", "未查询到正在运行的 Syncthing 实例，正在连接/启动…");
            }
            _mainForm.Show();
            if (_mainForm.WindowState == FormWindowState.Minimized)
                _mainForm.WindowState = FormWindowState.Normal;
            _mainForm.Activate();
            _mainForm.BringToFront();
        }

        /// <summary>显示主窗口（隐藏到托盘的情况下则唤出）并盖上等待遮罩。</summary>
        private void ShowMainWindowWait(string title, string detail)
        {
            ShowMainWindow();
            _mainForm?.ShowWait(title, detail);
        }

        /// <summary>更新遮罩文字（窗口未创建时忽略）。</summary>
        private void UpdateMainWindowWait(string title, string detail, bool bringFront)
        {
            if (_mainForm == null || _mainForm.IsDisposed) return;
            if (bringFront) ShowMainWindow();
            _mainForm.ShowWait(title, detail);
        }

        /// <summary>API 就绪后撤掉遮罩；若窗口可见则加载界面。</summary>
        private void HideMainWindowWait()
        {
            if (_mainForm == null || _mainForm.IsDisposed) return;
            _mainForm.HideWait(true);
        }

        private void ShowSettings()
        {
            using var form = new SettingsForm(_cfg, EffectiveExePath, EffectiveHomePath, SyncthingVersion);
            if (form.ShowDialog() == DialogResult.OK)
            {
                form.ApplyTo(_cfg);
                _cfg.Save();

                if (StartupManager.IsEnabled() != _cfg.StartOnLogin)
                    StartupManager.Set(_cfg.StartOnLogin);

                _pollTimer.Interval = Math.Max(500, _cfg.PollIntervalMs);
                RebuildMenu();

                if (MessageBox.Show("设置已保存。是否立即重启 Syncthing 使设置生效？",
                        "SyncTrayzor2", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    _ = RestartSyncthingAsync();
                }
            }
        }

        private void ShowLogWindow()
        {
            if (_logForm == null || _logForm.IsDisposed)
            {
                _logForm = new LogForm();
                _logForm.FormClosed += (s, e) => _logForm = null;
            }
            _logForm.Show();
            _logForm.Activate();
            _logForm.BringToFront();
        }

        private void ShowConflictWindow()
        {
            if (_conflictForm == null || _conflictForm.IsDisposed)
            {
                _conflictForm = new ConflictForm(_folders);
                _conflictForm.FormClosed += (s, e) => _conflictForm = null;
            }
            _conflictForm.Show();
            _conflictForm.Activate();
            _conflictForm.BringToFront();
        }

        private void Notify(string title, string message, ToolTipIcon icon)
        {
            if (!_cfg.ShowNotifications && icon == ToolTipIcon.Info) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = message;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(6000);
            }
            catch { }
        }

        // ---------------- 供窗口使用 ----------------

        public AppConfig Config => _cfg;
        public SyncthingApi Api => _api;
        public SyncthingProcess Process => _proc;
        public bool ApiReachable => _apiReachable;
        public string GuiUrl => _api.BaseUrl + "/?key=" + Uri.EscapeDataString(_apiKey ?? "");

        /// <summary>当前实际使用的 syncthing.exe（配置为空时为自动探测结果）。</summary>
        public string EffectiveExePath
        {
            get
            {
                if (!string.IsNullOrEmpty(_proc.ExePath)) return _proc.ExePath;
                return SyncthingLocator.Detect(_cfg.SyncthingPath, SyncthingLocator.DetectHome(_cfg.HomePath));
            }
        }

        /// <summary>当前实际使用的配置/数据目录。</summary>
        public string EffectiveHomePath =>
            !string.IsNullOrEmpty(_proc.HomePath) ? _proc.HomePath : SyncthingLocator.DetectHome(_cfg.HomePath);

        public string SyncthingVersion
        {
            get
            {
                if (!string.IsNullOrEmpty(_proc.Version)) return _proc.Version;
                if (!_versionProbed)
                {
                    _versionProbed = true;
                    _cachedVersion = SyncthingLocator.GetVersion(EffectiveExePath);
                }
                return _cachedVersion ?? "";
            }
        }
        public IReadOnlyList<FolderConfig> Folders => _folders;
        public IReadOnlyDictionary<string, DbStatus> FolderStatus => _folderStatus;
        public bool Paused => _paused;

        public async Task TogglePauseFromUiAsync() => await TogglePauseAsync();
        public async Task RestartFromUiAsync() => await RestartSyncthingAsync();
        public void OpenSettingsFromUi() => ShowSettings();
        public void OpenLogFromUi() => ShowLogWindow();

        // ---------------- 退出 ----------------

        public async Task ExitAsync()
        {
            if (_exiting) return;
            _exiting = true;

            try
            {
                Log.Info("用户请求退出");
                _pollTimer.Stop();
                _animTimer.Stop();

                if (_cfg.StopSyncthingOnExit && !_externalInstance && _proc.IsRunning)
                {
                    try
                    {
                        _proc.Stopping = true;
                        if (_apiReachable) await _api.ShutdownAsync().ConfigureAwait(true);
                        await Task.Run(() => _proc.WaitForExit(6000, out _)).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("退出时停止 Syncthing 失败: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("退出流程异常", ex);
            }
            finally
            {
                try
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                catch { }

                _mainForm?.Dispose();
                _logForm?.Dispose();
                _conflictForm?.Dispose();
                _messageWindow?.DestroyHandle();
                _proc.Dispose();
                _api.Dispose();

                ExitThread();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pollTimer.Dispose();
                _animTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}