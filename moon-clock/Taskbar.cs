// Centers the taskbar app icons, like a Mac dock. C# 5 syntax.
// It finds the task list inside the taskbar, measures the icons with UI Automation,
// and slides the list so the icons sit in the middle of the screen.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Collections.Generic;
using Microsoft.Win32;

namespace NightfallMoonClock
{
    // The taskbar features are built for Windows 10. Windows 11's taskbar works differently, so they switch off there.
    static class TaskbarSupport
    {
        public static readonly bool Supported = Check();
        static bool Check()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    int build;
                    if (k != null && int.TryParse(Convert.ToString(k.GetValue("CurrentBuildNumber")), out build)) return build < 22000;
                }
            }
            catch { }
            return true;
        }
    }

    static class TaskbarCenter
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;

        static IntPtr tray, rebar, taskSw, taskList;
        static int originalX = int.MinValue;
        static Timer timer;
        static int busy;
        static volatile bool enabled;

        public static void SetEnabled(bool on)
        {
            if (!TaskbarSupport.Supported) on = false;
            enabled = on;
            if (on)
            {
                if (timer == null) timer = new Timer(delegate { Tick(); }, null, 300, 800);
            }
            else
            {
                if (timer != null) { timer.Dispose(); timer = null; }
                Restore();
            }
        }

        static bool Find()
        {
            tray = FindWindow("Shell_TrayWnd", null);
            rebar = tray != IntPtr.Zero ? FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null) : IntPtr.Zero;
            taskSw = rebar != IntPtr.Zero ? FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
            taskList = taskSw != IntPtr.Zero ? FindWindowEx(taskSw, IntPtr.Zero, "MSTaskListWClass", null) : IntPtr.Zero;
            if (taskSw == IntPtr.Zero) return false;
            RECT r, rb;
            GetWindowRect(taskSw, out r);
            GetWindowRect(rebar, out rb);
            originalX = r.Left - rb.Left;
            if (originalX > (rb.Right - rb.Left) / 5) originalX = 0;   // was left centered by an earlier run
            return true;
        }

        // width of the actual icons (not the whole, mostly empty, task list)
        static bool IconSpan(out int left, out int right)
        {
            left = int.MaxValue; right = int.MinValue;
            try
            {
                AutomationElement list = AutomationElement.FromHandle(taskList);
                AutomationElementCollection kids = list.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement k in kids)
                {
                    System.Windows.Rect b = k.Current.BoundingRectangle;
                    if (b.IsEmpty || b.Width <= 0) continue;
                    left = Math.Min(left, (int)b.Left);
                    right = Math.Max(right, (int)b.Right);
                }
            }
            catch { return false; }
            return right > left;
        }

        static void Tick()
        {
            if (!enabled || Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                if (!IsWindow(taskSw) || !IsWindow(taskList)) { if (!Find()) return; }

                RECT trayRc, rebarRc, swRc;
                GetWindowRect(tray, out trayRc);
                GetWindowRect(rebar, out rebarRc);
                GetWindowRect(taskSw, out swRc);
                if (trayRc.Bottom - trayRc.Top > trayRc.Right - trayRc.Left) return;   // vertical taskbar: leave it alone

                int iconsLeft, iconsRight;
                if (!IconSpan(out iconsLeft, out iconsRight)) return;
                int iconsWidth = iconsRight - iconsLeft;
                int offsetInList = iconsLeft - swRc.Left;        // icons start a little inside the list

                int screenCenter = (trayRc.Left + trayRc.Right) / 2;
                int target = screenCenter - iconsWidth / 2 - offsetInList - rebarRc.Left;
                target = Math.Max(0, Math.Min(target, (rebarRc.Right - rebarRc.Left) - iconsWidth - offsetInList));

                int current = swRc.Left - rebarRc.Left;
                if (Math.Abs(target - current) <= 1) return;

                // glide there instead of jumping
                int y = swRc.Top - rebarRc.Top;
                for (int step = 0; step < 14; step++)
                {
                    current += (int)Math.Round((target - current) * 0.35);
                    if (Math.Abs(target - current) <= 1) current = target;
                    SetWindowPos(taskSw, IntPtr.Zero, current, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                    if (current == target) break;
                    Thread.Sleep(14);
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        public static void Restore()
        {
            for (int i = 0; i < 50 && Interlocked.CompareExchange(ref busy, 1, 0) == 1; i++) Thread.Sleep(20);   // wait for a running slide
            try
            {
                if (!IsWindow(taskSw) || originalX == int.MinValue) return;
                RECT swRc, rebarRc;
                GetWindowRect(taskSw, out swRc);
                GetWindowRect(rebar, out rebarRc);
                SetWindowPos(taskSw, IntPtr.Zero, originalX, swRc.Top - rebarRc.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
    }
}

namespace NightfallMoonClock
{
    // Makes the taskbar clear, blurred, or frosted (acrylic), like TranslucentTB.
    static class TaskbarLook
    {
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

        const int ACCENT_DISABLED = 0, ACCENT_TRANSPARENT = 2, ACCENT_BLUR = 3, ACCENT_ACRYLIC = 4;
        const uint NightTint = 0x9922111D;   // AABBGGRR: soft Nightfall plum

        static Timer timer;
        static volatile string style = "off", maximizedStyle = "same";
        static int busy;
        static readonly Dictionary<IntPtr, string> lastLook = new Dictionary<IntPtr, string>();

        public static void Configure(string normal, string whenMaximized)
        {
            if (!TaskbarSupport.Supported) { normal = "off"; whenMaximized = "same"; }
            style = normal;
            maximizedStyle = whenMaximized;
            bool on = normal != "off" || whenMaximized != "same";
            if (on && timer == null) timer = new Timer(delegate { Tick(); }, null, 0, 120);
            if (!on && timer != null) { timer.Dispose(); timer = null; RestoreAll(); }
            if (normal == "off" && whenMaximized == "same") RestoreAll();
        }

        public static void Stop()
        {
            if (timer != null) { timer.Dispose(); timer = null; }
            RestoreAll();
        }

        static List<IntPtr> Taskbars()
        {
            List<IntPtr> bars = new List<IntPtr>();
            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) bars.Add(main);
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, 64);
                if (sb.ToString() == "Shell_SecondaryTrayWnd") bars.Add(h);
                return true;
            }, IntPtr.Zero);
            return bars;
        }

        // monitors that currently have a maximized window on them
        static HashSet<IntPtr> MaximizedMonitors()
        {
            HashSet<IntPtr> monitors = new HashSet<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || !IsZoomed(h)) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;   // hidden on another virtual desktop
                monitors.Add(MonitorFromWindow(h, 2));
                return true;
            }, IntPtr.Zero);
            return monitors;
        }

        static void Apply(IntPtr hwnd, string look)
        {
            AccentPolicy accent = new AccentPolicy();
            switch (look)
            {
                case "clear": accent.AccentState = ACCENT_TRANSPARENT; accent.AccentFlags = 2; accent.GradientColor = 0x00000000; break;
                case "blur": accent.AccentState = ACCENT_BLUR; accent.AccentFlags = 2; accent.GradientColor = 0x00000000; break;
                case "acrylic": accent.AccentState = ACCENT_ACRYLIC; accent.AccentFlags = 2; accent.GradientColor = NightTint; break;
                default: return;   // "normal": leave it to Windows
            }
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                CompositionData data = new CompositionData();
                data.Attribute = 19;   // WCA_ACCENT_POLICY
                data.Data = ptr;
                data.SizeOfData = size;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        static void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                HashSet<IntPtr> maxed = maximizedStyle != "same" ? MaximizedMonitors() : null;
                lock (lastLook)
                foreach (IntPtr bar in Taskbars())
                {
                    string look = style;
                    if (maxed != null && maxed.Contains(MonitorFromWindow(bar, 2))) look = maximizedStyle;
                    if (look == "off") look = "normal";
                    string prev;
                    lastLook.TryGetValue(bar, out prev);
                    if (look == "normal")
                    {
                        if (prev != null && prev != "normal") Reset(bar);   // only once, when switching back
                    }
                    else Apply(bar, look);   // re-applied often, because Windows keeps resetting it
                    lastLook[bar] = look;
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        static void Reset(IntPtr bar)
        {
            AccentPolicy accent = new AccentPolicy();
            accent.AccentState = ACCENT_DISABLED;
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                CompositionData data = new CompositionData();
                data.Attribute = 19; data.Data = ptr; data.SizeOfData = size;
                SetWindowCompositionAttribute(bar, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
            PostMessage(bar, 0x031A, IntPtr.Zero, IntPtr.Zero);   // WM_THEMECHANGED: let Windows redraw its normal look
        }

        static void RestoreAll()
        {
            lock (lastLook)
            {
                try { foreach (IntPtr bar in Taskbars()) Reset(bar); } catch { }
                lastLook.Clear();
            }
        }
    }
}
