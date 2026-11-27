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
            Icon = TrayIconFactory.App;

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
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = Color.White
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 工具栏
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Web 界面
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // Syncthing 日志（5 行）
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));    // 状态栏

            _toolStrip.Dock = DockStyle.Fill;
            _toolStrip.Margin = Padding.Empty;
            _web.Dock = DockStyle.Fill;
            _web.Margin = Padding.Empty;
            status.Dock = DockStyle.Fill;
            status.Margin = Padding.Empty;

            layout.Controls.Add(_toolStrip, 0, 0);
            layout.Controls.Add(_web, 0, 1);
            layout.Controls.Add(BuildLogPanel(), 0, 2);
            layout.Controls.Add(status, 0, 3);

            SuspendLayout();
            Controls.Clear();
            Controls.Add(layout);
            ResumeLayout(true);

            SeedSyncthingLog();
            Log.LineAdded += AppendSyncthingLogLine;
            FormClosed += (s, e) => Log.LineAdded -= AppendSyncthingLogLine;

            Load += async (s, e) =>
            {
                await InitWebViewAsync();
            };

            FormClosing += OnFormClosing;
        }

        /// <summary>底部 Syncthing 日志区：固定 5 行，滚动查看更多。</summary>
        private TextBox _logBox;
        private bool _logAtBottom = true;

        private TableLayoutPanel BuildLogPanel()
        {
            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                BackColor = Color.White
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 标题行
            host.RowStyles.Add(new RowStyle(SizeType.Absolute));   // 日志框（5 行，高度下面算）

            var caption = new ToolStrip
            {
                Dock = DockStyle.Fill,
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.FromArgb(0xF0, 0xF0, 0xF0),
                Padding = new Padding(6, 1, 0, 0)
            };
            caption.Items.Add(new ToolStripLabel("Syncthing 日志")
            {
                Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x40, 0x40, 0x40)
            });
            caption.Items.Add(new ToolStripLabel("（最新在下，滚动查看历史）")
            {
                Font = new Font("Microsoft YaHei UI", 8.25f),
                ForeColor = Color.Gray
            });

            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(0xFA, 0xFA, 0xFA),
                ForeColor = Color.FromArgb(0x33, 0x36, 0x3B),
                Font = new Font("Consolas", 8.75f),
                Margin = new Padding(6, 2, 6, 2),
                ShortcutsEnabled = true
            };
            // 固定显示 5 行：行高×5 + 水平滚动条 + 边框/内边距
            var lineHeight = _logBox.Font.Height;
            _logBox.Height = lineHeight * 5
                             + SystemInformation.HorizontalScrollBarHeight
                             + SystemInformation.BorderSize.Height * 2 + 6;
            host.RowStyles[1].Height = _logBox.Height + 4;

            // 滚动定位：用户往上翻时暂停自动滚动，回到底部后恢复
            _logBox.Click += (s, e) => SyncLogBottomFlag();
            _logBox.KeyUp += (s, e) => SyncLogBottomFlag();
            _logBox.MouseWheel += (s, e) => SyncLogBottomFlag();

            host.Controls.Add(caption, 0, 0);
            host.Controls.Add(_logBox, 0, 1);
            return host;
        }

        private void SyncLogBottomFlag()
        {
            if (_logBox == null || _logBox.TextLength == 0) return;
            var lastPos = _logBox.GetPositionFromCharIndex(_logBox.TextLength - 1);
            _logAtBottom = lastPos.Y + _logBox.Font.Height >= _logBox.ClientSize.Height - _logBox.Font.Height;
        }

        private void AppendSyncthingLogLine(string rawLine)
        {
            if (_logBox == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendSyncthingLogLine), rawLine);
                return;
            }

            // 日志记录格式：时间 [级别] [syncthing] 原始输出 —— 只显示原始输出
            const string tag = "[syncthing] ";
            var idx = rawLine.IndexOf(tag, StringComparison.Ordinal);
            var line = idx >= 0 ? rawLine.Substring(idx + tag.Length) : null;
            if (line == null) return;

            _logBox.AppendText(line + Environment.NewLine);

            // 控制缓冲，避免长时间运行内存膨胀
            if (_logBox.Lines.Length > 3000)
            {
                var keep = new string[2000];
                Array.Copy(_logBox.Lines, _logBox.Lines.Length - 2000, keep, 0, 2000);
                var atBottom = _logAtBottom;
                _logBox.Lines = keep;
                _logAtBottom = atBottom;
            }

            if (_logAtBottom)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private void SeedSyncthingLog()
        {
            foreach (var line in Log.Snapshot())
                AppendSyncthingLogLineUnchecked(line);
            if (_logBox != null)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private void AppendSyncthingLogLineUnchecked(string rawLine)
        {
            const string tag = "[syncthing] ";
            var idx = rawLine.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return;
            _logBox.AppendText(rawLine.Substring(idx + tag.Length) + Environment.NewLine);
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

        /// <summary>中间遮罩面板：Syncthing 未就绪/下载时显示状态，避免 WebView2 显示 ERR_CONNECTION_REFUSED。</summary>
        private Panel _waitPanel;
        private Label _waitTitle;
        private Label _waitDetail;

        private void BuildWaitPanel()
        {
            if (_waitPanel != null) return;

            _waitPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(0xF5, 0xF7, 0xFA),
                Visible = false
            };

            _waitTitle = new Label
            {
                Text = "正在准备 Syncthing…",
                Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x2A, 0x2D, 0x34),
                AutoSize = true
            };
            _waitDetail = new Label
            {
                Text = "",
                Font = new Font("Microsoft YaHei UI", 10f),
                ForeColor = Color.FromArgb(0x5C, 0x64, 0x70),
                AutoSize = true,
                MaximumSize = new Size(540, 0)
            };
            var spinner = new Label
            {
                Text = "◐",
                Font = new Font("Segoe UI Symbol", 22f),
                ForeColor = Color.FromArgb(0x0B, 0x5E, 0xD7),
                AutoSize = true,
                Margin = new Padding(0, 0, 12, 0)
            };

            _waitTitle.TextAlign = ContentAlignment.MiddleLeft;
            _waitTitle.Height = 44;
            _waitTitle.AutoSize = false;

            var titleRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0)
            };
            spinner.Height = 38;
            spinner.TextAlign = ContentAlignment.MiddleLeft;
            titleRow.Controls.Add(spinner);
            titleRow.Controls.Add(_waitTitle);

            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.None
            };
            stack.Controls.Add(titleRow);
            stack.Controls.Add(new Label { AutoSize = false, Height = 12, Width = 1, Margin = new Padding(0) });
            stack.Controls.Add(_waitDetail);

            // 自动居中：布局或内容变化时把内容块固定在面板中央
            _waitPanel.Controls.Add(stack);
            void Center()
            {
                var pref = stack.GetPreferredSize(_waitPanel.ClientSize);
                stack.Location = new Point(
                    Math.Max(0, (_waitPanel.ClientSize.Width - pref.Width) / 2),
                    Math.Max(0, (_waitPanel.ClientSize.Height - pref.Height) / 2));
            }
            _waitPanel.Resize += (s, e) => Center();
            _waitPanel.Layout += (s, e) => Center();
            _waitDetail.TextChanged += (s, e) => Center();

            // spinner 转动动画
            var anim = new System.Windows.Forms.Timer { Interval = 120 };
            var frame = 0;
            anim.Tick += (s, e) =>
            {
                if (_waitPanel.Visible)
                {
                    frame = (frame + 1) % 4;
                    spinner.Text = new string(new[] { "◐◓◑◒"[frame] });
                }
            };
            anim.Start();

            var host = _web.Parent as TableLayoutPanel;
            if (host != null)
            {
                host.Controls.Add(_waitPanel, 0, 1);
                _waitPanel.BringToFront();
            }
        }

        /// <summary>Syncthing 未就绪时显示遮罩：未找到实例→下载提示；下载中→进度；失败→错误+手动指引。</summary>
        public void ShowWait(string title, string detail)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowWait(title, detail)));
                return;
            }
            BuildWaitPanel();
            _waitTitle.Text = title;
            _waitTitle.Width = TextRenderer.MeasureText(title, _waitTitle.Font).Width + 20;
            _waitDetail.Text = detail;
            _waitPanel.Visible = true;
            _waitPanel.BringToFront();
        }

        /// <summary>Syncthing 就绪后隐藏遮罩并加载界面。</summary>
        public void HideWait(bool navigate = true)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => HideWait(navigate)));
                return;
            }
            if (_waitPanel != null && _waitPanel.Visible)
            {
                _waitPanel.Visible = false;
                if (navigate) Navigate();
            }
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