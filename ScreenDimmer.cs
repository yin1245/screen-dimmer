using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("分屏息屏")]
[assembly: System.Reflection.AssemblyDescription("Screen blackout controls for individual Windows monitors")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

namespace ScreenDimmer
{
    internal static class Native
    {
        internal static readonly uint ShowMessage = RegisterWindowMessage("ScreenDimmer.ShowControl");
        internal static readonly uint ToggleMessage = RegisterWindowMessage("ScreenDimmer.Toggle");
        internal static readonly uint ApplyMessage = RegisterWindowMessage("ScreenDimmer.Apply");
        internal static readonly uint RestoreMessage = RegisterWindowMessage("ScreenDimmer.Restore");
        internal static readonly IntPtr HwndTopmost = new IntPtr(-1);
        internal static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        internal const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")]
        internal static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("kernel32.dll")]
        internal static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string String;
            public uint State;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }

        /// <summary>Switches the calling thread to per-monitor DPI awareness (Windows 10 1703+).
        /// Returns the previous context, or IntPtr.Zero when unsupported.</summary>
        internal static IntPtr EnterPerMonitorDpi()
        {
            try { return SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }

        internal static void LeaveDpi(IntPtr previous)
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }

        /// <summary>Physical-pixel bounds of every monitor, keyed by GDI device name.</summary>
        internal static Dictionary<string, Rectangle> PhysicalMonitorBounds()
        {
            var result = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
            IntPtr previous = EnterPerMonitorDpi();
            if (previous == IntPtr.Zero) return result;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data)
                {
                    var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
                    if (GetMonitorInfo(monitor, ref info))
                        result[info.Device] = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                    return true;
                }, IntPtr.Zero);
            }
            finally { LeaveDpi(previous); }
            return result;
        }

        /// <summary>Stable identifier of the physical monitor attached to a GDI device such as \\.\DISPLAY2.
        /// GDI device numbers change after reboots and docking; the monitor interface path does not.</summary>
        internal static string MonitorKey(string deviceName)
        {
            var info = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
            if (EnumDisplayDevices(deviceName, 0, ref info, 1) && !string.IsNullOrEmpty(info.Id))
                return info.Id;
            return deviceName;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool background = HasArg(args, "--background");
            uint command = HasArg(args, "--toggle") ? Native.ToggleMessage :
                HasArg(args, "--apply") ? Native.ApplyMessage :
                HasArg(args, "--restore") ? Native.RestoreMessage : 0;
            bool created;
            using (var instance = new Mutex(true, @"Local\ScreenDimmer.Desktop", out created))
            {
                if (!created)
                {
                    if (command != 0)
                        Native.PostMessage(new IntPtr(0xffff), command, IntPtr.Zero, IntPtr.Zero);
                    else if (!background)
                        Native.PostMessage(new IntPtr(0xffff), Native.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                if (command == Native.RestoreMessage) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                StartupSettings.RepairIfMoved();
                using (var window = new ControlWindow())
                using (var context = new ApplicationContext())
                {
                    window.FormClosed += delegate { context.ExitThread(); };
                    if (!background && command == 0) window.Show();
                    if (command != 0) window.RunCommand(command);
                    Application.Run(context);
                }
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal static class StartupSettings
    {
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "ScreenDimmer";
        internal static string CommandLine { get { return "\"" + Application.ExecutablePath + "\" --background"; } }

        internal static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && string.Equals(key.GetValue(ValueName) as string, CommandLine, StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>Points an existing login entry at this copy when the program file it names no longer exists.</summary>
        internal static void RepairIfMoved()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value) || string.Equals(value, CommandLine, StringComparison.OrdinalIgnoreCase)) return;
                    string path = value.StartsWith("\"") ? value.Substring(1, Math.Max(0, value.IndexOf('"', 1) - 1)) : value.Split(' ')[0];
                    if (!File.Exists(path)) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                }
            }
            catch (Exception) { }
        }
    }

    internal static class SelectionStore
    {
        private const string KeyPath = @"Software\ScreenDimmer";
        private const string ValueName = "SelectedMonitors";

        /// <summary>Saved monitor keys, or null when the user has never chosen.</summary>
        internal static HashSet<string> Load()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    var values = key == null ? null : key.GetValue(ValueName) as string[];
                    return values == null ? null : new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { return null; }
        }

        internal static void Save(IEnumerable<string> monitorKeys)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue(ValueName, monitorKeys.ToArray(), RegistryValueKind.MultiString);
            }
            catch (Exception) { }
        }
    }

    internal sealed class MonitorCard : CheckBox
    {
        internal readonly Screen Display;
        internal readonly string MonitorKey;
        internal int Number { get; private set; }
        internal bool Blanked;
        private static readonly Color Accent = Color.FromArgb(114, 158, 255);

        internal MonitorCard(Screen display, int number)
        {
            Display = display;
            MonitorKey = Native.MonitorKey(display.DeviceName);
            Number = number;
            Appearance = Appearance.Button;
            AutoSize = false;
            Size = new Size(304, 174);
            Margin = new Padding(0, 0, 16, 0);
            Cursor = Cursors.Hand;
            AccessibleName = "屏幕 " + number + (display.Primary ? " 主屏" : " 副屏") + " 息屏选择";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Width / 304f;
            g.ScaleTransform(scale, scale);
            Rectangle box = new Rectangle(1, 1, 301, 171);
            using (var fill = new SolidBrush(Checked ? Color.FromArgb(30, 44, 69) : Color.FromArgb(28, 32, 41)))
                g.FillRectangle(fill, box);
            using (var edge = new Pen(Checked ? Accent : Color.FromArgb(52, 59, 73), Checked ? 2 : 1))
                g.DrawRectangle(edge, box);

            using (var monitor = new Pen(Checked ? Accent : Color.FromArgb(157, 167, 186), 2))
            {
                g.DrawRectangle(monitor, 22, 22, 44, 29);
                g.DrawLine(monitor, 44, 51, 44, 58);
                g.DrawLine(monitor, 32, 59, 56, 59);
            }
            DrawText(g, Number.ToString(), new Rectangle(24, 24, 40, 25), 11, Checked ? Accent : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            DrawText(g, "屏幕 " + Number, new Rectangle(82, 20, 140, 28), 13, Color.White);
            DrawText(g, Display.Primary ? "主屏" : "副屏", new Rectangle(82, 50, 140, 24), 9, Color.FromArgb(169, 179, 197));
            DrawText(g, Display.Bounds.Width + " × " + Display.Bounds.Height + "  ·  " +
                (Display.Bounds.Left == 0 && Display.Bounds.Top == 0 ? "桌面原点" :
                "位置 " + Display.Bounds.Left + ", " + Display.Bounds.Top),
                new Rectangle(22, 87, 270, 24), 9, Color.FromArgb(169, 179, 197));

            using (var border = new Pen(Checked ? Accent : Color.FromArgb(111, 122, 142), 1.5f))
                g.DrawRectangle(border, 23, 133, 15, 15);
            if (Checked)
            {
                using (var pen = new Pen(Accent, 2))
                    g.DrawLines(pen, new[] { new Point(26, 140), new Point(30, 144), new Point(36, 136) });
            }
            DrawText(g, Blanked ? "正在息屏" : Checked ? "已选 · 应用后息屏" : "保持显示",
                new Rectangle(49, 127, 235, 28), 10, Checked ? Accent : Color.FromArgb(200, 207, 220));
            if (Focused)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(8, 8, 287, 157), Accent, BackColor);
        }

        private static void DrawText(Graphics g, string text, Rectangle bounds, float size, Color color,
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        {
            using (var font = new Font("Microsoft YaHei UI", size))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center,
                Alignment = (flags & TextFormatFlags.HorizontalCenter) != 0 ? StringAlignment.Center : StringAlignment.Near,
                FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, bounds, format);
        }
    }

    internal sealed class BlackoutWindow : Form
    {
        private const int WmSetCursor = 0x20, WmDpiChanged = 0x02E0;
        private readonly Action restore;
        private readonly Rectangle physical;
        internal readonly string DeviceName;

        internal BlackoutWindow(Screen display, Rectangle physicalBounds, Action restoreScreen, Action restoreAll)
        {
            DeviceName = display.DeviceName;
            physical = physicalBounds;
            restore = restoreScreen;
            Text = "分屏息屏 · " + display.DeviceName;
            BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = display.Bounds;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("恢复这块屏幕", null, delegate { restore(); });
            menu.Items.Add("全部恢复", null, delegate { restoreAll(); });
            ContextMenuStrip = menu;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: never listed in Alt+Tab
                return parameters;
            }
        }

        // The mask is created per-monitor DPI aware so that it can be sized in physical pixels.
        // The control panel stays system-DPI aware; WinForms on .NET Framework scales it correctly that way.
        protected override void CreateHandle()
        {
            IntPtr previous = physical.IsEmpty ? IntPtr.Zero : Native.EnterPerMonitorDpi();
            try { base.CreateHandle(); }
            finally { Native.LeaveDpi(previous); }
            Reassert();
        }

        /// <summary>Puts the mask back on top of every other topmost window and over its whole monitor.</summary>
        internal void Reassert()
        {
            // Raising the mask while its right-click menu is open would hide the menu behind it.
            if (!IsHandleCreated || (ContextMenuStrip != null && ContextMenuStrip.Visible)) return;
            if (physical.IsEmpty)
                Native.SetWindowPos(Handle, Native.HwndTopmost, 0, 0, 0, 0,
                    Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
            else
                Native.SetWindowPos(Handle, Native.HwndTopmost, physical.Left, physical.Top, physical.Width, physical.Height,
                    Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left) restore();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { e.Handled = true; restore(); }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmSetCursor && (message.LParam.ToInt64() & 0xffff) == 1)
            {
                Native.SetCursor(IntPtr.Zero);
                message.Result = new IntPtr(1);
                return;
            }
            if (message.Msg == WmDpiChanged)
            {
                // Ignore the suggested rectangle; the mask must always match its monitor exactly.
                Reassert();
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ContextMenuStrip != null) ContextMenuStrip.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class ControlWindow : Form
    {
        private const uint EsContinuous = 0x80000000u, EsSystem = 0x1u, EsDisplay = 0x2u;
        private readonly List<MonitorCard> cards = new List<MonitorCard>();
        private readonly Dictionary<string, BlackoutWindow> masks = new Dictionary<string, BlackoutWindow>();
        private readonly FlowLayoutPanel screenPanel;
        private readonly Label status;
        private readonly Label shortcuts;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip trayMenu;
        private readonly CheckBox keepAwake;
        private readonly Button apply;
        private readonly System.Windows.Forms.Timer guard;
        private bool toggleKey;
        private bool restoreKey;
        private bool closing;
        private bool exitRequested;
        private bool trayNoticeShown;
        private bool locked;
        private bool loading;
        private static readonly Color Muted = Color.FromArgb(164, 175, 194);
        internal int BlackoutCount { get { return masks.Count; } }
        internal bool ShortcutsAvailable { get { return toggleKey && restoreKey; } }

        internal ControlWindow()
        {
            Text = "分屏息屏";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Color.FromArgb(17, 20, 27);
            ForeColor = Color.FromArgb(239, 242, 249);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(700, 526);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 22), ColumnCount = 1, RowCount = 8 };
            foreach (int height in new[] { 48, 37, 32, 192, 42, 52, 40, 39 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            Controls.Add(layout);
            layout.Controls.Add(Label("分屏息屏", 22, Color.White), 0, 0);
            layout.Controls.Add(Label("让选中的屏幕变黑，其他屏幕照常使用。", 10, Muted), 0, 1);
            layout.Controls.Add(Label("选择需要息屏的屏幕（选择会被记住）", 10, Color.FromArgb(208, 216, 231)), 0, 2);
            screenPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0, 5, 0, 0) };
            layout.Controls.Add(screenPanel, 0, 3);

            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            keepAwake = new CheckBox { Text = "息屏期间保持电脑和其他屏幕唤醒", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 0, 0) };
            keepAwake.CheckedChanged += delegate { UpdateWakeState(); };
            options.Controls.Add(keepAwake);
            var startWithWindows = new CheckBox { Text = "开机自启动（后台运行）", AutoSize = true,
                Checked = StartupSettings.IsEnabled, Margin = new Padding(18, 8, 0, 0) };
            startWithWindows.CheckedChanged += delegate { StartupSettings.SetEnabled(startWithWindows.Checked); };
            options.Controls.Add(startWithWindows);
            layout.Controls.Add(options, 0, 4);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            apply = Button("应用息屏", true);
            apply.Click += delegate { ApplySelection(); };
            var restore = Button("全部恢复", false);
            restore.Click += delegate { RestoreAll(); };
            var identify = Button("识别屏幕", false);
            identify.Click += delegate { Identify(); };
            var hide = Button("收起到托盘", false);
            hide.Click += delegate { HideToTray(); };
            actions.Controls.AddRange(new Control[] { apply, restore, identify, hide });
            layout.Controls.Add(actions, 0, 5);

            shortcuts = Label("", 9, Muted);
            layout.Controls.Add(shortcuts, 0, 6);
            status = Label("", 9, Muted);
            layout.Controls.Add(status, 0, 7);

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开控制面板", null, delegate { ShowControl(); });
            trayMenu.Items.Add("切换所选屏幕", null, delegate { ToggleSelection(); });
            trayMenu.Items.Add("全部恢复", null, delegate { RestoreAll(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray = new NotifyIcon { Icon = Icon, Text = "分屏息屏", Visible = true, ContextMenuStrip = trayMenu };
            tray.DoubleClick += delegate { ShowControl(); };

            // Taskbar, notifications and other topmost windows can rise above a mask after it is shown.
            guard = new System.Windows.Forms.Timer { Interval = 1500 };
            guard.Tick += delegate { foreach (var mask in masks.Values) mask.Reassert(); };

            LoadScreens();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            RegisterShortcuts();
        }

        private static Label Label(string text, float size, Color color)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", size), ForeColor = color, Margin = new Padding(0) };
        }

        private static Button Button(string text, bool primary)
        {
            var button = new Button { Text = text, Size = new Size(primary ? 148 : 138, 40),
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0),
                BackColor = primary ? Color.FromArgb(114, 158, 255) : Color.FromArgb(35, 41, 53),
                ForeColor = primary ? Color.FromArgb(12, 22, 42) : Color.FromArgb(227, 233, 245),
                UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private void RegisterShortcuts()
        {
            toggleKey = Native.RegisterHotKey(Handle, 1, 0x4007, (uint)Keys.B);
            restoreKey = Native.RegisterHotKey(Handle, 2, 0x4007, (uint)Keys.R);
            shortcuts.Text = (toggleKey ? "Ctrl + Alt + Shift + B 切换息屏" : "切换快捷键未能注册") + "    ·    " +
                (restoreKey ? "Ctrl + Alt + Shift + R 全部恢复" : "恢复快捷键未能注册") + "\n双击黑屏可恢复该屏幕";
        }

        private void LoadScreens()
        {
            loading = true;
            HashSet<string> saved = SelectionStore.Load();
            foreach (var card in cards) card.Dispose();
            cards.Clear();
            screenPanel.Controls.Clear();
            var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
            for (int i = 0; i < screens.Length; i++)
            {
                var card = new MonitorCard(screens[i], i + 1);
                card.Checked = saved == null ? !screens[i].Primary : saved.Contains(card.MonitorKey);
                card.CheckedChanged += delegate { SelectionChanged(); };
                cards.Add(card);
                screenPanel.Controls.Add(card);
            }
            loading = false;
            apply.Enabled = cards.Any(c => c.Checked);
            UpdateStatus();
        }

        private void SelectionChanged()
        {
            apply.Enabled = cards.Any(c => c.Checked);
            if (loading) return;
            // Keep the choices of monitors that are currently unplugged.
            var keys = SelectionStore.Load() ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (card.Checked) keys.Add(card.MonitorKey);
                else keys.Remove(card.MonitorKey);
            }
            SelectionStore.Save(keys);
        }

        internal void RunCommand(uint command)
        {
            if (command == Native.ToggleMessage) ToggleSelection();
            else if (command == Native.ApplyMessage) { if (masks.Count == 0) ApplySelection(); }
            else if (command == Native.RestoreMessage) RestoreAll();
        }

        internal void ApplySelection()
        {
            var selected = cards.Where(c => c.Checked).ToArray();
            if (selected.Length == 0) { RestoreAll(); return; }
            RestoreAll();
            Screen visible = cards.Where(c => !c.Checked).Select(c => c.Display).FirstOrDefault();
            if (visible != null && selected.Any(c => c.Display.Bounds.IntersectsWith(Bounds)))
                CenterOn(visible);
            if (visible == null) Hide();
            var physical = Native.PhysicalMonitorBounds();
            foreach (var card in selected)
            {
                string device = card.Display.DeviceName;
                Rectangle exact;
                if (!physical.TryGetValue(device, out exact)) exact = Rectangle.Empty;
                var mask = new BlackoutWindow(card.Display, exact, delegate { RestoreScreen(device); }, delegate { RestoreAll(); });
                MonitorCard owner = card;
                mask.FormClosed += delegate
                {
                    if (masks.Remove(device))
                    {
                        owner.Blanked = false;
                        owner.Invalidate();
                        UpdateWakeState();
                        UpdateStatus();
                    }
                };
                masks.Add(device, mask);
                mask.Show();
                card.Blanked = true;
                card.Invalidate();
            }
            UpdateWakeState();
            UpdateStatus();
        }

        internal void ToggleSelection()
        {
            if (masks.Count > 0) RestoreAll(); else ApplySelection();
        }

        private void RestoreScreen(string device)
        {
            BlackoutWindow mask;
            if (!masks.TryGetValue(device, out mask)) return;
            masks.Remove(device);
            mask.Close();
            mask.Dispose();
            foreach (var card in cards.Where(c => c.Display.DeviceName == device))
            {
                card.Blanked = false;
                card.Invalidate();
            }
            UpdateWakeState();
            UpdateStatus();
        }

        internal void RestoreAll()
        {
            foreach (var device in masks.Keys.ToArray()) RestoreScreen(device);
        }

        private void UpdateWakeState()
        {
            // While the session is locked the remaining monitors may sleep as Windows normally decides.
            bool hold = masks.Count > 0 && keepAwake.Checked && !locked;
            Native.SetThreadExecutionState(hold ? EsContinuous | EsSystem | EsDisplay : EsContinuous);
            guard.Enabled = masks.Count > 0;
        }

        private void UpdateStatus()
        {
            status.Text = masks.Count == 0 ? "已连接 " + cards.Count + " 块屏幕 · 当前全部正常显示" :
                masks.Count + " 块屏幕正在息屏 · 后台程序继续运行";
            tray.Text = masks.Count == 0 ? "分屏息屏 · 全部正常显示" : "分屏息屏 · " + masks.Count + " 块屏幕已变黑";
        }

        private void CenterOn(Screen screen)
        {
            Rectangle area = screen.WorkingArea;
            Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        internal void ShowControl()
        {
            Screen visible = Screen.AllScreens.FirstOrDefault(s => !masks.ContainsKey(s.DeviceName));
            if (visible == null) { RestoreAll(); visible = Screen.PrimaryScreen; }
            if (masks.ContainsKey(Screen.FromRectangle(Bounds).DeviceName)) CenterOn(visible);
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            if (!trayNoticeShown)
            {
                trayNoticeShown = true;
                tray.ShowBalloonTip(2500, "分屏息屏已在后台运行", "双击托盘图标打开面板，右键菜单可退出程序。", ToolTipIcon.Info);
            }
        }

        internal void ExitApplication()
        {
            exitRequested = true;
            Close();
        }

        private void Identify()
        {
            RestoreAll();
            foreach (var card in cards)
            {
                var badge = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false,
                    StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(30, 44, 69),
                    ForeColor = Color.White, TopMost = true, Size = new Size(180, 150) };
                badge.Location = new Point(card.Display.Bounds.Left + (card.Display.Bounds.Width - badge.Width) / 2,
                    card.Display.Bounds.Top + (card.Display.Bounds.Height - badge.Height) / 2);
                badge.Controls.Add(new Label { Text = card.Number.ToString(), Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 56) });
                var timer = new System.Windows.Forms.Timer { Interval = 1600 };
                timer.Tick += delegate { timer.Stop(); badge.Close(); badge.Dispose(); timer.Dispose(); };
                badge.Show();
                timer.Start();
            }
        }

        private void DisplayChanged(object sender, EventArgs e)
        {
            if (!closing && IsHandleCreated) BeginInvoke((Action)delegate
            {
                var screens = Screen.AllScreens;
                bool sameLayout = screens.Length == cards.Count && cards.All(card => screens.Any(screen =>
                    screen.DeviceName == card.Display.DeviceName && screen.Bounds == card.Display.Bounds &&
                    screen.Primary == card.Display.Primary));
                if (sameLayout)
                {
                    foreach (var mask in masks.Values) mask.Reassert();
                    return;
                }
                bool wasActive = masks.Count > 0;
                RestoreAll();
                LoadScreens();
                // Monitors that are still connected and still selected go dark again.
                if (wasActive && cards.Any(c => c.Checked)) ApplySelection();
            });
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            if (closing || !IsHandleCreated) return;
            if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.ConsoleDisconnect)
                BeginInvoke((Action)delegate { locked = true; UpdateWakeState(); });
            else if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect)
                BeginInvoke((Action)delegate
                {
                    locked = false;
                    foreach (var mask in masks.Values) mask.Reassert();
                    UpdateWakeState();
                });
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { RestoreAll(); e.Handled = true; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && tray != null) HideToTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
            {
                e.Cancel = true;
                HideToTray();
            }
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message message)
        {
            uint msg = (uint)message.Msg;
            if (msg == Native.ShowMessage) { ShowControl(); return; }
            if (msg == Native.ToggleMessage || msg == Native.ApplyMessage || msg == Native.RestoreMessage)
            {
                RunCommand(msg);
                return;
            }
            if (message.Msg == 0x312)
            {
                if (message.WParam.ToInt32() == 1) ToggleSelection();
                if (message.WParam.ToInt32() == 2) RestoreAll();
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
            guard.Stop();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            RestoreAll();
            Native.SetThreadExecutionState(EsContinuous);
            Native.UnregisterHotKey(Handle, 1);
            Native.UnregisterHotKey(Handle, 2);
            guard.Dispose();
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            base.OnFormClosed(e);
        }
    }
}
