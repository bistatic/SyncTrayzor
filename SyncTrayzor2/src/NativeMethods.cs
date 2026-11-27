using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SyncTrayzor2
{
    internal static class NativeMethods
    {
        public const int HwndBroadcast = 0xffff;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static readonly int WmActivateApp = (int)RegisterWindowMessage("SyncTrayzor2.Activate-4F2C9C6E");
    }

    /// <summary>接收第二个实例广播的激活消息，避免重复启动。</summary>
    internal sealed class MessageWindow : NativeWindow
    {
        public event Action Activated;

        public MessageWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "SyncTrayzor2.MessageWindow",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = 0
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WmActivateApp)
            {
                try { Activated?.Invoke(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }
    }
}