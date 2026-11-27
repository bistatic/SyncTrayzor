using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SyncTrayzor2
{
    internal sealed class ConflictFile
    {
        public string Path { get; set; }
        public string FolderName { get; set; }
        public DateTime Modified { get; set; }
        public long Size { get; set; }
    }

    internal static class ConflictScanner
    {
        public static List<ConflictFile> Scan(IEnumerable<FolderConfig> folders)
        {
            var result = new List<ConflictFile>();
            if (folders == null) return result;

            foreach (var folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) continue;

                var name = string.IsNullOrWhiteSpace(folder.Label) ? folder.Id : folder.Label;
                try
                {
                    foreach (var file in EnumerateSafe(folder.Path))
                    {
                        var fileName = System.IO.Path.GetFileName(file);
                        if (fileName.IndexOf(".sync-conflict-", StringComparison.OrdinalIgnoreCase) < 0) continue;

                        try
                        {
                            var fi = new FileInfo(file);
                            result.Add(new ConflictFile
                            {
                                Path = file,
                                FolderName = name,
                                Modified = fi.LastWriteTime,
                                Size = fi.Length
                            });
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("扫描冲突文件失败（" + folder.Path + "）: " + ex.Message);
                }
            }

            return result.OrderByDescending(f => f.Modified).ToList();
        }

        public static int CountConflicts(IEnumerable<FolderConfig> folders) => Scan(folders).Count;

        private static IEnumerable<string> EnumerateSafe(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files;
                string[] dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch { continue; }

                foreach (var f in files) yield return f;
                foreach (var d in dirs)
                {
                    try
                    {
                        var attr = File.GetAttributes(d);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(d);
                }
            }
        }
    }

    internal sealed class ConflictForm : Form
    {
        private readonly IReadOnlyList<FolderConfig> _folders;
        private readonly ListView _list;
        private readonly Label _summary;
        private readonly Button _scanButton;
        private readonly Button _deleteButton;
        private readonly Button _openButton;
        private List<ConflictFile> _items = new();

        public ConflictForm(IReadOnlyList<FolderConfig> folders)
        {
            _folders = folders;
            Text = "冲突文件 - SyncTrayzor2";
            Size = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = TrayIconFactory.Get(TrayState.Error);
            Font = new Font("Microsoft YaHei UI", 9f);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = true,
                HideSelection = false
            };
            _list.Columns.Add("文件", 380);
            _list.Columns.Add("文件夹", 160);
            _list.Columns.Add("修改时间", 150);
            _list.Columns.Add("大小", 90);

            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            _summary = new Label { AutoSize = true, Location = new Point(10, 13), Text = "未扫描" };
            top.Controls.Add(_summary);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
            _scanButton = new Button { Text = "重新扫描", Width = 100, Height = 30, Location = new Point(8, 10) };
            _scanButton.Click += async (s, e) => await ScanAsync();

            _deleteButton = new Button { Text = "删除所选（回收站）", Width = 160, Height = 30, Location = new Point(116, 10) };
            _deleteButton.Click += (s, e) => DeleteSelected();

            _openButton = new Button { Text = "打开所在位置", Width = 120, Height = 30, Location = new Point(284, 10) };
            _openButton.Click += (s, e) => OpenSelectedLocation();

            var close = new Button { Text = "关闭", Width = 90, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            close.Location = new Point(bottom.Width - 100, 10);
            close.Click += (s, e) => Close();
            bottom.Resize += (s, e) => close.Location = new Point(bottom.Width - 100, 10);

            bottom.Controls.AddRange(new Control[] { _scanButton, _deleteButton, _openButton, close });

            Controls.Add(_list);
            Controls.Add(bottom);
            Controls.Add(top);

            Shown += async (s, e) => await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _scanButton.Enabled = false;
            _summary.Text = "正在扫描…";
            _list.Items.Clear();

            var folders = _folders;
            _items = await Task.Run(() => ConflictScanner.Scan(folders));

            foreach (var item in _items)
            {
                var lvi = new ListViewItem(item.Path);
                lvi.SubItems.Add(item.FolderName);
                lvi.SubItems.Add(item.Modified.ToString("yyyy-MM-dd HH:mm:ss"));
                lvi.SubItems.Add(TrayAppContext.FormatBytes(item.Size));
                lvi.Tag = item;
                _list.Items.Add(lvi);
            }

            _summary.Text = "共 " + _items.Count + " 个冲突文件";
            _scanButton.Enabled = true;
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要删除的冲突文件。", "SyncTrayzor2",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var count = _list.SelectedItems.Count;
            if (MessageBox.Show("将 " + count + " 个冲突文件移入回收站？", "确认删除",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var failed = 0;
            foreach (ListViewItem lvi in _list.SelectedItems.Cast<ListViewItem>().ToList())
            {
                var item = (ConflictFile)lvi.Tag;
                if (ShellFileOperations.Recycle(item.Path))
                    _list.Items.Remove(lvi);
                else
                    failed++;
            }

            _summary.Text = "共 " + _list.Items.Count + " 个冲突文件" + (failed > 0 ? "（" + failed + " 个删除失败）" : "");
        }

        private void OpenSelectedLocation()
        {
            var item = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ConflictFile : null;
            if (item == null) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + item.Path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error("打开文件位置失败", ex);
            }
        }
    }
}