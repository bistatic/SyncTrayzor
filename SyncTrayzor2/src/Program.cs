using System;
using System.Threading;
using System.Windows.Forms;

namespace SyncTrayzor2
{
    internal static class Program
    {
        private const string MutexName = @"Local\SyncTrayzor2.SingleInstance-9C1D4B7A";

        [STAThread]
        private static void Main(string[] args)
        {
            using var mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // 已有实例：广播激活消息后退出
                NativeMethods.PostMessage((IntPtr)NativeMethods.HwndBroadcast, NativeMethods.WmActivateApp, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            ApplicationConfiguration.Initialize();

            Application.ThreadException += (s, e) =>
            {
                Log.Error("界面线程异常", e.Exception);
                MessageBox.Show("发生错误：" + e.Exception.Message + "\n\n详情已写入日志。",
                    "SyncTrayzor2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("未处理异常", e.ExceptionObject as Exception);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Error("未观察的任务异常", e.Exception);
                e.SetObserved();
            };

            var startMinimized = Array.Exists(args, a =>
                a.Equals("-minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            Log.Info("================ SyncTrayzor2 启动 ================");
            Log.Info("程序目录: " + AppPaths.BaseDir + (AppPaths.IsPortable ? "（便携模式）" : "（用户目录模式）"));
            Log.Info("版本: " + typeof(Program).Assembly.GetName().Version);

            try
            {
                var context = new TrayAppContext(startMinimized);
                Application.Run(context);
            }
            catch (Exception ex)
            {
                Log.Error("程序启动失败", ex);
                MessageBox.Show("程序启动失败：" + ex.Message, "SyncTrayzor2",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Log.Info("================ SyncTrayzor2 退出 ================");
                mutex.ReleaseMutex();
            }
        }
    }
}