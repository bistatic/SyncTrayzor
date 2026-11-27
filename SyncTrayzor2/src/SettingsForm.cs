using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
namespace SyncTrayzor2
{
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox _syncthingPath = new();
        private readonly TextBox _homePath = new();
        private readonly TextBox _guiAddress = new();
        private readonly TextBox _apiKeyBox = new() { ReadOnly = true };
        private readonly TextBox _extraFlags = new() { Multiline = true, Height = 130, ScrollBars = ScrollBars.Vertical };
        private readonly CheckBox _startSyncthing = new() { Text = "启动 SyncTrayzor2 时同时启动 Syncthing", AutoSize = true };
        private readonly CheckBox _startOnLogin = new() { Text = "登录 Windows 时自动运行", AutoSize = true };
        private readonly CheckBox _startMinimized = new() { Text = "启动后最小化到托盘", AutoSize = true };
        private readonly CheckBox _closeToTray = new() { Text = "关闭窗口时隐藏到托盘", AutoSize = true };
        private readonly CheckBox _stopOnExit = new() { Text = "退出 SyncTrayzor2 时停止 Syncthing", AutoSize = true };
        private readonly CheckBox _denyUpgrade = new() { Text = "禁止 Syncthing 自动升级（推荐，避免 2.x 参数不兼容）", AutoSize = true };
        private readonly CheckBox _autoDownload = new() { Text = "找不到 syncthing.exe 时自动下载官方最新版（需联网，带 SHA256 校验）", AutoSize = true };
        private readonly CheckBox _autoFixFlags = new() { Text = "自动把单横线长参数修正为 2.x 双横线格式", AutoSize = true };
        private readonly CheckBox _notifications = new() { Text = "显示设备连接 / 同步完成通知", AutoSize = true };
        private readonly CheckBox _monitorConflicts = new() { Text = "监控并提示冲突文件", AutoSize = true };
        private readonly ComboBox _priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        private readonly NumericUpDown _pollInterval = new() { Minimum = 500, Maximum = 60000, Increment = 500, Width = 120 };
        private readonly Label _effectiveLabel = new()
        {
            AutoSize = true,
            ForeColor = Color.FromArgb(0x33, 0x33, 0x33),
            MaximumSize = new Size(660, 0)
        };
        private readonly string _effExe;
        private readonly string _effHome;
        private readonly string _effVersion;

        public SettingsForm(AppConfig cfg, string effectiveExe, string effectiveHome, string effectiveVersion)
        {
            _effExe = effectiveExe ?? "";
            _effHome = effectiveHome ?? "";
            _effVersion = effectiveVersion ?? "";

            Text = "设置 - SyncTrayzor2";
            Size = new Size(820, 660);
            MinimumSize = new Size(760, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = TrayIconFactory.App;
            Font = new Font("Microsoft YaHei UI", 9f);

            _priority.Items.AddRange(new object[] { "Normal", "BelowNormal", "AboveNormal", "High", "Idle" });

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(BuildGeneralTab());
            tabs.TabPages.Add(BuildSyncthingTab());
            tabs.TabPages.Add(BuildAdvancedTab());
            Controls.Add(tabs);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(10) };
            var ok = new Button { Text = "确定", Width = 96, Height = 32, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "取消", Width = 96, Height = 32, DialogResult = DialogResult.Cancel };
            ok.Location = new Point(bottom.Width - 216, 12);
            cancel.Location = new Point(bottom.Width - 112, 12);
            bottom.Resize += (s, e) =>
            {
                ok.Location = new Point(bottom.Width - 216, 12);
                cancel.Location = new Point(bottom.Width - 112, 12);
            };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);

            AcceptButton = ok;
            CancelButton = cancel;

            _syncthingPath.TextChanged += (s, e) => UpdateEffectiveInfo();
            _homePath.TextChanged += (s, e) => UpdateEffectiveInfo();

            LoadFrom(cfg);
        }

        /// <summary>由候选路径推出文件对话框的初始目录：优先目录本身，其次文件所在目录，均无效则回退桌面。</summary>
        private static string ResolveInitialDir(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    var trimmed = candidate.Trim().Trim('"');
                    if (Directory.Exists(trimmed)) return trimmed;
                    var dir = Path.GetDirectoryName(trimmed);
                    if (Directory.Exists(dir)) return dir;
                }
                catch { }
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private TabPage BuildGeneralTab()
        {
            var page = new TabPage("常规") { Padding = new Padding(4) };
            var layout = NewLayout();

            AddRow(layout, null, _startOnLogin);
            AddRow(layout, null, _startMinimized);
            AddRow(layout, null, _closeToTray);
            AddRow(layout, null, _notifications);
            AddRow(layout, null, _monitorConflicts);

            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildSyncthingTab()
        {
            var page = new TabPage("Syncthing") { Padding = new Padding(4) };
            var layout = NewLayout();

            var browseExe = new Button { Text = "浏览…", Width = 80, Height = 26 };
            browseExe.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Filter = "Syncthing (syncthing.exe)|syncthing.exe|可执行文件 (*.exe)|*.exe",
                    Title = "选择 syncthing.exe",
                    InitialDirectory = ResolveInitialDir(
                        !string.IsNullOrWhiteSpace(_syncthingPath.Text) ? _syncthingPath.Text : _effExe)
                };
                if (dlg.ShowDialog(this) == DialogResult.OK) _syncthingPath.Text = dlg.FileName;
            };
            AddRow(layout, "Syncthing 路径", WithButton(_syncthingPath, browseExe));

            var browseHome = new Button { Text = "浏览…", Width = 80, Height = 26 };
            browseHome.Click += (s, e) =>
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "选择 Syncthing 配置与数据目录",
                    SelectedPath = !string.IsNullOrWhiteSpace(_homePath.Text) ? _homePath.Text.Trim()
                        : (!string.IsNullOrWhiteSpace(_effHome) ? _effHome
                            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                };
                if (dlg.ShowDialog(this) == DialogResult.OK) _homePath.Text = dlg.SelectedPath;
            };
            AddRow(layout, "配置/数据目录", WithButton(_homePath, browseHome));

            AddRow(layout, null, _startSyncthing);
            AddRow(layout, null, _stopOnExit);
            AddRow(layout, null, _denyUpgrade);
            AddRow(layout, null, _autoDownload);

            AddRow(layout, "进程优先级", _priority);
            AddRow(layout, "GUI 地址", _guiAddress);
            AddRow(layout, "API Key", _apiKeyBox);
            AddRow(layout, "实际使用", _effectiveLabel);

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = Color.DimGray,
                Text = "留空路径时自动探测：程序目录\\data\\syncthing.exe → 程序目录\\syncthing.exe → 系统 PATH。\n" +
                       "API Key 从配置目录下的 config.xml 自动读取，只读显示。"
            };
            AddRow(layout, "", hint);

            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildAdvancedTab()
        {
            var page = new TabPage("高级") { Padding = new Padding(4) };
            var layout = NewLayout();

            AddRow(layout, null, _autoFixFlags);
            AddRow(layout, "状态刷新间隔(ms)", _pollInterval);

            var hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(680, 0),
                ForeColor = Color.DimGray,
                Text = "额外命令行参数，一行一个，不需要写 syncthing.exe 或 serve。\n" +
                       "2.x 必须使用双横线，例如：--log-level=DEBUG、--no-port-probing。\n" +
                       "如果误填 -no-browser 这类单横线参数，勾选上面的修正项会自动转换为 --no-browser。"
            };
            AddRow(layout, "", hint);
            AddRow(layout, "额外参数", _extraFlags);

            page.Controls.Add(layout);
            return page;
        }

        private static TableLayoutPanel NewLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoScroll = true,
                Padding = new Padding(14),
                GrowStyle = TableLayoutPanelGrowStyle.AddRows
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return layout;
        }

        private static void AddRow(TableLayoutPanel layout, string label, Control control)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            if (label != null)
            {
                layout.Controls.Add(new Label
                {
                    Text = label,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(3, 7, 3, 3)
                }, 0, row);
            }

            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            control.Margin = new Padding(3, 6, 3, 6);
            layout.Controls.Add(control, label == null ? 1 : 1, row);

            if (label == null)
                layout.SetColumnSpan(control, 1);
        }

        private static Panel WithButton(TextBox box, Button button)
        {
            var panel = new Panel { Height = 30, Dock = DockStyle.Fill };
            box.Dock = DockStyle.Fill;
            button.Dock = DockStyle.Right;
            panel.Controls.Add(box);
            panel.Controls.Add(button);
            return panel;
        }

        private void LoadFrom(AppConfig cfg)
        {
            _syncthingPath.Text = cfg.SyncthingPath;
            _homePath.Text = cfg.HomePath;
            _guiAddress.Text = cfg.GuiAddress;
            _priority.SelectedItem = cfg.Priority;
            if (_priority.SelectedIndex < 0) _priority.SelectedIndex = 0;
            _pollInterval.Value = Math.Max(_pollInterval.Minimum, Math.Min(_pollInterval.Maximum, cfg.PollIntervalMs));
            _extraFlags.Text = string.Join(Environment.NewLine, cfg.ExtraFlags ?? new System.Collections.Generic.List<string>());

            _startSyncthing.Checked = cfg.StartSyncthingAutomatically;
            _startOnLogin.Checked = cfg.StartOnLogin;
            _startMinimized.Checked = cfg.StartMinimized;
            _closeToTray.Checked = cfg.CloseToTray;
            _stopOnExit.Checked = cfg.StopSyncthingOnExit;
            _denyUpgrade.Checked = cfg.DenyUpgrade;
            _autoDownload.Checked = cfg.AutoDownloadSyncthing;
            _autoFixFlags.Checked = cfg.AutoFixFlags;
            _notifications.Checked = cfg.ShowNotifications;
            _monitorConflicts.Checked = cfg.MonitorConflicts;

            var home = SyncthingLocator.DetectHome(cfg.HomePath);
            var key = SyncthingLocator.ReadApiKey(home);
            _apiKeyBox.Text = string.IsNullOrEmpty(key) ? "（尚未生成，启动 Syncthing 后自动读取）" : key;

            _syncthingPath.PlaceholderText = _effExe;
            _homePath.PlaceholderText = _effHome;
            UpdateEffectiveInfo();
        }

        private void UpdateEffectiveInfo()
        {
            var exe = string.IsNullOrWhiteSpace(_syncthingPath.Text) ? _effExe : _syncthingPath.Text.Trim();
            var home = string.IsNullOrWhiteSpace(_homePath.Text) ? _effHome : _homePath.Text.Trim();

            var ver = string.IsNullOrWhiteSpace(_syncthingPath.Text) && !string.IsNullOrEmpty(_effVersion)
                ? "（v" + _effVersion + "）"
                : "";

            _effectiveLabel.Text = (string.IsNullOrEmpty(exe) ? "未找到 syncthing.exe" : exe + ver)
                                   + "\n数据目录：" + (string.IsNullOrEmpty(home) ? "（未确定）" : home)
                                   + "\n\n路径留空即自动探测：程序目录\\data\\syncthing.exe → 程序目录\\syncthing.exe → 上级目录\\syncthing.exe → 系统 PATH。";
        }

        public void ApplyTo(AppConfig cfg)
        {
            cfg.SyncthingPath = _syncthingPath.Text.Trim();
            cfg.HomePath = _homePath.Text.Trim();
            cfg.GuiAddress = _guiAddress.Text.Trim();
            cfg.Priority = _priority.SelectedItem as string ?? "Normal";
            cfg.PollIntervalMs = (int)_pollInterval.Value;
            cfg.ExtraFlags = _extraFlags.Lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            cfg.StartSyncthingAutomatically = _startSyncthing.Checked;
            cfg.StartOnLogin = _startOnLogin.Checked;
            cfg.StartMinimized = _startMinimized.Checked;
            cfg.CloseToTray = _closeToTray.Checked;
            cfg.StopSyncthingOnExit = _stopOnExit.Checked;
            cfg.DenyUpgrade = _denyUpgrade.Checked;
            cfg.AutoDownloadSyncthing = _autoDownload.Checked;
            cfg.AutoFixFlags = _autoFixFlags.Checked;
            cfg.ShowNotifications = _notifications.Checked;
            cfg.MonitorConflicts = _monitorConflicts.Checked;
            cfg.Normalize();
        }
    }
}