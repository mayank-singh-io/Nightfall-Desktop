// Live wallpaper: plays a scene behind the desktop icons, only while the desktop is actually visible.
// C# 5 syntax (built with the compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Windows.Automation;

namespace NightfallMoonClock
{
    static class WallpaperNative
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POWER { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out POWER status);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfo(int action, int param, string value, int winIni);

        // The window behind the desktop icons that Windows draws the wallpaper into.
        public static IntPtr FindWallpaperHost()
        {
            IntPtr progman = FindWindow("Progman", null);
            IntPtr res;
            SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out res);   // asks Explorer to create it if needed
            IntPtr worker = IntPtr.Zero;
            EnumWindows(delegate(IntPtr top, IntPtr l)
            {
                if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                return true;
            }, IntPtr.Zero);
            return worker != IntPtr.Zero ? worker : progman;
        }
    }

    // Decides whether the wallpaper should be moving right now.
    static class WallpaperGate
    {
        static readonly int myPid = Process.GetCurrentProcess().Id;
        static readonly HashSet<string> ignore = new HashSet<string> {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland", "Xaml_WindowedPopupClass", "ForegroundStaging", "MultitaskingViewFrame"
        };

        // true when any real app window overlaps this screen
        public static bool ScreenHasWindows(Rectangle screen)
        {
            bool found = false;
            long minArea = (long)screen.Width * screen.Height / 20;   // ignore tiny things like tooltips
            WallpaperNative.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!WallpaperNative.IsWindowVisible(h) || WallpaperNative.IsIconic(h)) return true;
                int pid; WallpaperNative.GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return true;
                int ex = WallpaperNative.GetWindowLong(h, -20);
                if ((ex & 0x80) != 0) return true;                               // tool windows
                int cloaked;
                if (WallpaperNative.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                StringBuilder sb = new StringBuilder(80);
                WallpaperNative.GetClassName(h, sb, 80);
                if (ignore.Contains(sb.ToString())) return true;
                WallpaperNative.RECT r;
                if (!WallpaperNative.GetWindowRect(h, out r)) return true;
                Rectangle overlap = Rectangle.Intersect(screen, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                if ((long)overlap.Width * overlap.Height >= minArea) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool BatteryAllows(Settings s)
        {
            WallpaperNative.POWER p;
            if (!WallpaperNative.GetSystemPowerStatus(out p)) return true;
            bool onBattery = p.ACLineStatus == 0;
            if (s.WallPauseSaver && p.SystemStatusFlag == 1) return false;
            if (onBattery && s.WallPauseOnBattery) return false;
            if (onBattery && s.WallPauseBelow > 0 && p.BatteryLifePercent != 255 && p.BatteryLifePercent < s.WallPauseBelow) return false;
            return true;
        }
    }

    // One window per monitor, parented behind the desktop icons.
    class WallpaperForm : Form
    {
        readonly Settings settings;
        readonly Rectangle screen;
        readonly SceneRenderer scene = new SceneRenderer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer frame = new Timer();
        double lastMs, lastCheck = -99999;
        bool running;
        public IntPtr HostHandle;

        public WallpaperForm(Settings s, Screen sc, SceneData data)
        {
            settings = s;
            screen = sc.Bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.Black;
            Bounds = screen;
            float ui;
            using (Graphics g = CreateGraphics()) ui = g.DpiX / 96f;
            scene.AllowShootingStars = s.SkyShooting;
            scene.TrainGapSeconds = s.TrainGap();
            scene.ClickStars = s.WallClickStars; scene.ClickLights = s.WallClickLights; scene.ClickWater = s.WallClickWater; scene.Parallax = s.WallParallax;
            scene.Build(data, screen.Width, screen.Height, ui);
            scene.SetDawn(SunClock.DawnAmount(s, DateTime.Now), true);
            frame.Interval = 40;
            frame.Tick += delegate { Tick(); };
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // tool window, never activates
        }

        public void Attach(IntPtr host)
        {
            HostHandle = host;
            IntPtr created = Handle;   // creates the window without showing it
            const int GWL_STYLE = -16, WS_CHILD = 0x40000000;
            int style = WallpaperNative.GetWindowLong(Handle, GWL_STYLE);
            WallpaperNative.SetWindowLong(Handle, GWL_STYLE, (style & ~unchecked((int)0x80000000)) | WS_CHILD);
            WallpaperNative.SetParent(Handle, host);
            Rectangle vs = SystemInformation.VirtualScreen;   // the host spans every monitor, starting at the virtual-screen origin
            WallpaperNative.SetWindowPos(Handle, IntPtr.Zero, screen.X - vs.X, screen.Y - vs.Y, screen.Width, screen.Height, 0x0010 | 0x0040);
            Show();   // only now, already behind the desktop icons
            frame.Start();
        }

        void Tick()
        {
            double t = clock.Elapsed.TotalMilliseconds;
            if (t - lastCheck > 1000)
            {
                lastCheck = t;
                if (!WallpaperNative.IsWindow(HostHandle)) { LiveWallpaper.Rebuild(); return; }   // Explorer restarted
                scene.SetDawn(SunClock.DawnAmount(settings, DateTime.Now));
                running = !SkyManager.Active && WallpaperGate.BatteryAllows(settings) && !WallpaperGate.ScreenHasWindows(screen);
            }
            if (!running) { lastMs = t; return; }   // frozen: keeps the last frame, uses no CPU
            if (settings.WallParallax)
            {
                Point c = Cursor.Position;
                scene.SetPointer((c.X - screen.X) / (float)screen.Width * 2 - 1, (c.Y - screen.Y) / (float)screen.Height * 2 - 1);
            }
            scene.Step(t, Math.Min(0.1, (t - lastMs) / 1000.0));
            lastMs = t;
            Invalidate();
        }

        public Rectangle ScreenBounds { get { return screen; } }
        public void UpdateOptions(Settings s)
        {
            scene.ClickStars = s.WallClickStars; scene.ClickLights = s.WallClickLights; scene.ClickWater = s.WallClickWater;
            scene.Parallax = s.WallParallax; scene.AllowShootingStars = s.SkyShooting;
            if (!s.WallParallax) scene.SetPointer(0, 0);
        }
        public bool IsRunning { get { return running; } }
        public void ClickAt(Point p)
        {
            if (!running) return;
            if (scene.Click(p.X - screen.X, p.Y - screen.Y, clock.Elapsed.TotalMilliseconds)) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { scene.Paint(e.Graphics, clock.Elapsed.TotalMilliseconds); }
            catch (Exception) { }   // skip a frame rather than ever take down the wallpaper
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { frame.Dispose(); scene.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Watches for clicks on the desktop itself (not on windows) and passes them to the wallpaper.
    static class DesktopClicks
    {
        delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct MSLL { public int X, Y, Data, Flags, Time; public IntPtr Extra; }
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, int thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        static IntPtr hook;
        static HookProc proc;   // kept alive so it isn't garbage collected
        public static Action<Point> OnClick;
        static Point downAt;
        static System.Threading.SynchronizationContext ui;

        public static void Start()
        {
            if (hook != IntPtr.Zero) return;
            ui = System.Threading.SynchronizationContext.Current;
            proc = Callback;
            hook = SetWindowsHookEx(14, proc, GetModuleHandle(null), 0);   // WH_MOUSE_LL
        }
        public static void Stop() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }

        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = w.ToInt32();
                MSLL info = (MSLL)Marshal.PtrToStructure(l, typeof(MSLL));
                Point p = new Point(info.X, info.Y);
                if (msg == 0x0201) downAt = p;   // left button down
                else if (msg == 0x0202 && Math.Abs(p.X - downAt.X) < 6 && Math.Abs(p.Y - downAt.Y) < 6 && OnClick != null)   // a click, not a drag
                {
                    StringBuilder sb = new StringBuilder(64);
                    WallpaperNative.GetClassName(WindowFromPoint(p), sb, 64);
                    if (sb.ToString() == "SysListView32" && ui != null)   // the desktop surface
                    {
                        Point cp = p;
                        ui.Post(delegate
                        {
                            try
                            {
                                AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(cp.X, cp.Y));
                                if (el != null && el.Current.ControlType == ControlType.ListItem) return;   // that was an icon
                            }
                            catch { }
                            if (OnClick != null) OnClick(cp);
                        }, null);
                    }
                }
            }
            return CallNextHookEx(hook, code, w, l);
        }
    }

    static class LiveWallpaper
    {
        static readonly List<WallpaperForm> forms = new List<WallpaperForm>();
        static Settings current;
        static string unlockPick;
        static readonly Random rng = new Random();
        static bool hooked;

        public static string CurrentKey(Settings s)
        {
            if (s.WallMode == "daily") return SceneData.Keys[DateTime.Now.DayOfYear % SceneData.Keys.Length];
            if (s.WallMode == "unlock") { if (unlockPick == null) unlockPick = SceneData.Keys[rng.Next(SceneData.Keys.Length)]; return unlockPick; }
            return Array.IndexOf(SceneData.Keys, s.WallScene) >= 0 ? s.WallScene : "aurora";
        }

        static string builtSignature;
        static string Signature(Settings s)
        {
            return s.WallpaperOn + "|" + s.WallMode + "|" + s.WallScene + "|" + s.TrainEvery + "|" + s.RoomView + "|" + Screen.AllScreens.Length;
        }

        public static void Apply(Settings s)
        {
            if (current == s && forms.Count > 0 && builtSignature == Signature(s))
            {
                foreach (WallpaperForm f in forms) f.UpdateOptions(s);   // just the switches changed: no restart, no flicker
                HookClicks();
                return;
            }
            current = s;
            if (!hooked)
            {
                hooked = true;
                SystemEvents.SessionSwitch += delegate(object o, SessionSwitchEventArgs e)
                {
                    if (e.Reason == SessionSwitchReason.SessionUnlock && current != null && current.WallMode == "unlock")
                    { unlockPick = SceneData.Keys[rng.Next(SceneData.Keys.Length)]; Rebuild(); }
                };
                SystemEvents.DisplaySettingsChanged += delegate { if (current != null && current.WallpaperOn) Rebuild(); };
            }
            Rebuild();
        }

        static string builtDay;
        public static void Rebuild()
        {
            bool had = forms.Count > 0;
            Stop(false);
            if (current == null || !current.WallpaperOn) { DesktopClicks.Stop(); if (had) RestoreDesktop(); return; }
            string key = SceneData.Resolve(CurrentKey(current), current);   // the same Cozy Room view on every monitor
            IntPtr host = WallpaperNative.FindWallpaperHost();
            if (host == IntPtr.Zero) return;
            foreach (Screen sc in Screen.AllScreens)
            {
                SceneData data = SceneData.Load(key, current);   // each monitor gets its own copy
                if (data == null) continue;
                WallpaperForm f = new WallpaperForm(current, sc, data);
                f.Attach(host);
                forms.Add(f);
            }
            builtDay = DateTime.Now.ToString("yyyyMMdd");
            builtSignature = Signature(current);
            HookClicks();
        }

        // called every minute by the clock, so "a new scene each day" switches over at midnight
        public static void CheckDay()
        {
            if (current != null && current.WallpaperOn && current.WallMode == "daily" && builtDay != DateTime.Now.ToString("yyyyMMdd")) Rebuild();
        }

        static void Stop(bool restore)
        {
            if (restore) DesktopClicks.Stop();
            foreach (WallpaperForm f in forms) { try { f.Close(); f.Dispose(); } catch { } }
            forms.Clear();
            if (restore) RestoreDesktop();
        }

        public static void Shutdown() { Stop(true); builtSignature = null; }

        static void HookClicks()
        {
            if (current != null && current.WallpaperOn && (current.WallClickStars || current.WallClickLights || current.WallClickWater))
            {
                DesktopClicks.OnClick = delegate(Point p) { foreach (WallpaperForm f in forms) if (f.ScreenBounds.Contains(p)) f.ClickAt(p); };
                DesktopClicks.Start();
            }
            else DesktopClicks.Stop();
        }

        // asks Windows to redraw the normal wallpaper where ours was
        static void RestoreDesktop()
        {
            try
            {
                string path = Convert.ToString(Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", ""));
                WallpaperNative.SystemParametersInfo(0x0014, 0, path, 0);   // SPI_SETDESKWALLPAPER, re-applies the current one
            }
            catch { }
        }
    }
}
