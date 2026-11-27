using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace SyncTrayzor2
{
    internal sealed class LogForm : Form
    {
        private readonly TextBox _text;
        private readonly CheckBox _autoScroll;

        public LogForm()
        {
            Text = "日志 - SyncTrayzor2";
            Size = new Size(1000, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = TrayIconFactory.App;
            Font = new Font("Consolas", 9f);

            _text = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.None
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };

            var openApp = new Button { Text = "打开程序日志", Width = 120, Height = 30, Location = new Point(8, 9) };
            openApp.Click += (s, e) => OpenFile(AppPaths.AppLogFile);

            var openSt = new Button { Text = "打开 Syncthing 日志", Width = 145, Height = 30, Location = new Point(136, 9) };
            openSt.Click += (s, e) => OpenFile(AppPaths.SyncthingLogFile);

            var clear = new Button { Text = "清空显示", Width = 90, Height = 30, Location = new Point(289, 9) };
            clear.Click += (s, e) => _text.Clear();

            _autoScroll = new CheckBox
            {
                Text = "自动滚动",
                Checked = true,
                AutoSize = true,
                Location = new Point(390, 15)
            };

            var copy = new Button { Text = "复制全部", Width = 90, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            copy.Location = new Point(bottom.Width - 100, 9);
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_text.Text); } catch { }
            };
            bottom.Resize += (s, e) => copy.Location = new Point(bottom.Width - 100, 9);

            bottom.Controls.AddRange(new Control[] { openApp, openSt, clear, _autoScroll, copy });

            Controls.Add(_text);
            Controls.Add(bottom);

            Load += (s, e) =>
            {
                _text.Text = string.Join(Environment.NewLine, Log.Snapshot());
                ScrollToEnd();
                Log.LineAdded += OnLineAdded;
            };

            FormClosed += (s, e) => Log.LineAdded -= OnLineAdded;
        }

        private void OnLineAdded(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(OnLineAdded), line); } catch { }
                return;
            }

            // 控制显示行数，避免长时间运行后卡顿
            if (_text.Lines.Length > 5000)
                _text.Text = string.Join(Environment.NewLine, Log.Snapshot());

            _text.AppendText(line + Environment.NewLine);
            if (_autoScroll.Checked) ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }

        private static void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show("日志文件尚不存在：\n" + path, "SyncTrayzor2",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开日志文件失败", ex);
            }
        }
    }
}