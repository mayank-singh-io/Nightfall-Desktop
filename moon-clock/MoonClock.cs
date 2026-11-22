// Nightfall Moon Clock
// A tiny transparent desktop widget: time, date, tonight's moon, and what's playing.
// Built with the C# compiler that ships with Windows (C# 5), so keep the syntax old-school.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NightfallMoonClock
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool isNew;
            using (Mutex mutex = new Mutex(true, "NightfallMoonClock_SingleInstance", out isNew))
            {
                if (!isNew) return;
                DesktopIcons.RecoverAfterCrash();   // in case a previous run was force-closed while icons were hidden
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                AppDomain.CurrentDomain.UnhandledException += delegate { DesktopIcons.Show(); };
                Application.ThreadException += delegate { DesktopIcons.Show(); };
                SystemEvents.SessionEnding += delegate { DesktopIcons.Show(); };
                try { Application.Run(new ClockWindow()); }
                finally { DesktopIcons.Show(); TaskbarCenter.SetEnabled(false); TaskbarLook.Stop(); }
            }
        }
    }

    // ---------------------------------------------------------------- settings
    class Settings
    {
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public bool Locked = false;
        public bool Use24h = false;
        public bool ShowPlaying = true;
        public bool BigMoon = false;
        public string Font = "Segoe UI Light";
        // away mode
        public int IconsAfter = 3;
        public int SkyAfter = 10;
        public bool PauseForMedia = false;
        public bool SkyShooting = true, SkyShowers = true, SkyMoon = true, SkyClock = true, SkyThoughts = true;
        public string Weather = "auto";
        public string SkyColors = "night", SkyExtras = "none", MoonStyle = "large";
        public int LockAfter = 20;
        public bool LockEnabled = true;   // lock the PC once the away sky has shown for LockAfter minutes
        public bool ShouldLock { get { return LockEnabled && LockAfter > 0; } }
        public bool SkyPlaying = true, SkyParallax = true, SkyConstellations = true, SkyHourly = true;
        public bool ShowTonight = true;
        public bool AlignRight = true;
        public bool CenterTaskbar = true;
        public string TaskbarStyle = "clear", TaskbarMaximized = "acrylic";
        public int RightEdge = int.MinValue;
        public string City = "";
        public double Lat = double.NaN, Lon = double.NaN;

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightfallMoonClock"); }
        }
        static string FilePath { get { return Path.Combine(Folder, "settings.ini"); } }

        static bool B(Dictionary<string, string> d, string k, bool def) { string v; return d.TryGetValue(k, out v) ? v == "true" : def; }
        static int I(Dictionary<string, string> d, string k, int def) { string v; int n; return d.TryGetValue(k, out v) && int.TryParse(v, out n) ? n : def; }
        static double D(Dictionary<string, string> d, string k)
        {
            string v; double n;
            return d.TryGetValue(k, out v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n) ? n : double.NaN;
        }
        static string S(Dictionary<string, string> d, string k, string def) { string v; return d.TryGetValue(k, out v) && v.Length > 0 ? v : def; }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                Dictionary<string, string> d = new Dictionary<string, string>();
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq > 0) d[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
                }
                s.X = I(d, "X", s.X); s.Y = I(d, "Y", s.Y);
                s.Locked = B(d, "Locked", s.Locked); s.Use24h = B(d, "Use24h", s.Use24h);
                s.ShowPlaying = B(d, "ShowPlaying", s.ShowPlaying); s.BigMoon = B(d, "BigMoon", s.BigMoon);
                s.Font = S(d, "Font", s.Font);
                s.IconsAfter = I(d, "IconsAfter", s.IconsAfter); s.SkyAfter = I(d, "SkyAfter", s.SkyAfter);
                s.PauseForMedia = B(d, "PauseForMedia", s.PauseForMedia);
                s.SkyShooting = B(d, "SkyShooting", s.SkyShooting); s.SkyShowers = B(d, "SkyShowers", s.SkyShowers);
                s.SkyMoon = B(d, "SkyMoon", s.SkyMoon); s.SkyClock = B(d, "SkyClock", s.SkyClock); s.SkyThoughts = B(d, "SkyThoughts", s.SkyThoughts);
                s.Weather = S(d, "Weather", s.Weather); s.City = S(d, "City", "");
                s.SkyColors = S(d, "SkyColors", s.SkyColors); s.SkyExtras = S(d, "SkyExtras", s.SkyExtras); s.MoonStyle = d.ContainsKey("RealMoon") ? S(d, "MoonStyle", s.MoonStyle) : "large";   // switch everyone to the real moon once
                s.LockAfter = I(d, "LockAfter", s.LockAfter);
                // older versions turned locking off by setting the minutes to 0
                s.LockEnabled = d.ContainsKey("LockEnabled") ? B(d, "LockEnabled", true) : s.LockAfter > 0;
                if (s.LockAfter <= 0) s.LockAfter = 20;
                s.SkyPlaying = B(d, "SkyPlaying", s.SkyPlaying); s.SkyParallax = B(d, "SkyParallax", s.SkyParallax);
                s.SkyConstellations = B(d, "SkyConstellations", s.SkyConstellations);
                s.SkyHourly = B(d, "SkyHourly", s.SkyHourly); s.ShowTonight = B(d, "ShowTonight", s.ShowTonight);
                s.AlignRight = B(d, "AlignRight", s.AlignRight); s.CenterTaskbar = B(d, "CenterTaskbar", s.CenterTaskbar);
                s.TaskbarStyle = S(d, "TaskbarStyle", s.TaskbarStyle); s.TaskbarMaximized = S(d, "TaskbarMaximized", s.TaskbarMaximized); s.RightEdge = I(d, "RightEdge", s.RightEdge);
                s.Lat = D(d, "Lat"); s.Lon = D(d, "Lon");
            }
            catch { }
            return s;
        }

        static string Bool(bool b) { return b ? "true" : "false"; }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                string temp = FilePath + ".tmp";
                File.WriteAllLines(temp, new string[] {
                    "X=" + X, "Y=" + Y,
                    "Locked=" + Bool(Locked), "Use24h=" + Bool(Use24h), "ShowPlaying=" + Bool(ShowPlaying), "BigMoon=" + Bool(BigMoon),
                    "Font=" + Font,
                    "IconsAfter=" + IconsAfter, "SkyAfter=" + SkyAfter, "PauseForMedia=" + Bool(PauseForMedia),
                    "SkyShooting=" + Bool(SkyShooting), "SkyShowers=" + Bool(SkyShowers), "SkyMoon=" + Bool(SkyMoon),
                    "SkyClock=" + Bool(SkyClock), "SkyThoughts=" + Bool(SkyThoughts),
                    "Weather=" + Weather, "City=" + City,
                    "SkyColors=" + SkyColors, "SkyExtras=" + SkyExtras, "MoonStyle=" + MoonStyle, "RealMoon=1", "LockAfter=" + LockAfter, "LockEnabled=" + Bool(LockEnabled),
                    "SkyPlaying=" + Bool(SkyPlaying), "SkyParallax=" + Bool(SkyParallax), "SkyConstellations=" + Bool(SkyConstellations), "SkyHourly=" + Bool(SkyHourly), "ShowTonight=" + Bool(ShowTonight),
                    "AlignRight=" + Bool(AlignRight), "CenterTaskbar=" + Bool(CenterTaskbar), "TaskbarStyle=" + TaskbarStyle, "TaskbarMaximized=" + TaskbarMaximized, "RightEdge=" + RightEdge,
                    "Lat=" + (double.IsNaN(Lat) ? "" : Lat.ToString(inv)), "Lon=" + (double.IsNaN(Lon) ? "" : Lon.ToString(inv))
                });
                // write the new file first, then swap it in, so a crash mid-save can't wipe your settings
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------- moon math
    static class Moon
    {
        static readonly string[] Names = {
            "New moon", "Waxing crescent", "First quarter", "Waxing gibbous",
            "Full moon", "Waning gibbous", "Last quarter", "Waning crescent"
        };

        // 0 = new, 0.5 = full
        public static double Phase(DateTime nowUtc)
        {
            const double synodic = 29.530588853;
            DateTime reference = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
            double days = (nowUtc - reference).TotalDays;
            double age = ((days % synodic) + synodic) % synodic;
            return age / synodic;
        }

        public static string Name(double p)
        {
            return Names[(int)Math.Round(p * 8) % 8];
        }

        public static int PercentLit(double p)
        {
            return (int)Math.Round((1 - Math.Cos(2 * Math.PI * p)) / 2 * 100);
        }

        // a larger moon with soft "seas", limb shading and a gently lit shadow side
        public static void DrawDetailed(Graphics g, float cx, float cy, float r, double p, Color sky)
        {
            using (GraphicsPath glowPath = new GraphicsPath())
            {
                float gr = r * 2.2f;
                glowPath.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                {
                    glow.CenterColor = Color.FromArgb((int)(6 + 22 * PercentLit(p) / 100.0), 236, 228, 244);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, 236, 228, 244) };
                    g.FillPath(glow, glowPath);
                }
            }
            RectangleF disk = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (Bitmap surface = Surface((int)Math.Ceiling(r * 4), p, sky))   // drawn at 2x, then scaled down for crisp detail
            {
                InterpolationMode im = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(surface, disk);
                g.InterpolationMode = im;
            }
        }

        // ---------- procedural moon surface (same every time)
        static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
            }
        }
        static float ValueNoise(float x, float y)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            float tx = x - xi, ty = y - yi;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float a = Hash(xi, yi), b = Hash(xi + 1, yi), c = Hash(xi, yi + 1), d = Hash(xi + 1, yi + 1);
            return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
        }
        static float Fbm(float x, float y, int octaves)
        {
            float sum = 0, amp = 0.5f, freq = 1;
            for (int i = 0; i < octaves; i++) { sum += amp * ValueNoise(x * freq + i * 17.3f, y * freq - i * 9.1f); amp *= 0.5f; freq *= 2.03f; }
            return sum;
        }
        static float Smooth(float e0, float e1, float x)
        {
            float t = Math.Max(0, Math.Min(1, (x - e0) / (e1 - e0)));
            return t * t * (3 - 2 * t);
        }

        static Bitmap photo;
        static bool photoChecked;

        // NASA moon photo (moon.png next to the app), if present
        static int[] PhotoPixels(int size)
        {
            if (!photoChecked)
            {
                photoChecked = true;
                try
                {
                    string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "moon.png");
                    if (File.Exists(path)) using (Image img = Image.FromFile(path)) photo = new Bitmap(img);
                }
                catch { photo = null; }
            }
            if (photo == null) return null;
            using (Bitmap scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(photo, 0, 0, size, size);
                }
                int[] px = new int[size * size];
                BitmapData data = scaled.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                scaled.UnlockBits(data);
                return px;
            }
        }

        // brightness map of the near side: highlands, seas, craters. Light comes from the right when waxing.
        public static Bitmap Surface(int size, double phase, Color sky)
        {
            size = Math.Max(8, size);
            int[] photoPx = PhotoPixels(size);
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            int[] px = new int[size * size];
            float R = size / 2f;

            // craters: x, y, radius (unit-disk), seeded so the moon looks the same every night
            Random cr = new Random(1969);
            int craterCount = 140;
            float[] cxs = new float[craterCount], cys = new float[craterCount], crs = new float[craterCount];
            for (int i = 0; i < craterCount; i++)
            {
                double ang = cr.NextDouble() * Math.PI * 2, dist = Math.Sqrt(cr.NextDouble()) * 0.95;
                cxs[i] = (float)(Math.Cos(ang) * dist); cys[i] = (float)(Math.Sin(ang) * dist);
                double t = cr.NextDouble();
                crs[i] = (float)(0.006 + t * t * t * t * 0.05);
            }
            const float rayX = -0.08f, rayY = 0.66f;     // one bright young crater with rays
            bool lightFromRight = phase <= 0.5;
            float sun = lightFromRight ? 1f : -1f;
            // sunlight direction: first quarter lights the right half, full moon lights the face toward us
            float Lx = (float)Math.Sin(2 * Math.PI * phase), Lz = (float)-Math.Cos(2 * Math.PI * phase);
            float darkR = sky.R * 0.7f + 6, darkG = sky.G * 0.7f + 5, darkB = sky.B * 0.7f + 9;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f - R) / R, v = (y + 0.5f - R) / R;
                    float d2 = u * u + v * v;
                    if (d2 > 1.02f) { px[y * size + x] = 0; continue; }

                    if (photoPx != null)
                    {
                        int pc = photoPx[y * size + x];
                        int pa = (pc >> 24) & 255;
                        float pr = Math.Min(1f, ((pc >> 16) & 255) / 255f * 1.22f);
                        float pg = Math.Min(1f, ((pc >> 8) & 255) / 255f * 1.22f);
                        float pb = Math.Min(1f, (pc & 255) / 255f * 1.26f);
                        float lum = (pr + pg + pb) / 3f;
                        float pnz = (float)Math.Sqrt(Math.Max(0, 1 - d2));
                        float pdot = u * Lx + pnz * Lz;
                        float plit = Smooth(-0.06f, 0.14f, pdot) * (0.85f + 0.15f * Math.Max(0, pdot));
                        float pdr = darkR + lum * 12, pdg = darkG + lum * 11, pdb = darkB + lum * 14;
                        int r2 = Math.Min(255, (int)(pdr + (255 * pr - pdr) * plit));
                        int g2 = Math.Min(255, (int)(pdg + (255 * pg - pdg) * plit));
                        int b2 = Math.Min(255, (int)(pdb + (255 * pb - pdb) * plit));
                        px[y * size + x] = (pa << 24) | (r2 << 16) | (g2 << 8) | b2;
                        continue;
                    }

                    // highlands: bright, mottled
                    float bright = 0.82f + (Fbm(u * 3.2f + 5, v * 3.2f + 2, 5) - 0.5f) * 0.12f;
                    bright += (Fbm(u * 14f, v * 14f, 3) - 0.5f) * 0.04f;              // fine grain

                    // seas: large, irregular, connected dark plains (domain-warped noise), mostly upper left like the real ones
                    float wx = Fbm(u * 2.4f + 11, v * 2.4f + 3, 3) - 0.5f, wy = Fbm(u * 2.4f - 7, v * 2.4f + 19, 3) - 0.5f;
                    float q = Fbm(u * 1.7f + wx * 1.1f + 3, v * 1.7f + wy * 1.1f + 8, 4);
                    float bias = 0.16f * (float)Math.Exp(-((u + 0.18f) * (u + 0.18f) + (v + 0.12f) * (v + 0.12f)) / 0.30f) - 0.06f * Math.Max(0, v - 0.35f);
                    float sea = Smooth(0.44f, 0.64f, q + bias);
                    bright -= sea * (0.12f + 0.05f * Fbm(u * 9, v * 9, 2));

                    // craters: shadowed bowl on the sun side, bright rim on the far side
                    for (int i = 0; i < craterCount; i++)
                    {
                        float dx = u - cxs[i], dy = v - cys[i], rr = crs[i];
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (dist > rr * 1.35f) continue;
                        float k = dist / rr;
                        float side = dx / Math.Max(dist, 1e-4f) * sun;              // +1 on the side facing the sun
                        if (k < 1) bright -= (1 - k * k) * 0.05f * (0.5f + 0.5f * side) * (sea > 0.5f ? 0.5f : 1f);
                        float rim = Math.Max(0, 1 - Math.Abs(k - 1.05f) / 0.22f);
                        bright += rim * 0.045f * (0.5f - 0.5f * side);
                    }

                    // the bright rayed crater
                    {
                        float dx = u - rayX, dy = v - rayY;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        float streaks = Fbm(u * 3 + (float)Math.Atan2(dy, dx) * 2.5f, dist * 2, 3);
                        bright += 0.07f * (float)Math.Exp(-dist / 0.3f) * Smooth(0.45f, 0.75f, streaks) * Smooth(0.03f, 0.07f, dist);
                        bright += 0.14f * (float)Math.Exp(-(dist * dist) / 0.0010f);
                    }

                    // light it like a sphere: soft terminator, faint earthshine on the dark side
                    float nz = (float)Math.Sqrt(Math.Max(0, 1 - d2));
                    float ndotl = u * Lx + nz * Lz;
                    float lit = Smooth(-0.06f, 0.14f, ndotl);
                    bright *= 0.82f + 0.18f * Math.Max(0, Math.Max(ndotl, nz));   // limb shading
                    bright = Math.Max(0.25f, Math.Min(1.05f, bright));
                    float er = darkR + bright * 10, eg = darkG + bright * 9, eb = darkB + bright * 12;   // earthshine
                    int rC = Math.Min(255, (int)(er + (238 * bright - er) * lit));
                    int gC = Math.Min(255, (int)(eg + (233 * bright - eg) * lit));
                    int bC = Math.Min(255, (int)(eb + (240 * bright - eb) * lit));
                    float alpha = 1 - Smooth(0.985f, 1.02f, d2);                   // anti-aliased edge
                    px[y * size + x] = ((int)(alpha * 255) << 24) | (rC << 16) | (gC << 8) | bC;
                }
            }
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        public static void Draw(Graphics g, float cx, float cy, float r, double p, bool glowOn)
        {
            // soft glow
            if (glowOn) using (GraphicsPath glowPath = new GraphicsPath())
            {
                float gr = r * 1.6f;
                glowPath.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
                using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                {
                    glow.CenterColor = Color.FromArgb(28, 236, 228, 244);
                    glow.SurroundColors = new Color[] { Color.FromArgb(0, 236, 228, 244) };
                    g.FillPath(glow, glowPath);
                }
            }
            // earthshine on the dark part
            using (SolidBrush dark = new SolidBrush(Color.FromArgb(55, 207, 198, 220)))
                g.FillEllipse(dark, cx - r, cy - r, r * 2, r * 2);

            double k = Math.Cos(2 * Math.PI * p);
            float rx = (float)Math.Max(0.01, Math.Abs(k) * r);
            using (GraphicsPath lit = new GraphicsPath())
            {
                RectangleF disk = new RectangleF(cx - r, cy - r, r * 2, r * 2);
                RectangleF term = new RectangleF(cx - rx, cy - r, rx * 2, r * 2);
                if (p <= 0.5)
                {
                    lit.AddArc(disk, -90, 180);                 // right half
                    lit.AddArc(term, 90, k > 0 ? -180 : 180);   // back up the terminator
                }
                else
                {
                    lit.AddArc(disk, -90, -180);                // left half
                    lit.AddArc(term, 90, k > 0 ? 180 : -180);
                }
                lit.CloseFigure();
                using (SolidBrush light = new SolidBrush(Color.FromArgb(245, 236, 230, 242)))
                    g.FillPath(light, lit);
            }
        }
    }

    // ---------------------------------------------------------------- now playing
    static class NowPlaying
    {
        // Reads Windows' media controls (the same info as the volume flyout) through
        // reflection, so the build needs no Windows Runtime references and always
        // includes this feature. Works on Windows 10 version 1809 and newer.
        public static volatile string Last;

        const string Ns = "Windows.Media.Control.";
        const string WinRT = ", Windows.Media.Control, ContentType=WindowsRuntime";
        static readonly object gate = new object();
        static int state;                    // 0 = not checked yet, 1 = available, -1 = not on this PC
        static Type managerType, sessionType, infoType, propsType;
        static MethodInfo asTask;            // WindowsRuntimeSystemExtensions.AsTask<T>(IAsyncOperation<T>)
        static object manager;

        public static bool Supported { get { return Init(); } }

        static bool Init()
        {
            lock (gate)
            {
                if (state != 0) return state > 0;
                state = -1;
                try
                {
                    managerType = Type.GetType(Ns + "GlobalSystemMediaTransportControlsSessionManager" + WinRT);
                    sessionType = Type.GetType(Ns + "GlobalSystemMediaTransportControlsSession" + WinRT);
                    infoType = Type.GetType(Ns + "GlobalSystemMediaTransportControlsSessionPlaybackInfo" + WinRT);
                    propsType = Type.GetType(Ns + "GlobalSystemMediaTransportControlsSessionMediaProperties" + WinRT);
                    Type ext = null;
                    foreach (string version in new string[] { "4.0.0.0", "4.0.10.0" })
                    {
                        try
                        {
                            ext = Assembly.Load("System.Runtime.WindowsRuntime, Version=" + version + ", Culture=neutral, PublicKeyToken=b77a5c561934e089")
                                          .GetType("System.WindowsRuntimeSystemExtensions");
                        }
                        catch { }
                        if (ext != null) break;
                    }
                    if (ext != null)
                        foreach (MethodInfo m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "AsTask" || !m.IsGenericMethodDefinition) continue;
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") { asTask = m; break; }
                        }
                    if (managerType != null && sessionType != null && infoType != null && propsType != null && asTask != null) state = 1;
                }
                catch { }
                return state > 0;
            }
        }

        // waits for a Windows Runtime async result, but never forever
        static object Await(object operation, Type resultType)
        {
            if (operation == null) return null;
            Task task = (Task)asTask.MakeGenericMethod(resultType).Invoke(null, new object[] { operation });
            if (!task.Wait(4000)) return null;
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        static object Call(Type type, object target, string method)
        {
            return type.GetMethod(method, Type.EmptyTypes).Invoke(target, null);
        }

        static object Get(Type type, object target, string property)
        {
            return type.GetProperty(property).GetValue(target, null);
        }

        static bool IsPlaying(object session)
        {
            if (session == null) return false;
            object info = Call(sessionType, session, "GetPlaybackInfo");
            if (info == null) return false;
            object status = Get(infoType, info, "PlaybackStatus");
            return status != null && status.ToString() == "Playing";
        }

        public static string Query()
        {
            if (!Init()) return null;
            try
            {
                if (manager == null)
                    manager = Await(managerType.GetMethod("RequestAsync").Invoke(null, null), managerType);
                if (manager == null) return null;

                // Windows' "current" session is often a paused browser tab while music plays
                // somewhere else, so prefer it only if it is actually playing.
                object playingSession = null;
                object current = Call(managerType, manager, "GetCurrentSession");
                if (IsPlaying(current)) playingSession = current;
                else
                {
                    System.Collections.IEnumerable all = Call(managerType, manager, "GetSessions") as System.Collections.IEnumerable;
                    if (all != null)
                        foreach (object session in all)
                            if (IsPlaying(session)) { playingSession = session; break; }
                }
                if (playingSession == null) return null;

                object props = Await(Call(sessionType, playingSession, "TryGetMediaPropertiesAsync"), propsType);
                if (props == null) return null;
                string title = (Get(propsType, props, "Title") as string ?? "").Trim();
                if (title.Length == 0) return null;
                string artist = (Get(propsType, props, "Artist") as string ?? "").Trim();
                if (artist.EndsWith(" - Topic")) artist = artist.Substring(0, artist.Length - 8);
                return artist.Length > 0 ? title + "  \u00B7  " + artist : title;
            }
            catch
            {
                manager = null;   // ask Windows for a fresh manager next time
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- window
    class ClockWindow : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
        const int WM_EXITSIZEMOVE = 0x232, WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

        Settings settings = Settings.Load();
        System.Windows.Forms.Timer clockTimer = new System.Windows.Forms.Timer();
        System.Threading.Timer mediaTimer;
        System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer();
        AwaySettingsForm awayForm;
        NotifyIcon tray = new NotifyIcon();
        ToolStripMenuItem lockItem, lockPcItem, h24Item, playingItem, startupItem, bigMoonItem, tonightItem, alignItem, centerItem;
        string playing;
        string lastRendered = "";
        float scale = 1f;

        public static readonly Color TimeColor = Color.FromArgb(255, 243, 238, 247);
        static readonly Color DateColor = Color.FromArgb(255, 203, 191, 214);
        static readonly Color FactColor = Color.FromArgb(205, 150, 134, 166);
        static readonly Color SoftColor = Color.FromArgb(255, 167, 150, 181);

        public ClockWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Moon Clock";
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            BuildTray();
            clockTimer.Interval = 1000;
            clockTimer.Tick += delegate { Render(false); KeepOnBottom(); };
            clockTimer.Start();
            mediaTimer = new System.Threading.Timer(delegate { PollMedia(); }, null, 500, 3000);
            idleTimer.Interval = 1000;
            idleTimer.Tick += delegate { CheckIdle(); };
            idleTimer.Start();
            WeatherService.Refresh(settings, true);
            TaskbarCenter.SetEnabled(settings.CenterTaskbar && TaskbarCenter.Supported);
            TaskbarLook.Configure(settings.TaskbarStyle, settings.TaskbarMaximized);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (settings.Locked) cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Render(true);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            int x = settings.X, y = settings.Y;
            if (x == int.MinValue || !SystemInformation.VirtualScreen.Contains(new Point(x + 20, y + 20)))
            {
                x = work.Right - Width - (int)(48 * scale);
                y = work.Top + (int)(56 * scale);
            }
            if (settings.AlignRight && settings.RightEdge != int.MinValue && settings.X != int.MinValue)
                x = settings.RightEdge - Width;
            Location = new Point(x, y);
            Render(true);
            KeepOnBottom();
        }

        // --------------------------------------------------------- away mode
        void CheckIdle()
        {
            WeatherService.Refresh(settings, false);
            if (SkyManager.Active) return;
            uint idle = NativeSky.IdleMilliseconds();
            if (settings.IconsAfter > 0 && idle >= (uint)settings.IconsAfter * 60000u) DesktopIcons.Hide();
            else if (idle < 1500) DesktopIcons.Show();

            if (settings.SkyAfter > 0 && idle >= (uint)settings.SkyAfter * 60000u && !SkyBlocked())
                SkyManager.Start(settings);
        }

        bool SkyBlocked()
        {
            if (NativeSky.FullScreenBusy()) return true;
            if (settings.PauseForMedia && playing != null) return true;
            return false;
        }

        void OpenAwaySettings()
        {
            if (awayForm != null && !awayForm.IsDisposed) { awayForm.Activate(); return; }
            awayForm = new AwaySettingsForm(settings, delegate { SyncMenu(); });
            awayForm.Show();
        }

        void KeepOnBottom()
        {
            Native.SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_BOTTOM, no size/move/activate
        }

        // --------------------------------------------------------- tray menu
        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            lockItem = new ToolStripMenuItem("Lock position (clicks pass through)", null, delegate { SetLocked(!settings.Locked); });
            h24Item = new ToolStripMenuItem("Use 24-hour time", null, delegate { settings.Use24h = !settings.Use24h; settings.Save(); SyncMenu(); Render(true); });
            playingItem = new ToolStripMenuItem("Show what's playing", null, delegate { settings.ShowPlaying = !settings.ShowPlaying; settings.Save(); SyncMenu(); Render(true); });
            bigMoonItem = new ToolStripMenuItem("Large moon beside the time", null, delegate { settings.BigMoon = !settings.BigMoon; settings.Save(); SyncMenu(); Render(true); });
            centerItem = new ToolStripMenuItem("Center taskbar icons", null, delegate
            {
                settings.CenterTaskbar = !settings.CenterTaskbar; settings.Save();
                TaskbarCenter.SetEnabled(settings.CenterTaskbar && TaskbarCenter.Supported); SyncMenu();
            });
            alignItem = new ToolStripMenuItem("Align to the right", null, delegate
            {
                int rightEdge = Left + Width;
                settings.AlignRight = !settings.AlignRight;
                Render(true);
                if (settings.AlignRight) Left = rightEdge - Width;
                SavePosition(); SyncMenu(); Render(true);
            });
            tonightItem = new ToolStripMenuItem("Show tonight's constellation", null, delegate { settings.ShowTonight = !settings.ShowTonight; settings.Save(); SyncMenu(); Render(true); });
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { SetStartup(!IsStartup()); SyncMenu(); });
            menu.Items.Add(lockItem);
            menu.Items.Add(h24Item);
            menu.Items.Add(playingItem);
            menu.Items.Add(bigMoonItem);
            menu.Items.Add(tonightItem);
            menu.Items.Add(alignItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(centerItem);
            menu.Items.Add(LookMenu("Taskbar look", new string[] { "Clear", "Blur", "Acrylic (night tint)", "Windows default" },
                new string[] { "clear", "blur", "acrylic", "off" }, true));
            menu.Items.Add(LookMenu("When a window is maximized", new string[] { "Keep the same look", "Clear", "Blur", "Acrylic (night tint)", "Windows default" },
                new string[] { "same", "clear", "blur", "acrylic", "normal" }, false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Show the night sky now", null, delegate { SkyManager.Start(settings); }));
            lockPcItem = new ToolStripMenuItem("Lock my PC in away mode", null, delegate
            {
                settings.LockEnabled = !settings.LockEnabled; settings.Save(); SyncMenu();
                if (awayForm != null && !awayForm.IsDisposed) awayForm.SyncLock();
            });
            menu.Items.Add(lockPcItem);
            menu.Items.Add(new ToolStripMenuItem("Away mode settings...", null, delegate { OpenAwaySettings(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Move back to the top right", null, delegate { ResetPosition(); }));
            menu.Items.Add(new ToolStripMenuItem("Quit Moon Clock", null, delegate { DesktopIcons.Show(); TaskbarCenter.SetEnabled(false); TaskbarLook.Stop(); tray.Visible = false; Application.Exit(); }));
            playingItem.Enabled = NowPlaying.Supported;
            centerItem.Enabled = TaskbarCenter.Supported;
            if (!TaskbarCenter.Supported) centerItem.Text = "Center taskbar icons (Windows 11 does this itself)";
            if (!NowPlaying.Supported) playingItem.Text = "Show what's playing (needs Windows 10 version 1809 or newer)";

            tray.ContextMenuStrip = menu;
            tray.Text = "Moon Clock";
            tray.Icon = MakeTrayIcon();
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetLocked(!settings.Locked); };
            SyncMenu();
        }

        ToolStripMenuItem LookMenu(string title, string[] labels, string[] values, bool normalLook)
        {
            ToolStripMenuItem parent = new ToolStripMenuItem(title);
            for (int i = 0; i < labels.Length; i++)
            {
                string value = values[i];
                ToolStripMenuItem item = new ToolStripMenuItem(labels[i]);
                item.Click += delegate
                {
                    if (normalLook) settings.TaskbarStyle = value; else settings.TaskbarMaximized = value;
                    settings.Save();
                    TaskbarLook.Configure(settings.TaskbarStyle, settings.TaskbarMaximized);
                    foreach (ToolStripMenuItem sib in parent.DropDownItems) sib.Checked = sib == item;
                };
                item.Checked = (normalLook ? settings.TaskbarStyle : settings.TaskbarMaximized) == value;
                parent.DropDownItems.Add(item);
            }
            return parent;
        }

        void SyncMenu()
        {
            lockItem.Checked = settings.Locked;
            h24Item.Checked = settings.Use24h;
            lockPcItem.Checked = settings.LockEnabled;
            lockPcItem.Text = settings.LockEnabled ? "Lock my PC in away mode (after " + settings.LockAfter + " min)" : "Lock my PC in away mode";
            playingItem.Checked = settings.ShowPlaying && NowPlaying.Supported;
            startupItem.Checked = IsStartup();
            bigMoonItem.Checked = settings.BigMoon;
            tonightItem.Checked = settings.ShowTonight;
            alignItem.Checked = settings.AlignRight;
            centerItem.Checked = settings.CenterTaskbar && TaskbarCenter.Supported;
            tray.Text = settings.Locked ? "Moon Clock (locked)" : "Moon Clock (drag to move)";
        }

        Icon MakeTrayIcon()
        {
            Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Moon.Draw(g, 16, 16, 11, 0.32, false);
            }
            IntPtr handle = bmp.GetHicon();
            try { using (Icon temp = Icon.FromHandle(handle)) return (Icon)temp.Clone(); }
            finally { Native.DestroyIcon(handle); bmp.Dispose(); }
        }

        void SetLocked(bool locked)
        {
            settings.Locked = locked;
            settings.Save();
            int ex = Native.GetWindowLong(Handle, -20);
            ex = locked ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            Native.SetWindowLong(Handle, -20, ex);
            SyncMenu();
            Render(true);
            tray.ShowBalloonTip(1500, "Moon Clock", locked ? "Locked. Clicks now pass through it." : "Unlocked. Drag it anywhere, then lock it from here.", ToolTipIcon.None);
        }

        void ResetPosition()
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(work.Right - Width - (int)(48 * scale), work.Top + (int)(56 * scale));
            SavePosition();
            Render(true);
        }

        void SavePosition()
        {
            settings.X = Left;
            settings.Y = Top;
            settings.RightEdge = Left + Width;
            settings.Save();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        bool IsStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue("NightfallMoonClock") != null;
            }
            catch { return false; }
        }
        void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) key.SetValue("NightfallMoonClock", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("NightfallMoonClock", false);
                }
            }
            catch { }
        }

        // --------------------------------------------------------- dragging
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && !settings.Locked)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            }
            else if (e.Button == MouseButtons.Right)
            {
                tray.ContextMenuStrip.Show(Cursor.Position);
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE) { SavePosition(); KeepOnBottom(); }
        }

        // --------------------------------------------------------- media
        int mediaBusy;
        void PollMedia()
        {
            if (Interlocked.Exchange(ref mediaBusy, 1) == 1) return;
            try { PollMediaOnce(); } finally { Interlocked.Exchange(ref mediaBusy, 0); }
        }

        void PollMediaOnce()
        {
            // also checked for the away-mode lock, which never locks while something is playing
            string now = (settings.ShowPlaying || settings.PauseForMedia || settings.SkyPlaying || settings.ShouldLock) ? NowPlaying.Query() : null;
            NowPlaying.Last = now;
            if (now != playing)
            {
                playing = now;
                try { BeginInvoke(new MethodInvoker(delegate { Render(true); })); } catch { }
            }
        }

        // --------------------------------------------------------- drawing
        void Render(bool force)
        {
            DateTime now = DateTime.Now;
            string song = settings.ShowPlaying ? playing : null;
            TonightInfo tn = settings.ShowTonight ? TonightSky.Get(settings) : null;
            string key = now.ToString("HH:mm dd") + "|" + song + "|" + settings.Locked + "|" + settings.BigMoon + "|" + settings.Use24h + "|" + settings.AlignRight + "|" + (tn != null ? tn.Shape.Name + tn.Where : "");
            if (!force && key == lastRendered) return;
            lastRendered = key;
            using (Bitmap bmp = Compose(settings, now, song, scale))
            {
                if (settings.AlignRight && IsHandleCreated && Width > 1 && bmp.Width != Width)
                    Left = Left + Width - bmp.Width;      // grow and shrink to the left, so the right edge stays put
                Native.ApplyBitmap(this, bmp);
            }
        }

        public static Bitmap Compose(Settings settings, DateTime now, string song, float s)
        {
            string time = settings.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm");
            string date = now.ToString("dddd, d MMMM");
            double p = Moon.Phase(now.ToUniversalTime());
            string phase = Moon.Name(p) + "  \u00B7  " + Moon.PercentLit(p) + "% lit";
            song = Shorten(song, 44);

            float pad = 16 * s;
            float bigR = 22 * s;
            float left = settings.BigMoon ? pad + bigR * 3.3f : pad;   // room for the big moon and its glow

            TonightInfo tonight = settings.ShowTonight ? TonightSky.Get(settings) : null;
            using (GraphicsPath timePath = TextPath(time, settings.Font, 64 * s, FontStyle.Regular))
            using (GraphicsPath datePath = TextPath(date, "Segoe UI Semilight", 16 * s, FontStyle.Regular))
            using (GraphicsPath phasePath = TextPath(phase, "Segoe UI", 12.5f * s, FontStyle.Regular))
            using (GraphicsPath songPath = song != null ? TextPath(song, "Segoe UI", 12.5f * s, FontStyle.Regular) : null)
            using (GraphicsPath tonightPath = tonight != null ? TextPath("Tonight: " + tonight.Shape.Name + "  \u00B7  " + tonight.Where + ", " + tonight.When, "Segoe UI", 12.5f * s, FontStyle.Regular) : null)
            {
                RectangleF tb = timePath.GetBounds();
                RectangleF db = datePath.GetBounds();
                RectangleF pb = phasePath.GetBounds();
                RectangleF sb = songPath != null ? songPath.GetBounds() : RectangleF.Empty;

                float iconW = 13 * s, iconGap = 9 * s;
                float lineH = Math.Max(pb.Height, 13 * s);
                float timeY = pad;
                float dateY = timeY + tb.Height + 16 * s;
                float phaseY = dateY + db.Height + 14 * s;
                RectangleF nb = tonightPath != null ? tonightPath.GetBounds() : RectangleF.Empty;
                float tonightY = phaseY + lineH + 9 * s;
                float afterTonight = tonightPath != null ? tonightY + Math.Max(nb.Height, 13 * s) + 9 * s : tonightY;
                float songY = afterTonight;
                float contentW = Math.Max(tb.Width, Math.Max(db.Width, iconW + iconGap + Math.Max(pb.Width, Math.Max(sb.Width, nb.Width))));
                GraphicsPath factPath = null;
                RectangleF fb = RectangleF.Empty;
                if (tonight != null)
                {
                    factPath = WrappedPath(tonight.Shape.Fact, "Segoe UI", 11.5f * s, Math.Max(contentW - iconW - iconGap, 240 * s), settings.AlignRight);
                    fb = factPath.GetBounds();
                    songY = afterTonight + fb.Height + 10 * s;
                    contentW = Math.Max(contentW, iconW + iconGap + fb.Width);
                }
                float bottom = (songPath != null ? songY + Math.Max(sb.Height, 13 * s) : songY - 9 * s) + pad;
                int w = (int)Math.Ceiling(left + contentW + pad);
                int h = (int)Math.Ceiling(bottom);

                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    if (!settings.Locked)
                        using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                            g.FillRectangle(hit, 0, 0, w, h);   // invisible, lets you grab it anywhere

                    bool right = settings.AlignRight;
                    float R = left + contentW;                       // right edge of the content
                    float textEnd = R - iconW - iconGap;              // where text ends when icons sit on the right

                    if (settings.BigMoon)
                        Moon.Draw(g, pad + bigR * 1.3f, timeY + tb.Height / 2, bigR, p, true);

                    DrawText(g, timePath, (right ? R - tb.Width : left) - tb.X, timeY - tb.Y, TimeColor);
                    DrawText(g, datePath, (right ? R - db.Width : left) - db.X, dateY - db.Y, DateColor);

                    float iconX = right ? R - iconW : left;
                    float iconCx = iconX + iconW / 2, r = 6 * s;
                    Moon.Draw(g, iconCx, phaseY + lineH / 2, r, p, false);
                    DrawText(g, phasePath, (right ? textEnd - pb.Width : left + iconW + iconGap) - pb.X, phaseY + (lineH - pb.Height) / 2 - pb.Y, SoftColor);

                    if (tonightPath != null)
                    {
                        float th = Math.Max(nb.Height, 13 * s);
                        TonightSky.DrawMini(g, tonight.Shape, iconX, tonightY + (th - iconW) / 2, iconW, SoftColor);
                        DrawText(g, tonightPath, (right ? textEnd - nb.Width : left + iconW + iconGap) - nb.X, tonightY + (th - nb.Height) / 2 - nb.Y, SoftColor);
                        DrawText(g, factPath, (right ? textEnd - fb.Width : left + iconW + iconGap) - fb.X, afterTonight - fb.Y, FactColor);
                    }
                    if (factPath != null) factPath.Dispose();

                    if (songPath != null)
                    {
                        float sh = Math.Max(sb.Height, 13 * s);
                        DrawNote(g, iconCx, songY + sh / 2, s);
                        DrawText(g, songPath, (right ? textEnd - sb.Width : left + iconW + iconGap) - sb.X, songY + (sh - sb.Height) / 2 - sb.Y, SoftColor);
                    }
                }
                return bmp;
            }
        }

        static void DrawNote(Graphics g, float cx, float cy, float s)
        {
            using (SolidBrush b = new SolidBrush(SoftColor))
            using (Pen pen = new Pen(SoftColor, 1.3f * s))
            {
                g.FillEllipse(b, cx - 5 * s, cy + 1.5f * s, 5 * s, 4 * s);
                g.DrawLine(pen, cx - 0.6f * s, cy + 3.5f * s, cx - 0.6f * s, cy - 6 * s);
                g.DrawLine(pen, cx - 0.6f * s, cy - 6 * s, cx + 4 * s, cy - 4 * s);
            }
        }

        static GraphicsPath WrappedPath(string text, string family, float px, float maxWidth, bool alignRight)
        {
            GraphicsPath path = new GraphicsPath();
            FontFamily ff;
            try { ff = new FontFamily(family); } catch { ff = FontFamily.GenericSansSerif; }
            using (ff)
            using (StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags &= ~StringFormatFlags.NoWrap;
                if (alignRight) fmt.Alignment = StringAlignment.Far;
                path.AddString(text, ff, (int)FontStyle.Regular, px, new RectangleF(0, 0, maxWidth, 1000), fmt);
            }
            return path;
        }

        static GraphicsPath TextPath(string text, string family, float px, FontStyle style)
        {
            GraphicsPath path = new GraphicsPath();
            FontFamily ff;
            try { ff = new FontFamily(family); }
            catch { try { ff = new FontFamily("Segoe UI"); } catch { ff = FontFamily.GenericSansSerif; } }
            using (ff)
            {
                if (!ff.IsStyleAvailable(style)) style = FontStyle.Regular;
                path.AddString(text, ff, (int)style, px, new PointF(0, 0), StringFormat.GenericTypographic);
            }
            return path;
        }

        static string Shorten(string s, int max)
        {
            if (s == null || s.Length <= max) return s;
            return s.Substring(0, max - 1).TrimEnd() + "\u2026";
        }

        static void DrawText(Graphics g, GraphicsPath path, float x, float y, Color color)
        {
            using (Matrix shadowMove = new Matrix())
            using (GraphicsPath shadow = (GraphicsPath)path.Clone())
            {
                shadowMove.Translate(x, y + 1.2f);
                shadow.Transform(shadowMove);
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
                    g.FillPath(sh, shadow);
            }
            using (Matrix move = new Matrix())
            using (GraphicsPath copy = (GraphicsPath)path.Clone())
            {
                move.Translate(x, y);
                copy.Transform(move);
                using (SolidBrush b = new SolidBrush(color))
                    g.FillPath(b, copy);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (mediaTimer != null) mediaTimer.Dispose();
                clockTimer.Dispose();
                tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- Win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; public POINT(int x, int y) { this.x = x; this.y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        public static void ApplyBitmap(Form form, Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBitmap);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                POINT src = new POINT(0, 0);
                POINT dst = new POINT(form.Left, form.Top);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0; blend.BlendFlags = 0; blend.SourceConstantAlpha = 255; blend.AlphaFormat = 1;
                UpdateLayeredWindow(form.Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
                form.Size = new Size(bmp.Width, bmp.Height);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBitmap != IntPtr.Zero) { SelectObject(memDc, old); DeleteObject(hBitmap); }
                DeleteDC(memDc);
            }
        }
    }
}
