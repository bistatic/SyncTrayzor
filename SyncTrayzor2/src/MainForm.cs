using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SyncTrayzor2
{
    /// <summary>主窗口：内置 WebView2 显示 Syncthing 网页界面，附状态栏与快捷操作。</summary>
    internal sealed class MainForm : Form
    {
        private readonly TrayAppContext _ctx;
        private readonly WebView2 _web;
        private readonly ToolStrip _toolStrip;
        private readonly ToolStripStatusLabel _statusLabel;
        private readonly ToolStripStatusLabel _deviceLabel;
        private readonly ToolStripStatusLabel _pathLabel;
        private readonly ToolStripButton _pauseButton;
        private bool _webReady;
        private bool _forceClose;

        public MainForm(TrayAppContext ctx)
        {
            _ctx = ctx;
            Text = "SyncTrayzor2 - Syncthing";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1120, 760);
            MinimumSize = new Size(720, 480);
            Icon = TrayIconFactory.Get(TrayState.Idle);

            _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(16, 16) };

            var openExternal = new ToolStripButton("在浏览器中打开");
            openExternal.Click += (s, e) => OpenExternal();
            _toolStrip.Items.Add(openExternal);

            var refresh = new ToolStripButton("刷新");
            refresh.Click += (s, e) => Navigate();
            _toolStrip.Items.Add(refresh);

            _toolStrip.Items.Add(new ToolStripSeparator());

            _pauseButton = new ToolStripButton("暂停同步");
            _pauseButton.Click += async (s, e) =>
            {
                await _ctx.TogglePauseFromUiAsync();
                UpdatePauseButton();
            };
            _toolStrip.Items.Add(_pauseButton);

            var restart = new ToolStripButton("重启 Syncthing");
            restart.Click += async (s, e) => await _ctx.RestartFromUiAsync();
            _toolStrip.Items.Add(restart);

            _toolStrip.Items.Add(new ToolStripSeparator());

            var settings = new ToolStripButton("设置");
            settings.Click += (s, e) => _ctx.OpenSettingsFromUi();
            _toolStrip.Items.Add(settings);

            var logs = new ToolStripButton("日志");
            logs.Click += (s, e) => _ctx.OpenLogFromUi();
            _toolStrip.Items.Add(logs);

            Controls.Add(_toolStrip);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);

            var status = new StatusStrip();
            _statusLabel = new ToolStripStatusLabel("正在启动…") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _deviceLabel = new ToolStripStatusLabel("");
            _pathLabel = new ToolStripStatusLabel("") { BorderSides = ToolStripStatusLabelBorderSides.Left };
            status.Items.Add(_statusLabel);
            status.Items.Add(_deviceLabel);
            status.Items.Add(_pathLabel);
            Controls.Add(status);

            // 用 TableLayoutPanel 明确分行，避免工具栏与 Web 内容重叠
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _toolStrip.Dock = DockStyle.Fill;
            _toolStrip.Margin = Padding.Empty;
            _web.Dock = DockStyle.Fill;
            _web.Margin = Padding.Empty;
            status.Dock = DockStyle.Fill;
            status.Margin = Padding.Empty;

            layout.Controls.Add(_toolStrip, 0, 0);
            layout.Controls.Add(_web, 0, 1);
            layout.Controls.Add(status, 0, 2);

            SuspendLayout();
            Controls.Clear();
            Controls.Add(layout);
            ResumeLayout(true);

            Load += async (s, e) =>
            {
                await InitWebViewAsync();
            };

            FormClosing += OnFormClosing;
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                var userData = Path.Combine(AppPaths.DataDir, "webview2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await _web.EnsureCoreWebView2Async(env);
                _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                _web.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    if (args.IsSuccess)
                        Log.Info("内置浏览器已加载 Syncthing 界面");
                    else
                        Log.Warn("内置浏览器加载失败: " + args.WebErrorStatus);
                };
                _webReady = true;
                Navigate();
            }
            catch (Exception ex)
            {
                Log.Error("初始化内置浏览器失败，将使用系统浏览器", ex);
                _statusLabel.Text = "内置浏览器不可用，请使用“在浏览器中打开”";
                ShowFallbackPanel();
            }
        }

        private void ShowFallbackPanel()
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var label = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "内置浏览器（WebView2）初始化失败。\n\n请点击上方“在浏览器中打开”，或安装 Microsoft Edge WebView2 Runtime。",
                Font = new Font("Microsoft YaHei UI", 11f)
            };
            var button = new Button
            {
                Text = "在浏览器中打开",
                AutoSize = true,
                Anchor = AnchorStyles.None
            };
            button.Click += (s, e) => OpenExternal();
            panel.Controls.Add(button);
            panel.Controls.Add(label);
            Controls.Add(panel);
        }

        private void Navigate()
        {
            if (!_webReady) return;

            var url = _ctx.GuiUrl;
            if (string.IsNullOrEmpty(_ctx.Api.ApiKey))
                url = _ctx.Api.BaseUrl + "/";

            try
            {
                _web.Source = new Uri(url);
                Log.Info("打开 Syncthing 界面: " + _ctx.Api.BaseUrl);
            }
            catch (Exception ex)
            {
                Log.Error("打开界面失败", ex);
            }
        }

        private void OpenExternal()
        {
            try
            {
                Process.Start(new ProcessStartInfo(_ctx.GuiUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("调用系统浏览器失败", ex);
            }
        }

        private void UpdatePauseButton()
        {
            _pauseButton.Text = _ctx.Paused ? "恢复同步" : "暂停同步";
        }

        public void UpdateStatus(TrayState state, string tooltip)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateStatus(state, tooltip)));
                return;
            }

            _statusLabel.Text = tooltip;
            _deviceLabel.Text = state switch
            {
                TrayState.Syncing => "同步中",
                TrayState.Paused => "已暂停",
                TrayState.Error => "错误",
                TrayState.Offline => "离线",
                _ => "空闲"
            };

            var version = _ctx.SyncthingVersion;
            _pathLabel.Text = (string.IsNullOrEmpty(version) ? "Syncthing" : "v" + version)
                              + " · " + AppPaths.Shorten(_ctx.EffectiveHomePath, 2);
            _pathLabel.ToolTipText = "可执行文件：" + _ctx.EffectiveExePath + "\n数据目录：" + _ctx.EffectiveHomePath;

            UpdatePauseButton();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            Log.Info("窗口关闭请求: reason=" + e.CloseReason + ", CloseToTray=" + _ctx.Config.CloseToTray + ", force=" + _forceClose);

            if (_forceClose) return;

            if (e.CloseReason == CloseReason.UserClosing && _ctx.Config.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // 其余情况（点 X 且未勾选“关闭到托盘”、任务管理器关闭、系统关机等）都退出程序
            e.Cancel = true;
            Hide();
            _ = _ctx.ExitAsync();
        }

        public void ForceClose()
        {
            _forceClose = true;
            Close();
        }
    }
}