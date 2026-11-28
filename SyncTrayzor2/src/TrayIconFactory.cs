using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SyncTrayzor2
{
    internal enum TrayState
    {
        Idle,
        Syncing,
        Paused,
        Error,
        Offline
    }

    /// <summary>动态绘制托盘图标：底色表示状态，白色双向箭头表示同步，同步中整体旋转。</summary>
    internal static class TrayIconFactory
    {
        private static Icon _appIcon;

        /// <summary>程序官方图标（原版 SyncTrayzor 图标，从 exe 内嵌资源读取），用于各窗体。</summary>
        public static Icon App
        {
            get
            {
                if (_appIcon != null) return _appIcon;
                try
                {
                    var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch
                {
                    _appIcon = Get(TrayState.Idle);
                }
                return _appIcon;
            }
        }

        private static readonly Dictionary<string, Icon> StateCache = new();

        /// <summary>
        /// 托盘版图标：原版程序图标做底，仅在有事件时叠加右上角小色点
        /// （绿=同步中闪烁、红=错误、灰=暂停），空闲/离线为纯原图标，保持原汁原味。
        /// </summary>
        public static Icon GetTray(TrayState state, int frame = 0)
        {
            var key = state + ":" + (state == TrayState.Syncing ? frame % 2 : 0);
            lock (StateCache)
            {
                if (StateCache.TryGetValue(key, out var cached)) return cached;

                var icon = (Icon)App.Clone();
                if (state == TrayState.Idle || state == TrayState.Offline)
                {
                    StateCache[key] = icon;
                    return icon;
                }

                // 叠加状态角标：8px 圆点 + 白描边，位于右上角
                using var bmp = icon.ToBitmap();
                const int size = 32;
                using var canvas = new Bitmap(size, size);
                using (var g = Graphics.FromImage(canvas))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, size, size);

                    var dotColor = state switch
                    {
                        TrayState.Syncing => frame % 2 == 0 ? Color.FromArgb(0x12, 0x8C, 0x3E) : Color.FromArgb(0x1F, 0xB2, 0x57),
                        TrayState.Error => Color.FromArgb(0xC0, 0x2B, 0x2B),
                        TrayState.Paused => Color.FromArgb(0x8A, 0x94, 0xA6),
                        _ => Color.Transparent
                    };

                    if (dotColor != Color.Transparent)
                    {
                        using var dot = new SolidBrush(dotColor);
                        g.FillEllipse(dot, 19, 1, 12, 12);
                        using var ring = new Pen(Color.White, 2f);
                        g.DrawEllipse(ring, 19, 1, 12, 12);
                    }
                }

                var hIcon = canvas.GetHicon();
                try
                {
                    using var tmp = Icon.FromHandle(hIcon);
                    icon = (Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }

                StateCache[key] = icon;
                return icon;
            }
        }
        private static readonly Dictionary<string, Icon> Cache = new();
        private const int Frames = 8;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Get(TrayState state, int frame = 0)
        {
            if (state != TrayState.Syncing) frame = 0;
            var key = state + ":" + (frame % Frames);
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;

                var icon = Render(state, frame % Frames);
                Cache[key] = icon;
                return icon;
            }
        }

        private static Icon Render(TrayState state, int frame)
        {
            const int size = 32;
            using var bmp = new Bitmap(size, size);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var bg = state switch
            {
                TrayState.Idle => Color.FromArgb(0x0B, 0x5E, 0xD7),
                TrayState.Syncing => Color.FromArgb(0x12, 0x8C, 0x3E),
                TrayState.Paused => Color.FromArgb(0x8A, 0x94, 0xA6),
                TrayState.Error => Color.FromArgb(0xC0, 0x2B, 0x2B),
                _ => Color.FromArgb(0x6B, 0x72, 0x80)
            };

            using (var brush = new SolidBrush(bg))
            {
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);
            }

            var angle = state == TrayState.Syncing ? frame * (360f / Frames) : 0f;
            g.TranslateTransform(size / 2f, size / 2f);
            g.RotateTransform(angle);
            g.TranslateTransform(-size / 2f, -size / 2f);

            if (state == TrayState.Paused)
            {
                using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, 12, 10, 12, 22);
                g.DrawLine(pen, 20, 10, 20, 22);
            }
            else if (state == TrayState.Error)
            {
                using var pen = new Pen(Color.White, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, 16, 9, 16, 19);
                g.FillEllipse(Brushes.White, 14.4f, 21.5f, 3.2f, 3.2f);
            }
            else
            {
                using var pen = new Pen(Color.White, 2.6f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.ArrowAnchor,
                    CustomEndCap = new AdjustableArrowCap(2.2f, 2.2f, true)
                };
                g.DrawLine(pen, 10, 12.5f, 22, 12.5f);
                g.DrawLine(pen, 22, 19.5f, 10, 19.5f);
            }

            var hIcon = bmp.GetHicon();
            try
            {
                using var tmp = Icon.FromHandle(hIcon);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
    }
}