// Nightfall away mode: hides desktop icons when you're idle, then fades in a full-screen night sky.
// C# 5 syntax only (built with the compiler that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace NightfallMoonClock
{
    static class Alpha { public static int Clamp(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; } }
    // ---------------------------------------------------------------- Win32 for away mode
    static class NativeSky
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool LockWorkStation();
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

        public static uint LastInputTick()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            try { GetLastInputInfo(ref info); } catch { return 0; }
            return info.dwTime;
        }

        public static uint IdleMilliseconds()
        {
            return unchecked((uint)Environment.TickCount - LastInputTick());
        }

        // true while a full-screen app, game, or presentation is running
        public static bool FullScreenBusy()
        {
            try
            {
                int state;
                if (SHQueryUserNotificationState(out state) != 0) return false;
                // 1 = locked or screen saver, 2 = full-screen app, 3 = full-screen game, 4 = presentation mode
                return state == 1 || state == 2 || state == 3 || state == 4;
            }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- desktop icons
    static class DesktopIcons
    {
        static IntPtr hiddenList = IntPtr.Zero;

        static IntPtr FindList()
        {
            IntPtr defView = NativeSky.FindWindowEx(NativeSky.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero)
            {
                NativeSky.EnumWindows(delegate(IntPtr hWnd, IntPtr l)
                {
                    IntPtr dv = NativeSky.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (dv != IntPtr.Zero) { defView = dv; return false; }
                    return true;
                }, IntPtr.Zero);
            }
            if (defView == IntPtr.Zero) return IntPtr.Zero;
            return NativeSky.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        public static void Hide()
        {
            if (hiddenList != IntPtr.Zero) return;
            IntPtr list = FindList();
            if (list == IntPtr.Zero || !NativeSky.IsWindowVisible(list)) return;
            NativeSky.ShowWindow(list, 0);
            hiddenList = list;
        }

        public static void Show()
        {
            if (hiddenList == IntPtr.Zero) return;
            NativeSky.ShowWindow(hiddenList, 5);
            IntPtr fresh = FindList();                  // in case Explorer restarted
            if (fresh != IntPtr.Zero && fresh != hiddenList) NativeSky.ShowWindow(fresh, 5);
            hiddenList = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- weather
    // Is the internet connection metered (a phone hotspot, a capped plan)? Asked of Windows' own network list.
    static class NetCost
    {
        [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface INetworkCostManager
        {
            void GetCost(out uint cost, IntPtr destIPAddr);
            void GetDataPlanStatus(IntPtr status, IntPtr destIPAddr);
            void SetDestinationAddresses(uint length, IntPtr addrList, [MarshalAs(UnmanagedType.VariantBool)] bool append);
        }
        [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")] class NetworkListManager { }
        static DateTime checkedAt = DateTime.MinValue; static bool metered;
        public static bool Metered()
        {
            if ((DateTime.Now - checkedAt).TotalMinutes < 5) return metered;
            checkedAt = DateTime.Now; metered = false;
            try
            {
                object nlm = new NetworkListManager();
                try
                {
                    uint cost; ((INetworkCostManager)nlm).GetCost(out cost, IntPtr.Zero);
                    // fixed or variable cost, over the data limit, or roaming
                    metered = (cost & (0x2 | 0x4 | 0x10000 | 0x40000)) != 0;
                }
                finally { Marshal.ReleaseComObject(nlm); }
            }
            catch { metered = false; }
            return metered;
        }
        public static bool BatterySaver()
        {
            try { WallpaperNative.POWER p; return WallpaperNative.GetSystemPowerStatus(out p) && p.SystemStatusFlag == 1; }
            catch { return false; }
        }
    }

    static class WeatherService
    {
        static int code = -1;
        static DateTime fetched = DateTime.MinValue;
        public static DateTime Sunrise = DateTime.MinValue, Sunset = DateTime.MinValue;

        public static string KindFromCode(int c)
        {
            if (c == 45 || c == 48) return "fog";
            if ((c >= 51 && c <= 67) || (c >= 80 && c <= 82)) return "rain";
            if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "snow";
            if (c >= 95) return "storm";
            return "clear";
        }

        // weather for live scenes: like Kind, but also tells overcast skies apart ("cloudy")
        public static string SceneKind(Settings s)
        {
            if (s.Weather != "auto") return s.Weather;
            if (code < 0) return "clear";
            if (code == 2 || code == 3) return "cloudy";
            return KindFromCode(code);
        }

        public static string Kind(Settings s)
        {
            if (s.Weather != "auto") return s.Weather;
            return code >= 0 ? KindFromCode(code) : "clear";
        }

        public static string Status(Settings s)
        {
            if (s.Weather != "auto") return "Weather is locked to one style.";
            if (double.IsNaN(s.Lat)) return "Add your city to match the real weather.";
            if (code < 0) return "Checking the weather for " + s.City + "...";
            return "Showing " + KindFromCode(code) + " for " + s.City + ".";
        }

        static string Download(string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            using (WebClient wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers.Add("User-Agent", "NightfallMoonClock");
                return wc.DownloadString(url);
            }
        }

        // Checks at most once an hour, and only when it matters: while a scene is showing,
        // never on a metered connection or in battery saver. (Opening Settings still checks right away.)
        public static void Refresh(Settings s, bool force)
        {
            if (double.IsNaN(s.Lat)) return;
            if (!force)
            {
                if ((DateTime.Now - fetched).TotalMinutes < 60) return;
                if (!SkyManager.Active && !LiveWallpaper.AnyShowing) return;
                if (NetCost.Metered() || NetCost.BatterySaver()) return;
            }
            fetched = DateTime.Now;
            double lat = s.Lat, lon = s.Lon;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string url = string.Format(CultureInfo.InvariantCulture,
                        "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=weather_code&daily=sunrise,sunset&timezone=auto&forecast_days=1", lat, lon);
                    string json = Download(url);
                    Match m = Regex.Match(json, "\"current\"\\s*:\\s*\\{[^}]*\"weather_code\"\\s*:\\s*(\\d+)");
                    if (m.Success) code = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    Match sr = Regex.Match(json, "\"sunrise\"\\s*:\\s*\\[\\s*\"([^\"]+)\"");
                    Match ss = Regex.Match(json, "\"sunset\"\\s*:\\s*\\[\\s*\"([^\"]+)\"");
                    DateTime d;
                    if (sr.Success && DateTime.TryParse(sr.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) Sunrise = d;
                    if (ss.Success && DateTime.TryParse(ss.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) Sunset = d;
                }
                catch { fetched = DateTime.Now.AddMinutes(-25); }   // offline: try again in about 5 minutes
            });
        }

        public static bool FindCity(string name, out string display, out double lat, out double lon)
        {
            display = null; lat = double.NaN; lon = double.NaN;
            string json = Download("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name=" + Uri.EscapeDataString(name));
            int start = json.IndexOf("\"results\"", StringComparison.Ordinal);
            if (start < 0) return false;
            string first = json.Substring(start);
            Match n = Regex.Match(first, "\"name\"\\s*:\\s*\"([^\"]*)\"");
            Match la = Regex.Match(first, "\"latitude\"\\s*:\\s*(-?[0-9.]+)");
            Match lo = Regex.Match(first, "\"longitude\"\\s*:\\s*(-?[0-9.]+)");
            Match co = Regex.Match(first, "\"country\"\\s*:\\s*\"([^\"]*)\"");
            if (!n.Success || !la.Success || !lo.Success) return false;
            lat = double.Parse(la.Groups[1].Value, CultureInfo.InvariantCulture);
            lon = double.Parse(lo.Groups[1].Value, CultureInfo.InvariantCulture);
            display = n.Groups[1].Value + (co.Success ? ", " + co.Groups[1].Value : "");
            code = -1;
            return true;
        }
    }

    // ---------------------------------------------------------------- poetic lines
    static class Thoughts
    {
        static List<string> lines;
        static List<int> order = new List<int>();
        static Random rng = new Random();

        static void Load()
        {
            lines = new List<string>();
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "thoughts.txt");
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string t = raw.Trim();
                    if (t.Length > 0) lines.Add(t.Replace(" | ", "\n").Replace("|", "\n"));
                }
            }
            catch { }
            if (lines.Count == 0)
            {
                lines.Add("the river talks in the rain and I stay up to hear it out.");
                lines.Add("The moon keeps its own slow schedule.");
                lines.Add("Breathe in. Let your shoulders drop.");
            }
        }

        public static string Next()
        {
            if (lines == null) Load();
            if (order.Count == 0)
            {
                for (int i = 0; i < lines.Count; i++) order.Add(i);
                for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); int t = order[i]; order[i] = order[j]; order[j] = t; }
            }
            int idx = order[order.Count - 1];
            order.RemoveAt(order.Count - 1);
            return lines[idx];
        }
    }

    // ---------------------------------------------------------------- live illustrated scenes
    class SceneData
    {
        public Bitmap Background;
        public string Dir, Key, Rain = "none", Snow = "none", FrontFile;
        public bool NoShoot, LifeUnder;
        public List<string[]> Sprites = new List<string[]>();
        public List<float[]> SpriteSmoke = new List<float[]>(), SpriteGlow = new List<float[]>(), SpriteWheel = new List<float[]>();
        public List<float[]> Pulses = new List<float[]>(), GlassDrops = new List<float[]>(), Water = new List<float[]>(), Curtains = new List<float[]>();
        public float SatelliteEvery, DebrisEvery, AstronautEvery, PlaneEvery, ShotEvery;
        public bool NoDawn;
        public string DawnFile, DawnFront;
        public List<float[]> Falls = new List<float[]>();
        public List<string[]> Sways = new List<string[]>();
        public List<float[]> Cats = new List<float[]>(), Moons = new List<float[]>();
        public List<string[]> Surprises = new List<string[]>();
        public float ClockTop = -1;   // where the clock sits, as a share of the screen height (default if -1)
        public List<float[]> Stars = new List<float[]>(), Lights = new List<float[]>(), Shimmers = new List<float[]>(), Ripples = new List<float[]>();
        public List<float[]> Windows = new List<float[]>(), Smokes = new List<float[]>(), Waves = new List<float[]>(), Birds = new List<float[]>();
        public List<float[]> Bobs = new List<float[]>(), Sands = new List<float[]>(), Toggles = new List<float[]>(), Beacons = new List<float[]>();
        public List<float[]> Puddles = new List<float[]>(), Fireflies = new List<float[]>();
        public List<string[]> Layers = new List<string[]>(), Reflects = new List<string[]>(), Fades = new List<string[]>();
        public List<PointF> Skyline = new List<PointF>();

        public static readonly string[] Keys = { "river", "galaxy", "city", "raincity", "snowcabin", "ocean", "desert", "rooftop", "fireflies", "aurora", "train", "village", "room", "space", "porch", "meadow", "falls", "lakedawn" };
        public static readonly string[] Names = { "River Night", "Galaxy Peaks", "City Below", "Rain over the City", "Snowy Cabin", "Ocean Shore", "Desert Night", "Rooftop Rain", "Firefly Lake", "Northern Lights", "Steam Train", "Snowy Village", "Cozy Room", "Earth from Space", "Porch in the Forest", "Moonlit Meadow", "Wide Falls", "Mountain Lake at Dawn" };
        static readonly Random pick = new Random();

        // "room" becomes one of its four window views, chosen by the Cozy Room setting
        public static string Resolve(string key, Settings s)
        {
            if (key != "room") return key;
            string v = s != null ? s.RoomView : "random";
            if (v == "weather")
            {
                string w = WeatherService.Kind(s);
                if (w == "rain" || w == "storm") return "room-city";
                if (w == "snow") return "room-snow";
                v = "random";
            }
            if (v == "random") { string[] all = { "room-city", "room-lake", "room-snow", "room-sea" }; return all[pick.Next(all.Length)]; }
            return "room-" + v;
        }

        public static string Folder { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "scenes"); } }

        public static SceneData Load(string key) { return Load(key, null); }
        public static SceneData Load(string key, Settings settings)
        {
            key = Resolve(key, settings);
            try
            {
                string dir = Folder;
                string img = Path.Combine(dir, key + ".jpg");
                if (!File.Exists(img)) img = Path.Combine(dir, key + ".png");
                string txt = Path.Combine(dir, key + ".txt");
                if (!File.Exists(img) || !File.Exists(txt)) return null;
                SceneData d = new SceneData();
                d.Dir = dir; d.Key = key;
                using (Image im = Image.FromFile(img)) d.Background = new Bitmap(im);
                foreach (string raw in File.ReadAllLines(txt))
                {
                    string[] p = raw.Trim().Split(' ');
                    if (p.Length == 0 || p[0].Length == 0) continue;
                    switch (p[0])
                    {
                        case "rain": d.Rain = p[1]; break;
                        case "snow": d.Snow = p[1]; break;
                        case "noshoot": d.NoShoot = true; break;
                        case "clock": d.ClockTop = F(p[1]); break;
                        case "lifeunder": d.LifeUnder = true; break;
                        case "sprite": d.Sprites.Add(p); break;
                        case "spritesmoke": d.SpriteSmoke.Add(Nums(p, 2)); break;
                        case "spriteglow": d.SpriteGlow.Add(Nums(p, 2)); break;
                        case "spritewheel": d.SpriteWheel.Add(Nums(p, 2)); break;
                        case "pulse": d.Pulses.Add(Nums(p)); break;
                        case "glassdrops": d.GlassDrops.Add(Nums(p)); break;
                        case "water": d.Water.Add(Nums(p)); break;
                        case "curtain": d.Curtains.Add(Nums(p)); break;
                        case "satellite": d.SatelliteEvery = F(p[1]); break;
                        case "debris": d.DebrisEvery = F(p[1]); break;
                        case "astronaut": d.AstronautEvery = F(p[1]); break;
                        case "plane": d.PlaneEvery = F(p[1]); break;
                        case "shots": d.ShotEvery = F(p[1]); break;
                        case "nodawn": d.NoDawn = true; break;
                        case "dawn": d.DawnFile = p[1]; break;
                        case "dawnfront": d.DawnFront = p[1]; break;
                        case "fall": d.Falls.Add(Nums(p)); break;
                        case "sway": d.Sways.Add(p); break;
                        case "cat": d.Cats.Add(Nums(p)); break;
                        case "moon": d.Moons.Add(Nums(p)); break;
                        case "surprise": d.Surprises.Add(p); break;
                        case "star": d.Stars.Add(Nums(p)); break;
                        case "light": d.Lights.Add(Nums(p)); break;
                        case "shimmer": d.Shimmers.Add(Nums(p)); break;
                        case "ripple": d.Ripples.Add(Nums(p)); break;
                        case "window": d.Windows.Add(Nums(p)); break;
                        case "smoke": d.Smokes.Add(Nums(p)); break;
                        case "waves": d.Waves.Add(Nums(p)); break;
                        case "bird": d.Birds.Add(Nums(p)); break;
                        case "bob": d.Bobs.Add(Nums(p)); break;
                        case "sand": d.Sands.Add(Nums(p)); break;
                        case "toggle": d.Toggles.Add(Nums(p)); break;
                        case "beacon": d.Beacons.Add(Nums(p)); break;
                        case "puddle": d.Puddles.Add(Nums(p)); break;
                        case "fireflies": d.Fireflies.Add(Nums(p)); break;
                        case "layer": d.Layers.Add(p); break;
                        case "reflect": d.Reflects.Add(p); break;
                        case "front": d.FrontFile = p[1]; break;
                        case "fade": case "fadeafter": d.Fades.Add(p); break;
                        case "skyline":
                            for (int i = 1; i < p.Length; i++)
                            {
                                string[] xy = p[i].Split(',');
                                d.Skyline.Add(new PointF(F(xy[0]), F(xy[1])));
                            }
                            break;
                    }
                }
                return d;
            }
            catch { return null; }
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static float[] Nums(string[] p) { return Nums(p, 1); }
        static float[] Nums(string[] p, int from)
        {
            float[] n = new float[p.Length - from];
            for (int i = from; i < p.Length; i++) n[i - from] = F(p[i]);
            return n;
        }
    }

    // Soft round glows (bright centre fading smoothly to nothing), cached by colour and strength.
    static class GlowSprite
    {
        static readonly Dictionary<int, Bitmap> cache = new Dictionary<int, Bitmap>();
        public static void Draw(Graphics g, float x, float y, float radius, int r, int gr, int b, float strength)
        {
            if (strength <= 0.01f || radius < 0.5f) return;
            int level = Math.Max(1, Math.Min(16, (int)Math.Round(strength * 16)));
            int key = (r << 16) ^ (gr << 8) ^ b ^ (level << 24);
            Bitmap bmp;
            if (!cache.TryGetValue(key, out bmp))
            {
                const int N = 64;
                bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
                for (int yy = 0; yy < N; yy++)
                    for (int xx = 0; xx < N; xx++)
                    {
                        double dx = (xx + 0.5 - N / 2.0) / (N / 2.0), dy = (yy + 0.5 - N / 2.0) / (N / 2.0);
                        double dd = Math.Sqrt(dx * dx + dy * dy);
                        double a = dd >= 1 ? 0 : Math.Pow(1 - dd, 2.2) * level / 16.0;
                        bmp.SetPixel(xx, yy, Color.FromArgb(Alpha.Clamp(a * 255), r, gr, b));
                    }
                if (cache.Count > 400) cache.Clear();
                cache[key] = bmp;
            }
            g.DrawImage(bmp, x - radius, y - radius, radius * 2, radius * 2);
        }
    }

    // When the sun rises and sets, worked out from the date and where you are (no internet needed).
    static class SunClock
    {
        public static void Times(double lat, double lon, DateTime day, out DateTime rise, out DateTime set)
        {
            double n = day.DayOfYear, rad = Math.PI / 180;
            double decl = -23.44 * Math.Cos(2 * Math.PI / 365 * (n + 10)) * rad;
            double cosH = (Math.Cos(90.833 * rad) - Math.Sin(lat * rad) * Math.Sin(decl)) / (Math.Cos(lat * rad) * Math.Cos(decl));
            cosH = Math.Max(-1, Math.Min(1, cosH));
            double h = Math.Acos(cosH) / rad / 15;   // hours from solar noon
            double b = 2 * Math.PI * (n - 81) / 364;
            double eot = (9.87 * Math.Sin(2 * b) - 7.53 * Math.Cos(b) - 1.5 * Math.Sin(b)) / 60;   // equation of time, hours
            double tz = TimeZoneInfo.Local.GetUtcOffset(day).TotalHours;
            double noon = 12 - lon / 15 - eot + tz;
            rise = day.Date.AddHours(noon - h); set = day.Date.AddHours(noon + h);
        }

        // 0 = night, 1 = full dawn colours
        public static float DawnAmount(Settings s, DateTime now)
        {
            string mode = s != null ? s.TimeOfDay : "sunrise";
            if (mode == "night") return 0;
            if (mode == "dawn") return 1;
            double lat = s != null && !double.IsNaN(s.Lat) ? s.Lat : 23, lon = s != null && !double.IsNaN(s.Lon) ? s.Lon : TimeZoneInfo.Local.GetUtcOffset(now).TotalHours * 15;
            DateTime rise, set;
            Times(lat, lon, now, out rise, out set);
            float f = Bump((now - rise).TotalMinutes, -80, -20, 45, 110);
            if (mode == "both") f = Math.Max(f, Bump((now - set).TotalMinutes, -100, -35, 20, 70));
            return f;
        }

        static float Bump(double m, double a, double b, double c, double d)
        {
            if (m <= a || m >= d) return 0;
            double f = m < b ? (m - a) / (b - a) : m <= c ? 1 : (d - m) / (d - c);
            return (float)(f * f * (3 - 2 * f));
        }
    }

    // Draws and animates a scene. Used by the away-mode sky and the live wallpaper.
    class SceneRenderer : IDisposable
    {
        class Drift { public Bitmap Bmp; public float Y, Speed; public bool AfterFront; }
        // frames that slowly crossfade into each other in place (used for the aurora)
        class Fade { public List<Bitmap> Frames = new List<Bitmap>(); public Bitmap Mix; public float Y, Period; public bool AfterFront; public double LastMix = -1e9; }
        readonly List<Fade> fades = new List<Fade>();
        class Part { public float X, Y, VX, VY, Size, Age, Life, Seed; public int Kind, Col; }   // 0 smoke, 1 sand, 2 puddle ripple, 6-8 star trails
        // shooting star trails: style "dust", "ribbon", "sparks" or "none"; length "brief" (1 s), "linger" (3 s), "long" (6 s)
        public string TrailStyle = "dust", TrailLength = "linger";
        static readonly int[][] TrailColours = { new[] { 236, 236, 255 }, new[] { 255, 214, 140 }, new[] { 210, 180, 255 }, new[] { 255, 170, 215 }, new[] { 170, 220, 255 } };
        int trailCount;

        SceneData d;
        int w, h;
        float sc, ox, oy, ui;
        readonly Random rng = new Random();
        Bitmap bg, front;
        Point frontAt;
        readonly List<Drift> drifts = new List<Drift>();
        GraphicsPath skyClip;
        Region skyRegion;
        readonly List<float[]> twinkles = new List<float[]>();   // x, y, r, phase, speed
        float[] phase;
        readonly List<Part> parts = new List<Part>();
        readonly List<float[]> streaks = new List<float[]>();    // x, y, vx, vy, start, duration, tail, width
        float[] dx, dy, dlen, dspeed, dph, dr;
        int dropKind;                                            // 0 none, 1 rain, 2 snow
        bool[] toggleOn; double[] toggleNext;
        float[] bx, by, bv, bph; RectangleF[] birdBox;
        float[] fx, fy, fvx, fvy, fph; RectangleF[] ffBox;
        double nextShot, shimmerStart = -1e9, lastSmoke, lastSand, lastPuddle;
        readonly SolidBrush brush = new SolidBrush(Color.White);
        readonly Pen pen = new Pen(Color.White, 1);

        public bool AllowShootingStars = true;
        // dawn: a second painting blended in around sunrise
        Bitmap bgDawn, bgMix, frontDawn, frontMix; float dawnAmt, dawnTarget, mixedAt = -1;
        public bool HasDawn { get { return bgDawn != null; } }
        public float DawnShown { get { return dawnAmt; } }
        const float DawnFadeSeconds = 8f;

        // The colours always ease toward where they should be. Real sunrise changes are much slower than this,
        // so it only shows when the scene was frozen (windows open) and has catching up to do.
        public void SetDawn(float f) { SetDawn(f, false); }
        public void SetDawn(float f, bool immediate)
        {
            dawnTarget = HasDawn ? Math.Max(0, Math.Min(1, f)) : 0;
            if (immediate) { dawnAmt = dawnTarget; Reblend(true); }
        }
        void EaseDawn(float dt)
        {
            if (dawnAmt == dawnTarget) return;
            float stepBy = dt / DawnFadeSeconds;
            dawnAmt = Math.Abs(dawnTarget - dawnAmt) <= stepBy ? dawnTarget : dawnAmt + Math.Sign(dawnTarget - dawnAmt) * stepBy;
            Reblend(dawnAmt == dawnTarget);
        }
        void Reblend(bool force)
        {
            if (!force && Math.Abs(dawnAmt - mixedAt) < 0.03f) return;   // re-blend in small steps, not every frame
            mixedAt = dawnAmt;
            Blend(bg, bgDawn, ref bgMix);
            if (front != null && frontDawn != null) Blend(front, frontDawn, ref frontMix);
        }
        void Blend(Bitmap a, Bitmap b, ref Bitmap mix)
        {
            if (a == null || b == null) return;
            if (mix == null) mix = new Bitmap(a.Width, a.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(mix))
            {
                g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(a, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = dawnAmt;
                using (ImageAttributes ia = new ImageAttributes()) { ia.SetColorMatrix(cm); g.DrawImage(b, new Rectangle(0, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia); }
            }
        }
        Bitmap CurrentBg { get { return dawnAmt <= 0.01f || bgMix == null ? bg : dawnAmt >= 0.99f ? bgDawn : bgMix; } }
        Bitmap CurrentFront { get { return frontDawn == null || dawnAmt <= 0.01f || frontMix == null ? front : dawnAmt >= 0.99f ? frontDawn : frontMix; } }
        // falling water and swaying layers
        class FallStreak { public int Fall; public float T, Off, Speed, Len; }
        readonly List<FallStreak> fallStreaks = new List<FallStreak>();
        class Sway { public Bitmap Bmp; public float Y, Amp; }
        readonly List<Sway> sways = new List<Sway>();
        // interactions and options
        public bool ClickStars = true, ClickLights = true, ClickWater = true, Parallax = true;
        public float TrainGapSeconds = -1;   // -1 = scene default, 0 = always looping
        // play: hover reactions, extra clicks, surprises
        public bool HFireflies = true, HStars = true, HCat = true, HWater = true, HGrassSnow = true, HBirds = true, HLamps = true;
        public bool CMeteorHold = true, CMoon = true, CTrain = true, CCatMug = true, CCatch = true, CDraw = true, CWeather = true, Surprises = true;
        public Func<string[]> MoonInfo;
        // special days and music
        string festival; float horizonY;
        class Burst { public int Type; public float X, Y, R; public double Start; public int[] Col; public float[] Ang; }
        readonly List<Burst> bursts = new List<Burst>();
        class Lantern { public float X, Y, VY, S, Ph; }
        readonly List<Lantern> lanterns = new List<Lantern>();
        float[][] diyas; float[][] holiPuffs; float[][] holiSpecks; double nextBurst, nextLantern;
        static readonly int[][] FestColours = { new[] { 255, 200, 130 }, new[] { 205, 180, 255 }, new[] { 255, 170, 215 }, new[] { 170, 220, 255 }, new[] { 255, 222, 170 } };
        static readonly int[][] HoliColours = { new[] { 255, 80, 160 }, new[] { 255, 200, 60 }, new[] { 80, 190, 255 }, new[] { 130, 230, 100 }, new[] { 190, 110, 255 } };
        float musicGlow, musicTarget;
        public void SetMusicPlaying(bool playing) { musicTarget = playing ? 1 : 0; }
        public string Festival { get { return festival; } }
        public void SetFestival(string f)
        {
            if (f == festival) return;
            festival = f; bursts.Clear(); lanterns.Clear(); diyas = null; holiPuffs = null; holiSpecks = null; nextBurst = 0; nextLantern = 0;
            RectangleF sb = new RectangleF(0, 0, w, h * 0.6f);
            if (skyRegion != null) using (Bitmap one = new Bitmap(1, 1)) using (Graphics tg = Graphics.FromImage(one)) sb = skyRegion.GetBounds(tg);
            horizonY = Math.Max(h * 0.3f, Math.Min(h * 0.8f, sb.Bottom * 0.88f));
            if (f == "diwali" && d.Water.Count > 0)   // little lamps floating on the water
            {
                List<float[]> list = new List<float[]>();
                for (int i = 0; i < 24; i++)
                {
                    float[] wr = d.Water[rng.Next(d.Water.Count)]; PointF a = M(wr[0], wr[1]), c = M(wr[2], wr[3]);
                    float y = a.Y + (float)Math.Pow(rng.NextDouble(), 0.8) * (c.Y - a.Y) * 0.85f + 6 * ui;
                    list.Add(new float[] { a.X + (float)rng.NextDouble() * (c.X - a.X), y, (float)rng.NextDouble() * 6, (float)(rng.NextDouble() - 0.5) * 5 * ui });
                }
                list.Sort(delegate (float[] p, float[] q) { return p[1].CompareTo(q[1]); });
                diyas = list.ToArray();
            }
            if (f == "holi")
            {
                holiPuffs = new float[9 * 12][]; int k = 0;
                for (int c = 0; c < 9; c++)
                {
                    float cx = (float)rng.NextDouble() * w, cy = h * (0.08f + (float)rng.NextDouble() * 0.34f), v = (6 + (float)rng.NextDouble() * 8) * ui; int col = rng.Next(HoliColours.Length);
                    for (int j = 0; j < 12; j++) holiPuffs[k++] = new float[] { cx + (float)(rng.NextDouble() - 0.5) * 140 * ui, cy + (float)(rng.NextDouble() - 0.5) * 40 * ui, (26 + (float)rng.NextDouble() * 30) * ui, v, col };
                }
                holiSpecks = new float[140][];
                for (int i = 0; i < holiSpecks.Length; i++) holiSpecks[i] = new float[] { (float)rng.NextDouble() * w, (float)rng.NextDouble() * h, (10 + (float)rng.NextDouble() * 18) * ui, (float)rng.NextDouble() * 6, rng.Next(HoliColours.Length), (1.2f + (float)rng.NextDouble() * 1.4f) * ui };
            }
        }

        void StepFestival(double t, float fdt)
        {
            musicGlow += (musicTarget - musicGlow) * Math.Min(1, fdt * 0.8f);
            if (festival == null) return;
            if (festival == "newyear" && t > nextBurst && bursts.Count < 6)
            {
                nextBurst = t + 1400 + rng.NextDouble() * 2600;
                Burst b = new Burst(); double pick = rng.NextDouble();
                b.Type = pick < 0.4 ? 0 : pick < 0.75 ? 1 : 2;   // soft bloom, golden willow, far away
                b.Start = t; b.X = w * (0.12f + (float)rng.NextDouble() * 0.76f);
                b.Y = b.Type == 2 ? horizonY - (20 + (float)rng.NextDouble() * 50) * ui : h * (0.1f + (float)rng.NextDouble() * 0.25f);
                b.R = (b.Type == 2 ? 12 + (float)rng.NextDouble() * 10 : b.Type == 1 ? 50 + (float)rng.NextDouble() * 20 : 30 + (float)rng.NextDouble() * 16) * ui;
                b.Col = b.Type == 1 ? new[] { 255, 205, 120 } : FestColours[rng.Next(FestColours.Length)];
                int n = b.Type == 1 ? 26 : b.Type == 2 ? 20 : 28; b.Ang = new float[n];
                for (int i = 0; i < n; i++) b.Ang[i] = (float)(i * Math.PI * 2 / n + (rng.NextDouble() - 0.5) * 0.08);
                bursts.Add(b);
            }
            for (int i = bursts.Count - 1; i >= 0; i--) if (t - bursts[i].Start > 4200) bursts.RemoveAt(i);
            if ((festival == "diwali" || festival == "birthday") && t > nextLantern && lanterns.Count < 40)
            {
                nextLantern = t + (festival == "diwali" ? 700 : 1300) * (0.6 + rng.NextDouble() * 0.8);
                Lantern l = new Lantern(); l.X = w * (0.04f + (float)rng.NextDouble() * 0.92f); l.Y = horizonY + (float)rng.NextDouble() * 30 * ui;
                l.VY = -(10 + (float)rng.NextDouble() * 12) * ui; l.S = (9 + (float)rng.NextDouble() * 7) * ui; l.Ph = (float)rng.NextDouble() * 6; lanterns.Add(l);
            }
            for (int i = lanterns.Count - 1; i >= 0; i--) { Lantern l = lanterns[i]; l.Y += l.VY * fdt; l.X += (float)Math.Sin(t / 1800 + l.Ph) * 6 * ui * fdt; if (l.Y < -40 * ui) lanterns.RemoveAt(i); }
            if (holiPuffs != null) foreach (float[] p in holiPuffs) { p[0] += p[3] * fdt; if (p[0] > w + p[2] * 2) p[0] = -p[2] * 2; }
            if (holiSpecks != null) foreach (float[] p in holiSpecks) { p[1] += p[2] * fdt; p[0] += (float)Math.Sin(t / 900 + p[3]) * 10 * ui * fdt; if (p[1] > h) { p[1] = -5; p[0] = (float)rng.NextDouble() * w; } }
        }

        void DrawFestival(Graphics g, double t)
        {
            if (musicGlow > 0.01f)   // music playing: a warm glow rising from below, and the scene's lights brighten a little
                GlowSprite.Draw(g, w * 0.5f, h * 1.05f, w * 0.75f, 255, 170, 110, 0.16f * musicGlow * (0.85f + 0.15f * (float)Math.Sin(t / 1300)));
            if (festival == null) return;
            if (diyas != null)
                foreach (float[] dy in diyas)
                {
                    float x = dy[0] + (float)Math.Sin(t / 2600 + dy[2]) * dy[3], y = dy[1] + (float)Math.Sin(t / 900 + dy[2]) * 1.5f * ui;
                    float s = (3.5f + (y - horizonY) / (h - horizonY + 1) * 6) * ui, fl = 0.75f + 0.25f * (float)Math.Sin(t / 120 + dy[2] * 5);
                    for (int j = 0; j < 4; j++) GlowSprite.Draw(g, x + (float)Math.Sin(t / 400 + j) * ui, y + s * (1.2f + j * 0.9f), s * (0.9f - j * 0.12f), 255, 170, 80, (0.3f - j * 0.06f) * fl);   // reflection
                    GlowSprite.Draw(g, x, y - s * 0.9f, s * 3, 255, 170, 80, 0.45f * fl);
                    brush.Color = Color.FromArgb(255, 120, 56, 36); g.FillPie(brush, x - s * 1.4f, y - s * 0.9f, s * 2.8f, s * 1.6f, 0, 180);
                    GlowSprite.Draw(g, x, y - s * 0.95f, s * 0.8f, 255, 236, 170, fl);
                }
            Region old = g.Clip; if (skyRegion != null) g.SetClip(skyRegion, CombineMode.Intersect);
            if (holiPuffs != null) foreach (float[] p in holiPuffs) { int[] c = HoliColours[(int)p[4]]; GlowSprite.Draw(g, p[0], p[1], p[2] * 1.6f, c[0], c[1], c[2], 0.11f); }
            foreach (Lantern l in lanterns)
            {
                float fade = Math.Min(1, (horizonY - l.Y + 30 * ui) / (40 * ui)), fl = 0.85f + 0.15f * (float)Math.Sin(t / 160 + l.Ph * 4);
                GlowSprite.Draw(g, l.X, l.Y + l.S * 0.4f, l.S * 3.2f, 255, 150, 70, 0.45f * fade * fl);
                brush.Color = Color.FromArgb(Alpha.Clamp(235 * fade), 255, 196, 120);
                g.FillPolygon(brush, new PointF[] { new PointF(l.X - l.S * 0.7f, l.Y - l.S), new PointF(l.X + l.S * 0.7f, l.Y - l.S), new PointF(l.X + l.S * 0.5f, l.Y + l.S), new PointF(l.X - l.S * 0.5f, l.Y + l.S) });
            }
            if (festival == "birthday")   // a small heart of stars, on the side away from the moon
            {
                float hxc = d.Moons.Count > 0 && M(d.Moons[0][0], 0).X > w / 2 ? w * 0.28f : w * 0.72f, hyc = h * 0.17f, k = 4.2f * ui;
                for (int i = 0; i < 24; i++)
                {
                    double a = i / 24.0 * Math.PI * 2; float x = (float)(16 * Math.Pow(Math.Sin(a), 3)), y = -(float)(13 * Math.Cos(a) - 5 * Math.Cos(2 * a) - 2 * Math.Cos(3 * a) - Math.Cos(4 * a));
                    float tw = 0.75f + 0.25f * (float)Math.Sin(t / 700 + i);
                    GlowSprite.Draw(g, hxc + x * k, hyc + y * k, 10 * ui, 255, 190, 215, 0.55f * tw);
                    GlowSprite.Draw(g, hxc + x * k, hyc + y * k, 3.2f * ui, 255, 250, 252, tw);
                }
            }
            foreach (Burst b in bursts)
            {
                float age = (float)((t - b.Start) / 1000.0);
                float life = b.Type == 1 ? 3.6f : b.Type == 2 ? 1.8f : 2.6f; if (age > life) continue;
                float fade = age < 0.15f ? age / 0.15f : 1 - (age - 0.15f) / (life - 0.15f);
                if (age < 0.5f) GlowSprite.Draw(g, b.X, b.Y, b.R * 0.9f, b.Col[0], b.Col[1], b.Col[2], 0.5f * (1 - age / 0.5f));   // the first flash
                foreach (float a in b.Ang)
                    for (int tr = 0; tr < (b.Type == 1 ? 7 : 4); tr++)   // each spark with a short fading trail
                    {
                        float ta = Math.Max(0, age - tr * (b.Type == 1 ? 0.12f : 0.07f));
                        float rr = b.R * (1 - (float)Math.Exp(-ta * (b.Type == 1 ? 2.2f : 2.6f)));
                        float x = b.X + (float)Math.Cos(a) * rr, y = b.Y + (float)Math.Sin(a) * rr * (b.Type == 1 ? 0.8f : 1) + (b.Type == 1 ? 0.5f * b.R * 0.9f * ta * ta : 6 * ui * ta * ta);
                        float al = fade * (1 - tr * (b.Type == 1 ? 0.18f : 0.3f));
                        float rad = (b.Type == 2 ? 4f : 7f) * ui * (tr == 0 ? 1.3f : 0.85f);
                        GlowSprite.Draw(g, x, y, rad, b.Col[0], b.Col[1], b.Col[2], Math.Min(1, al * 1.1f));
                        if (tr == 0) { GlowSprite.Draw(g, x, y, rad * 2.2f, b.Col[0], b.Col[1], b.Col[2], al * 0.25f); GlowSprite.Draw(g, x, y, rad * 0.4f, 255, 252, 245, Math.Min(1, al * 1.2f)); }
                    }
            }
            g.Clip = old;
            if (holiSpecks != null) foreach (float[] p in holiSpecks) { int[] c = HoliColours[(int)p[4]]; brush.Color = Color.FromArgb(220, c[0], c[1], c[2]); g.FillEllipse(brush, p[0] - p[5], p[1] - p[5], p[5] * 2, p[5] * 2); }
        }
        // the real weather and the real moon
        string realWeather = "clear"; float[] rwx, rwy, rwl, rwsp, rwph, rwr; int rwKind; double nextFlash, flashAt = -1e9;
        float[] cloudX, cloudY, cloudS, cloudV; Bitmap cloudBmp; Color moonShadow = Color.Empty;
        double moonPhase = -1;
        public bool OwnWeather { get { return d.Rain != "none" || d.Snow != "none" || d.Key == "space" || d.LifeUnder; } }
        public void SetMoonPhase(double p) { moonPhase = p; }
        public void SetWeather(string kind)
        {
            if (kind == null || OwnWeather) kind = "clear";
            if (kind == realWeather) return;
            realWeather = kind;
            rwKind = kind == "rain" || kind == "storm" ? 1 : kind == "snow" ? 2 : 0;
            if (rwKind > 0)
            {
                int n = (int)(w * h / (rwKind == 2 ? 6000f : kind == "storm" ? 5000f : 8500f));
                rwx = new float[n]; rwy = new float[n]; rwl = new float[n]; rwsp = new float[n]; rwph = new float[n]; rwr = new float[n];
                for (int i = 0; i < n; i++)
                {
                    rwx[i] = (float)rng.NextDouble() * w; rwy[i] = (float)rng.NextDouble() * h; rwl[i] = (10 + (float)rng.NextDouble() * 10) * ui;
                    rwsp[i] = rwKind == 1 ? (650 + (float)rng.NextDouble() * 350) * ui : (24 + (float)rng.NextDouble() * 36) * ui; rwph[i] = (float)rng.NextDouble() * 6; rwr[i] = (1 + (float)rng.NextDouble() * 1.6f) * ui;
                }
            }
            else rwx = null;
            if (kind == "cloudy" || kind == "rain" || kind == "storm" || kind == "fog")
            {
                if (cloudBmp == null) cloudBmp = MakeCloud();
                int n = kind == "fog" ? 0 : 7;
                cloudX = new float[n]; cloudY = new float[n]; cloudS = new float[n]; cloudV = new float[n];
                for (int i = 0; i < n; i++) { cloudX[i] = (float)rng.NextDouble() * w; cloudY[i] = h * (0.02f + (float)rng.NextDouble() * 0.3f); cloudS[i] = (1.1f + (float)rng.NextDouble() * 1.1f) * ui; cloudV[i] = (5 + (float)rng.NextDouble() * 7) * ui; }
            }
            else cloudX = null;
            nextFlash = 0;
        }
        // one soft cloud shape, made once and reused
        static Bitmap MakeCloud()
        {
            // many faint, overlapping puffs along a gently curved band, so they merge into one smooth, soft cloud bank
            Bitmap b = new Bitmap(640, 180, PixelFormat.Format32bppArgb);
            Random r = new Random(7);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < 140; i++)
                {
                    double u = r.NextDouble();
                    float x = 70 + (float)u * 500, thick = (float)Math.Sin(u * Math.PI);          // thicker in the middle, thin at the ends
                    float y = 95 - thick * 22 + (float)(r.NextDouble() - 0.5) * 34 * thick;
                    float rr = (26 + (float)r.NextDouble() * 34) * (0.5f + 0.5f * thick);
                    GlowSprite.Draw(g, x, y, rr * 1.9f, 92, 80, 112, 0.16f);
                }
            }
            return b;
        }                  // lines for the moon card (phase, light, tonight's sky)
        public double SurpriseEveryMs = 0;               // 0 = rare (15 to 40 minutes)
        float hx, hy; bool hoverOn;
        bool pDown, showerFired; float pdx, pdy; double pDownAt, ignoreClicksUntil = -1e9;
        double lastClickT = -1e9; float lastClickX, lastClickY;
        double moonCardAt = -1e9; string[] moonCard; double wishAt = -1e9; float wishX, wishY; double catReactAt = -1e9;
        double weatherUntil = -1e9, weatherFrom; int tempKind; float[] tx, ty, tl, tsp, tph, tr;
        double nextSurprise, nextHoverRipple;
        readonly List<PointF> drawing = new List<PointF>();
        readonly List<KeyValuePair<double, PointF[]>> drawings = new List<KeyValuePair<double, PointF[]>>();
        Font cardFont, cardFontBig;
        float parX, parY, parTX, parTY;
        bool[] lightOff, windowOff;
        class Sprite { public Bitmap Bmp; public float Y, Speed, Gap, X, CropY; public bool Active; public double Next; }
        Sprite train;
        float[] gx, gy, gv, gph; RectangleF[] gBox;
        double nextSat, nextDebris, nextAstro, nextPlane;
        readonly List<float[]> movers = new List<float[]>();   // kind, x, y, vx, vy, start, life, spin
        public float ClockTop { get { return d != null ? d.ClockTop : -1; } }

        PointF M(float x, float y) { return new PointF(ox + x * sc, oy + y * sc); }

        public void Build(SceneData data, int width, int height, float uiScale)
        {
            d = data; w = width; h = height; ui = uiScale;
            sc = Math.Max(w / (float)d.Background.Width, h / (float)d.Background.Height) * 1.02f;
            ox = (w - d.Background.Width * sc) / 2; oy = (h - d.Background.Height * sc) / 2;
            bg = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bg))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(d.Background, ox, oy, d.Background.Width * sc, d.Background.Height * sc);
            }
            if (d.DawnFile != null && !d.NoDawn)
            {
                string df = Path.Combine(d.Dir, d.DawnFile);
                if (File.Exists(df))
                    using (Image di = Image.FromFile(df))
                    {
                        bgDawn = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(bgDawn)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(di, ox, oy, d.Background.Width * sc, d.Background.Height * sc); }
                    }
            }
            d.Background.Dispose(); d.Background = null;   // only the screen-sized copy is needed from here on
            foreach (string[] L in d.Layers) AddDrift(L[1], F(L[2]), F(L[3]), false);
            foreach (string[] L in d.Reflects) AddDrift(L[1], F(L[2]), F(L[3]), true);
            foreach (string[] L in d.Fades)
            {
                Fade fd = new Fade(); fd.AfterFront = L[0] == "fadeafter"; fd.Period = F(L[3]);
                int top = int.MaxValue; List<Bitmap> raw = new List<Bitmap>(); List<Rectangle> crops = new List<Rectangle>();
                foreach (string file in L[1].Split(','))
                {
                    Rectangle crop; Bitmap b = LoadCropped(Path.Combine(d.Dir, file), out crop);
                    if (b == null) continue;
                    raw.Add(b); crops.Add(crop); top = Math.Min(top, crop.Y);
                }
                if (raw.Count == 0) continue;
                int bottom = 0; foreach (Rectangle c in crops) bottom = Math.Max(bottom, c.Bottom);
                int fw = (int)Math.Ceiling(raw[0].Width * sc), fh = (int)Math.Ceiling((bottom - top) * sc);
                for (int i = 0; i < raw.Count; i++)
                {
                    Bitmap fb = new Bitmap(fw, fh, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(fb))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(raw[i], 0, (crops[i].Y - top) * sc, raw[i].Width * sc, raw[i].Height * sc);
                    }
                    fd.Frames.Add(fb); raw[i].Dispose();
                }
                fd.Mix = new Bitmap(fw, fh, PixelFormat.Format32bppPArgb);
                fd.Y = oy + (F(L[2]) + top) * sc;
                fades.Add(fd);
            }
            if (d.FrontFile != null)
            {
                Rectangle crop;
                using (Bitmap raw = LoadCropped(Path.Combine(d.Dir, d.FrontFile), out crop))
                    if (raw != null)
                    {
                        front = Scale(raw, raw.Width * sc, raw.Height * sc);
                        frontAt = Point.Round(M(crop.X, crop.Y));
                        if (d.DawnFront != null)
                        {
                            Rectangle c2;
                            using (Bitmap rd = LoadCropped(Path.Combine(d.Dir, d.DawnFront), out c2))
                                if (rd != null && c2.Y == crop.Y && rd.Height == raw.Height) frontDawn = Scale(rd, rd.Width * sc, rd.Height * sc);
                        }
                    }
            }
            skyClip = new GraphicsPath();
            List<PointF> pts = new List<PointF>();
            pts.Add(new PointF(0, 0));
            foreach (PointF p in d.Skyline) { PointF q = M(p.X, p.Y); pts.Add(new PointF(Math.Max(0, Math.Min(w, q.X)), Math.Max(0, Math.Min(h, q.Y)))); }
            if (pts.Count < 2) pts.Add(new PointF(w, h * 0.5f));
            pts.Add(new PointF(w, pts[pts.Count - 1].Y));
            pts.Add(new PointF(w, 0));
            skyClip.AddPolygon(pts.ToArray());
            skyRegion = new Region(skyClip);

            foreach (float[] s in d.Stars)
            {
                PointF p = M(s[0], s[1]);
                twinkles.Add(new float[] { p.X, p.Y, s[2] * ui, (float)(rng.NextDouble() * 6.28), 0.6f + (float)rng.NextDouble() * 1.2f });
            }
            phase = new float[512];
            for (int i = 0; i < phase.Length; i++) phase[i] = (float)(rng.NextDouble() * Math.PI * 2);

            // rain or snow
            float area = w * h;
            int n = 0;
            if (d.Rain != "none") { dropKind = 1; n = (int)(area / (d.Rain == "heavy" ? 6000f : 12000f)); }
            else if (d.Snow != "none") { dropKind = 2; n = (int)(area / (d.Snow == "heavy" ? 3500f : 6000f)); }
            dx = new float[n]; dy = new float[n]; dlen = new float[n]; dspeed = new float[n]; dph = new float[n]; dr = new float[n];
            for (int i = 0; i < n; i++)
            {
                dx[i] = (float)rng.NextDouble() * w; dy[i] = (float)rng.NextDouble() * h;
                dlen[i] = (10 + (float)rng.NextDouble() * 10) * ui;
                dspeed[i] = dropKind == 1 ? (650 + (float)rng.NextDouble() * 350) * ui : (22 + (float)rng.NextDouble() * 40) * ui;
                dph[i] = (float)rng.NextDouble() * 6; dr[i] = (1 + (float)rng.NextDouble() * 1.8f) * ui;
            }

            toggleOn = new bool[d.Toggles.Count]; toggleNext = new double[d.Toggles.Count];
            for (int i = 0; i < d.Toggles.Count; i++) { toggleOn[i] = d.Toggles[i][10] > 0; toggleNext[i] = 2000 + rng.NextDouble() * 18000; }

            int nb = 0; foreach (float[] b in d.Birds) nb += (int)b[4];
            bx = new float[nb]; by = new float[nb]; bv = new float[nb]; bph = new float[nb]; birdBox = new RectangleF[nb];
            int k = 0;
            foreach (float[] b in d.Birds)
            {
                PointF a = M(b[0], b[1]), c = M(b[2], b[3]);
                for (int i = 0; i < (int)b[4]; i++, k++)
                {
                    birdBox[k] = RectangleF.FromLTRB(a.X, a.Y, c.X, c.Y);
                    bx[k] = a.X + (float)rng.NextDouble() * (c.X - a.X); by[k] = a.Y + (float)rng.NextDouble() * (c.Y - a.Y);
                    bv[k] = (18 + (float)rng.NextDouble() * 22) * ui * (rng.NextDouble() < 0.5 ? -1 : 1); bph[k] = (float)rng.NextDouble() * 6;
                }
            }
            int nf = 0; foreach (float[] f in d.Fireflies) nf += (int)f[4];
            fx = new float[nf]; fy = new float[nf]; fvx = new float[nf]; fvy = new float[nf]; fph = new float[nf]; ffBox = new RectangleF[nf];
            k = 0;
            foreach (float[] f in d.Fireflies)
            {
                PointF a = M(f[0], f[1]), c = M(f[2], f[3]);
                for (int i = 0; i < (int)f[4]; i++, k++)
                {
                    ffBox[k] = RectangleF.FromLTRB(a.X, a.Y, c.X, c.Y);
                    fx[k] = a.X + (float)rng.NextDouble() * (c.X - a.X); fy[k] = a.Y + (float)rng.NextDouble() * (c.Y - a.Y);
                    fph[k] = (float)rng.NextDouble() * 6;
                }
            }
            nextShot = 4000 + rng.Next(6000);
            lightOff = new bool[d.Lights.Count]; windowOff = new bool[d.Windows.Count];
            for (int i = 0; i < d.Pulses.Count; i++) { float[] pl = d.Pulses[i]; float[] n2 = new float[7]; Array.Copy(pl, n2, Math.Min(pl.Length, 5)); float a = pl.Length > 5 ? pl[5] : 80; n2[5] = a; n2[6] = a; d.Pulses[i] = n2; }   // x, y, r, g, b, current strength, original strength
            foreach (string[] sp in d.Sprites)
            {
                Rectangle crop;
                using (Bitmap raw = LoadCropped(Path.Combine(d.Dir, sp[2]), out crop))
                    if (raw != null)
                    {
                        train = new Sprite();
                        train.Bmp = Scale(raw, raw.Width * sc, raw.Height * sc);
                        train.Y = oy + (F(sp[3]) + crop.Y) * sc; train.CropY = crop.Y; train.Speed = F(sp[4]); train.Gap = F(sp[5]);
                        train.Next = 1500;
                    }
            }
            int ng = 0; foreach (float[] gd in d.GlassDrops) ng += (int)gd[4];
            gx = new float[ng]; gy = new float[ng]; gv = new float[ng]; gph = new float[ng]; gBox = new RectangleF[ng];
            k = 0;
            foreach (float[] gd in d.GlassDrops)
            {
                PointF a = M(gd[0], gd[1]), c = M(gd[2], gd[3]);
                for (int i = 0; i < (int)gd[4]; i++, k++)
                {
                    gBox[k] = RectangleF.FromLTRB(a.X, a.Y, c.X, c.Y);
                    gx[k] = a.X + (float)rng.NextDouble() * (c.X - a.X); gy[k] = a.Y + (float)rng.NextDouble() * (c.Y - a.Y);
                    gv[k] = 0; gph[k] = (float)rng.NextDouble() * 6;
                }
            }
            nextSat = 3000 + rng.NextDouble() * 4000; nextDebris = 12000 + rng.NextDouble() * 10000; nextAstro = 20000 + rng.NextDouble() * 20000;
            nextPlane = 5000 + rng.NextDouble() * 8000;
            for (int fi = 0; fi < d.Falls.Count; fi++)
            {
                int nStreaks = Math.Max(6, (int)(d.Falls[fi][4] * 0.35f));
                for (int i = 0; i < nStreaks; i++) { FallStreak st = new FallStreak(); st.Fall = fi; st.T = (float)rng.NextDouble(); st.Off = (float)(rng.NextDouble() - 0.5); st.Speed = 0.45f + (float)rng.NextDouble() * 0.35f; st.Len = 0.12f + (float)rng.NextDouble() * 0.18f; fallStreaks.Add(st); }
            }
            foreach (string[] sw in d.Sways)
            {
                Rectangle crop;
                using (Bitmap raw = LoadCropped(Path.Combine(d.Dir, sw[1]), out crop))
                    if (raw != null) { Sway sy = new Sway(); sy.Bmp = Scale(raw, raw.Width * sc, raw.Height * sc); sy.Y = oy + (F(sw[2]) + crop.Y) * sc; sy.Amp = F(sw[3]); sways.Add(sy); }
            }
            SetDawn(0, true);
            nextSurprise = SurpriseEveryMs > 0 ? SurpriseEveryMs : (3 + rng.NextDouble() * 6) * 60000;
        }

        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }

        static Bitmap Scale(Bitmap src, float nw, float nh)
        {
            Bitmap b = new Bitmap(Math.Max(1, (int)Math.Ceiling(nw)), Math.Max(1, (int)Math.Ceiling(nh)), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            return b;
        }

        // loads a transparent layer and trims empty rows above and below, so drawing it each frame is cheaper
        static Bitmap LoadCropped(string path, out Rectangle crop)
        {
            crop = Rectangle.Empty;
            if (!File.Exists(path)) return null;
            using (Image im = Image.FromFile(path))
            using (Bitmap full = new Bitmap(im))
            {
                int top = 0, bottom = full.Height - 1;
                BitmapData bd = full.LockBits(new Rectangle(0, 0, full.Width, full.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int[] px = new int[full.Width * full.Height];
                Marshal.Copy(bd.Scan0, px, 0, px.Length);
                full.UnlockBits(bd);
                Func<int, bool> rowEmpty = delegate(int y) { for (int x = 0; x < full.Width; x += 2) if (((px[y * full.Width + x] >> 24) & 255) > 4) return false; return true; };
                while (top < bottom && rowEmpty(top)) top++;
                while (bottom > top && rowEmpty(bottom)) bottom--;
                crop = new Rectangle(0, top, full.Width, bottom - top + 1);
                return full.Clone(crop, PixelFormat.Format32bppArgb);
            }
        }

        void AddDrift(string file, float y, float speed, bool afterFront)
        {
            Rectangle crop;
            using (Bitmap raw = LoadCropped(Path.Combine(d.Dir, file), out crop))
            {
                if (raw == null) return;
                Drift dr = new Drift();
                dr.Bmp = Scale(raw, raw.Width * sc, raw.Height * sc);
                dr.Y = oy + (y + crop.Y) * sc; dr.Speed = speed; dr.AfterFront = afterFront;
                drifts.Add(dr);
            }
        }

        bool InSky(float x, float y) { return skyRegion != null && skyRegion.IsVisible(x, y); }

        // hover: screen position and whether hover effects are active right now (Ctrl held, or always on)
        public void SetHover(float x, float y, bool active)
        {
            hx = x - parX; hy = y - parY; hoverOn = active;
            if (pDown && CDraw && InSky(hx, hy))   // drawing your own constellation by dragging across stars
            {
                foreach (float[] s in twinkles)
                {
                    if (Math.Abs(s[0] - hx) > 22 * ui || Math.Abs(s[1] - hy) > 22 * ui) continue;
                    PointF p = new PointF(s[0], s[1]);
                    if (drawing.Count == 0 || (drawing[drawing.Count - 1] != p && !drawing.Contains(p))) drawing.Add(p);
                    break;
                }
            }
        }
        public void PointerDown(float x, float y, double t) { pDown = true; showerFired = false; pdx = x - parX; pdy = y - parY; pDownAt = t; drawing.Clear(); }
        public void PointerUp(float x, float y, double t)
        {
            if (showerFired || drawing.Count >= 2) ignoreClicksUntil = t + 400;   // letting go after a hold or a drag isn't a click
            pDown = false; showerFired = false;
            if (drawing.Count >= 2) { drawings.Add(new KeyValuePair<double, PointF[]>(t, drawing.ToArray())); if (drawings.Count > 12) drawings.RemoveAt(0); }
            drawing.Clear();
        }

        bool OverWater(float x, float y)
        {
            foreach (float[] wr in d.Water) { PointF a = M(wr[0], wr[1]), c = M(wr[2], wr[3]); if (x > a.X && x < c.X && y > a.Y && y < c.Y) return true; }
            return false;
        }

        // rare surprises: kind 10 comet, 11 owl, 12 fox, 13 whale
        public void SpawnSurprise(double t)
        {
            if (d.Surprises.Count == 0) return;
            string[] sp = d.Surprises[rng.Next(d.Surprises.Count)];
            bool ltr = rng.NextDouble() < 0.5;
            if (sp[1] == "comet" && dawnAmt < 0.5f && !d.NoShoot)
                movers.Add(new float[] { 10, ltr ? -60 : w + 60, h * (0.06f + (float)rng.NextDouble() * 0.16f), (ltr ? 1 : -1) * w / 45f, h * 0.002f * (float)(rng.NextDouble() - 0.3), (float)t, 60000, 0 });
            else if (sp[1] == "owl" && sp.Length >= 6)
            {
                PointF a = M(F(sp[2]), F(sp[3])), c = M(F(sp[4]), F(sp[5]));
                movers.Add(new float[] { 11, ltr ? a.X - 60 : c.X + 60, a.Y + (float)rng.NextDouble() * (c.Y - a.Y), (ltr ? 1 : -1) * (c.X - a.X) / 7f, (float)(rng.NextDouble() - 0.5) * 10 * ui, (float)t, 12000, (float)rng.NextDouble() * 6 });
            }
            else if (sp[1] == "fox" && sp.Length >= 5)
            {
                PointF a = M(F(sp[2]), F(sp[4])), c = M(F(sp[3]), F(sp[4]));
                movers.Add(new float[] { 12, ltr ? a.X - 80 : c.X + 80, a.Y, (ltr ? 1 : -1) * (c.X - a.X) / 18f, 0, (float)t, 25000, 0 });
            }
            else if (sp[1] == "whale" && sp.Length >= 4)
            {
                PointF a = M(F(sp[2]), F(sp[3]));
                movers.Add(new float[] { 13, a.X, a.Y, 0, 0, (float)t, 7000, ltr ? 1 : -1 });
            }
        }

        void StartWeatherBurst(double t)
        {
            bool snowy = d.Snow != "none" || d.Key == "snowcabin" || d.Key == "village" || d.Key == "room-snow";
            tempKind = snowy ? 2 : 1;
            int n = (int)(w * h / (snowy ? 5000f : 7000f));
            tx = new float[n]; ty = new float[n]; tl = new float[n]; tsp = new float[n]; tph = new float[n]; tr = new float[n];
            for (int i = 0; i < n; i++)
            {
                tx[i] = (float)rng.NextDouble() * w; ty[i] = (float)rng.NextDouble() * h; tl[i] = (10 + (float)rng.NextDouble() * 10) * ui;
                tsp[i] = tempKind == 1 ? (650 + (float)rng.NextDouble() * 350) * ui : (26 + (float)rng.NextDouble() * 40) * ui; tph[i] = (float)rng.NextDouble() * 6; tr[i] = (1 + (float)rng.NextDouble() * 1.8f) * ui;
            }
            weatherFrom = t; weatherUntil = t + 60000;
        }

        void ShowerAt(float x, float y, double t)
        {
            for (int i = 0; i < 16; i++)
            {
                double a = rng.NextDouble() * Math.PI * 2; float sp = (220 + (float)rng.NextDouble() * 200) * ui;
                streaks.Add(new float[] { x + (float)Math.Cos(a) * 20 * ui, y + (float)Math.Sin(a) * 20 * ui, (float)Math.Cos(a) * sp, (float)Math.Sin(a) * sp, (float)(t + rng.NextDouble() * 2500), 700 + (float)rng.NextDouble() * 400, 0.3f, 1.4f });
            }
        }

        // mouse position as -1..1 across the screen; the scene shifts a little the other way
        public void SetPointer(float nx, float ny) { if (Parallax) { parTX = -nx * 12 * ui; parTY = -ny * 7 * ui; } else { parTX = parTY = 0; } }

        // a click at screen coordinates; returns true when it did something
        public bool Click(float x, float y, double t)
        {
            if ((pDown && (showerFired || drawing.Count >= 2)) || t < ignoreClicksUntil) { ignoreClicksUntil = -1e9; return true; }
            x -= parX; y -= parY;
            // double-click the sky: a minute of rain (or snow in snowy scenes)
            bool dbl = t - lastClickT < 450 && Math.Abs(x - lastClickX) < 24 * ui && Math.Abs(y - lastClickY) < 24 * ui;
            lastClickT = dbl ? -1e9 : t; lastClickX = x; lastClickY = y;
            if (dbl && CWeather && InSky(x, y)) { StartWeatherBurst(t); return true; }
            // catch a shooting star
            if (CCatch)
                for (int i = streaks.Count - 1; i >= 0; i--)
                {
                    float[] s = streaks[i]; if (t < s[4] || t > s[4] + s[5]) continue;
                    float k = (float)((t - s[4]) / s[5]); float hxs = s[0] + s[2] * k, hys = s[1] + s[3] * k;
                    if (Math.Abs(hxs - x) < 40 * ui && Math.Abs(hys - y) < 40 * ui)
                    {
                        streaks.RemoveAt(i);
                        for (int j = 0; j < 18; j++) { Part q = new Part(); q.Kind = 4; q.X = hxs; q.Y = hys; double a = rng.NextDouble() * 6.283; float sp = (30 + (float)rng.NextDouble() * 90) * ui; q.VX = (float)Math.Cos(a) * sp; q.VY = (float)Math.Sin(a) * sp; q.Size = (1.5f + (float)rng.NextDouble() * 2) * ui; q.Life = 1.2f + (float)rng.NextDouble() * 0.8f; parts.Add(q); }
                        wishAt = t; wishX = hxs; wishY = hys; return true;
                    }
                }
            // the moon: phase and tonight's sky
            if (CMoon)
                foreach (float[] mn in d.Moons)
                {
                    PointF c = M(mn[0], mn[1]); float r = mn[2] * sc;
                    if ((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y) < r * r * 2.6f) { moonCardAt = t; moonCard = MoonInfo != null ? MoonInfo() : new string[] { "The moon" }; return true; }
                }
            // the cat, and steaming mugs
            if (CCatMug)
            {
                foreach (float[] ct in d.Cats)
                {
                    PointF c = M(ct[0], ct[1]); float cs = ct[2] * sc;
                    if (Math.Abs(x - c.X) < cs * 0.5f && y > c.Y - cs * 1.45f && y < c.Y + cs * 0.1f)
                    {
                        catReactAt = t;
                        for (int j = 0; j < 4; j++) { Part q = new Part(); q.Kind = 5; q.X = c.X + (float)(rng.NextDouble() - 0.5) * cs * 0.4f; q.Y = c.Y - cs * 1.3f; q.VX = (float)(rng.NextDouble() - 0.5) * 14 * ui; q.VY = -(22 + (float)rng.NextDouble() * 16) * ui; q.Size = (5 + (float)rng.NextDouble() * 3) * ui; q.Life = 2.2f; q.Age = -j * 0.25f; parts.Add(q); }
                        return true;
                    }
                }
                foreach (float[] sm in d.Smokes)
                {
                    PointF c = M(sm[0], sm[1]);
                    if (Math.Abs(x - c.X) < 45 * sc && y > c.Y - 30 * sc && y < c.Y + 80 * sc)
                    {
                        for (int j = 0; j < 9; j++) { Part q = new Part(); q.Kind = 0; q.X = c.X + (float)(rng.NextDouble() - 0.5) * 10 * ui; q.Y = c.Y; q.VX = (float)(rng.NextDouble() - 0.5) * 24 * ui; q.VY = -(30 + (float)rng.NextDouble() * 25) * ui; q.Size = sm[2] * sc * (0.9f + (float)rng.NextDouble() * 0.6f); q.Life = 3 + (float)rng.NextDouble() * 1.5f; q.Seed = (float)rng.NextDouble() * 6; q.Age = -j * 0.08f; parts.Add(q); }
                        return true;
                    }
                }
            }
            // send the train
            if (CTrain && train != null && !train.Active && y > train.Y - 20 * ui && y < train.Y + train.Bmp.Height + 30 * ui) { train.Next = t; return true; }
            if (ClickLights)
            {
                for (int i = 0; i < d.Toggles.Count; i++)
                {
                    float[] T = d.Toggles[i]; PointF p = M(T[0], T[1]);
                    RectangleF r = new RectangleF(p.X - 6, p.Y - 6, T[2] * sc + 12, T[3] * sc + 12);
                    if (r.Contains(x, y)) { toggleOn[i] = !toggleOn[i]; toggleNext[i] = t + 30000 + rng.NextDouble() * 30000; return true; }
                }
                for (int i = 0; i < d.Windows.Count; i++)
                {
                    float[] Wn = d.Windows[i]; PointF p = M(Wn[0], Wn[1]);
                    if (new RectangleF(p.X - 10, p.Y - 10, Wn[2] * sc + 20, Wn[3] * sc + 20).Contains(x, y)) { windowOff[i] = !windowOff[i]; return true; }
                }
                for (int i = 0; i < d.Lights.Count; i++)
                {
                    PointF p = M(d.Lights[i][0], d.Lights[i][1]);
                    if (Math.Abs(p.X - x) < 18 * ui && Math.Abs(p.Y - y) < 18 * ui) { lightOff[i] = !lightOff[i]; return true; }
                }
                foreach (float[] pl in d.Pulses)
                {
                    PointF p = M(pl[0], pl[1]);
                    if (Math.Abs(p.X - x) < 60 * ui && Math.Abs(p.Y - y) < 60 * ui) { pl[5] = pl[5] > 0 ? 0 : pl[6]; return true; }
                }
            }
            if (ClickWater)
                foreach (float[] pd in d.Puddles)   // puddles ripple too
                {
                    PointF c = M(pd[0], pd[1]); float rw = pd[2] * sc;
                    if (Math.Abs(x - c.X) < rw && Math.Abs(y - c.Y) < rw * 0.3f)
                    {
                        for (int k2 = 0; k2 < 2; k2++) { Part q = new Part(); q.Kind = 2; q.X = x; q.Y = y; q.Size = rw * (0.5f + k2 * 0.3f); q.Life = 1.2f; q.Age = -k2 * 0.2f; parts.Add(q); }
                        return true;
                    }
                }
            if (ClickWater)
                foreach (float[] wr in d.Water)
                {
                    PointF a = M(wr[0], wr[1]), c = M(wr[2], wr[3]);
                    if (RectangleF.FromLTRB(a.X, a.Y, c.X, c.Y).Contains(x, y))
                    {
                        for (int k = 0; k < 3; k++) { Part q = new Part(); q.Kind = 3; q.X = x; q.Y = y; q.Size = (40 + k * 25) * ui; q.Life = 1.6f + k * 0.4f; q.Age = -k * 0.25f; parts.Add(q); }
                        return true;
                    }
                }
            if (ClickStars && !d.NoShoot && skyClip != null && skyClip.IsVisible(x, y))
            {
                float vx = -(260 + (float)rng.NextDouble() * 160) * ui, vy = (100 + (float)rng.NextDouble() * 60) * ui;
                streaks.Add(new float[] { x, y, vx, vy, (float)t, 900, 0.28f, 1.6f });
                return true;
            }
            return false;
        }

        public void Shimmer(double t) { shimmerStart = t; for (int i = 0; i < 4; i++) AddStreak(t + 500 + i * 450, false); }

        public void StartShower(double t)
        {
            if (d.NoShoot) return;
            for (int i = 0; i < 12; i++) AddStreak(t + rng.NextDouble() * 3000, true);
        }

        void AddStreak(double start, bool shower)
        {
            if (d.NoShoot) return;
            float x = w * (0.35f + (float)rng.NextDouble() * 0.6f), y = h * (0.04f + (float)rng.NextDouble() * 0.13f);
            float vx = -(240 + (float)rng.NextDouble() * 180) * ui, vy = (90 + (float)rng.NextDouble() * 70) * ui;
            if (shower) { double a = 0.3 + rng.NextDouble() * 2.5; float s = (240 + (float)rng.NextDouble() * 200) * ui; vx = (float)Math.Cos(a) * s; vy = Math.Abs((float)Math.Sin(a)) * s; }
            streaks.Add(new float[] { x, y, vx, vy, (float)start, 800 + (float)rng.NextDouble() * 400, 0.28f, 1.4f });
        }

        public void Step(double t, double dt)
        {
            float fdt = (float)dt;
            EaseDawn(fdt);
            // rain and snow
            for (int i = 0; i < dx.Length; i++)
            {
                dy[i] += dspeed[i] * fdt;
                if (dropKind == 1) dx[i] -= dspeed[i] * fdt * 0.12f;
                else dx[i] += (float)Math.Sin(t / 1200 + dph[i]) * 14 * ui * fdt;
                if (dy[i] > h) { dy[i] = -dlen[i]; dx[i] = (float)rng.NextDouble() * w * 1.1f; }
            }
            // chimney smoke and steam
            if (d.Smokes.Count > 0 && t - lastSmoke > 240)
            {
                lastSmoke = t;
                foreach (float[] s in d.Smokes)
                {
                    PointF p = M(s[0], s[1]);
                    Part q = new Part();
                    q.Kind = 0; q.X = p.X + (float)(rng.NextDouble() - 0.5) * 4 * ui; q.Y = p.Y;
                    q.VX = s[3] * ui * (0.6f + (float)rng.NextDouble() * 0.8f); q.VY = -(16 + (float)rng.NextDouble() * 12) * ui;
                    q.Size = s[2] * sc * (0.8f + (float)rng.NextDouble() * 0.4f); q.Life = 5 + (float)rng.NextDouble() * 2; q.Seed = (float)rng.NextDouble() * 6;
                    parts.Add(q);
                }
            }
            // sand blowing off dune crests
            if (d.Sands.Count > 0 && t - lastSand > 90)
            {
                lastSand = t;
                float[] s = d.Sands[rng.Next(d.Sands.Count)];
                PointF p = M(s[0], s[1]);
                for (int i = 0; i < 3; i++)
                {
                    Part q = new Part();
                    q.Kind = 1; q.X = p.X + (float)rng.NextDouble() * 30 * ui; q.Y = p.Y - (float)rng.NextDouble() * 3 * ui;
                    q.VX = (22 + (float)rng.NextDouble() * 26) * ui; q.VY = -(3 + (float)rng.NextDouble() * 8) * ui;
                    q.Size = (0.8f + (float)rng.NextDouble() * 1.2f) * ui; q.Life = 2 + (float)rng.NextDouble() * 2;
                    parts.Add(q);
                }
            }
            // raindrops landing in puddles
            if (d.Puddles.Count > 0 && t - lastPuddle > 260)
            {
                lastPuddle = t;
                float[] s = d.Puddles[rng.Next(d.Puddles.Count)];
                PointF p = M(s[0] + (float)(rng.NextDouble() - 0.5) * s[2], s[1]);
                Part q = new Part(); q.Kind = 2; q.X = p.X; q.Y = p.Y; q.Size = s[2] * sc * 0.35f; q.Life = 1.1f;
                parts.Add(q);
            }
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Part q = parts[i];
                q.Age += fdt;
                if (q.Age > q.Life) { if (q.Kind >= 6 && q.Kind <= 8) trailCount--; parts.RemoveAt(i); continue; }
                if (q.Kind == 8) q.VY += 70 * ui * fdt;
                q.X += q.VX * fdt + (q.Kind == 0 ? (float)Math.Sin(q.Age * 1.3 + q.Seed) * 5 * ui * fdt : 0);
                q.Y += q.VY * fdt;
            }
            // office windows switching on and off
            for (int i = 0; i < toggleOn.Length; i++)
                if (t > toggleNext[i]) { toggleOn[i] = !toggleOn[i]; toggleNext[i] = t + 4000 + rng.NextDouble() * 22000; }
            // birds gliding
            for (int i = 0; i < bx.Length; i++)
            {
                bx[i] += bv[i] * fdt; by[i] += (float)Math.Sin(t / 1500 + bph[i]) * 4 * ui * fdt;
                RectangleF r = birdBox[i];
                if (bx[i] < r.Left - 40 * ui) bx[i] = r.Right + 30 * ui;
                if (bx[i] > r.Right + 40 * ui) bx[i] = r.Left - 30 * ui;
            }
            // fireflies wandering
            for (int i = 0; i < fx.Length; i++)
            {
                fvx[i] += (float)(rng.NextDouble() - 0.5) * 40 * ui * fdt; fvy[i] += (float)(rng.NextDouble() - 0.5) * 40 * ui * fdt;
                float lim = (hoverOn && HFireflies ? 60 : 18) * ui;   // they speed up while drifting toward you
                fvx[i] = Math.Max(-lim, Math.Min(lim, fvx[i])); fvy[i] = Math.Max(-lim, Math.Min(lim, fvy[i]));
                fx[i] += fvx[i] * fdt; fy[i] += fvy[i] * fdt;
                RectangleF r = ffBox[i];
                if (fx[i] < r.Left || fx[i] > r.Right) fvx[i] = -fvx[i];
                if (fy[i] < r.Top || fy[i] > r.Bottom) fvy[i] = -fvy[i];
                fx[i] = Math.Max(r.Left, Math.Min(r.Right, fx[i])); fy[i] = Math.Max(r.Top, Math.Min(r.Bottom, fy[i]));
            }
            // the train crossing the viaduct
            if (train != null)
            {
                float gap = TrainGapSeconds >= 0 ? TrainGapSeconds : train.Gap;
                if (!train.Active && t >= train.Next) { train.Active = true; train.X = train.Speed < 0 ? w + 20 : -train.Bmp.Width - 20; }
                if (train.Active)
                {
                    train.X += train.Speed * sc * fdt;
                    if ((train.Speed < 0 && train.X < -train.Bmp.Width - 40) || (train.Speed > 0 && train.X > w + 40)) { train.Active = false; train.Next = t + gap * 1000; }
                    if (t - lastSmoke > 120)
                        foreach (float[] sm in d.SpriteSmoke)
                        {
                            Part q = new Part(); q.Kind = 0; q.X = train.X + sm[0] * sc; q.Y = train.Y + (sm[1] - train.CropY) * sc;
                            q.VX = (float)(rng.NextDouble() - 0.5) * 10 * ui; q.VY = -(28 + (float)rng.NextDouble() * 20) * ui;
                            q.Size = sm[2] * sc * (0.8f + (float)rng.NextDouble() * 0.5f); q.Life = 3.5f + (float)rng.NextDouble() * 1.5f; q.Seed = (float)rng.NextDouble() * 6;
                            parts.Add(q);
                        }
                }
            }
            // raindrops sliding down the window glass
            for (int i = 0; i < gx.Length; i++)
            {
                if (gv[i] <= 0 && rng.NextDouble() < 0.01) gv[i] = (20 + (float)rng.NextDouble() * 60) * ui;
                gy[i] += gv[i] * fdt; gx[i] += (float)Math.Sin(t / 300 + gph[i]) * 6 * ui * fdt;
                if (gv[i] > 0 && rng.NextDouble() < 0.02) gv[i] = 0;   // drops stop and start, like real ones
                if (gy[i] > gBox[i].Bottom) { gy[i] = gBox[i].Top + (float)rng.NextDouble() * gBox[i].Height * 0.3f; gx[i] = gBox[i].Left + (float)rng.NextDouble() * gBox[i].Width; gv[i] = 0; }
            }
            // satellites, debris and the occasional astronaut
            foreach (FallStreak st in fallStreaks) { st.T += st.Speed * fdt; if (st.T > 1 + st.Len) { st.T = -st.Len * (float)rng.NextDouble(); st.Off = (float)(rng.NextDouble() - 0.5); } }
            if (d.PlaneEvery > 0 && t > nextPlane) { nextPlane = t + d.PlaneEvery * (0.6 + rng.NextDouble() * 0.8); bool ltr = rng.NextDouble() < 0.5; movers.Add(new float[] { 3, ltr ? -20 : w + 20, h * (0.06f + (float)rng.NextDouble() * 0.16f), (ltr ? 1 : -1) * (22 + (float)rng.NextDouble() * 14) * ui, (float)(rng.NextDouble() - 0.5f) * 3 * ui, (float)t, 200000, 0 }); }
            if (d.SatelliteEvery > 0 && t > nextSat) { nextSat = t + d.SatelliteEvery * (0.6 + rng.NextDouble() * 0.8); bool ltr = rng.NextDouble() < 0.5; movers.Add(new float[] { 0, ltr ? -10 : w + 10, h * (0.05f + (float)rng.NextDouble() * 0.35f), (ltr ? 1 : -1) * (40 + (float)rng.NextDouble() * 40) * ui, (float)(rng.NextDouble() - 0.5f) * 12 * ui, (float)t, 60000, 0 }); }
            if (d.DebrisEvery > 0 && t > nextDebris) { nextDebris = t + d.DebrisEvery * (0.6 + rng.NextDouble() * 0.8); bool ltr = rng.NextDouble() < 0.5; movers.Add(new float[] { 1, ltr ? -20 : w + 20, h * (0.1f + (float)rng.NextDouble() * 0.4f), (ltr ? 1 : -1) * (25 + (float)rng.NextDouble() * 25) * ui, (float)(rng.NextDouble() - 0.5f) * 8 * ui, (float)t, 90000, (float)(rng.NextDouble() * 2 - 1) }); }
            if (d.AstronautEvery > 0 && t > nextAstro) { nextAstro = t + d.AstronautEvery * (0.7 + rng.NextDouble() * 0.6); bool ltr = rng.NextDouble() < 0.5; movers.Add(new float[] { 2, ltr ? -80 : w + 80, h * (0.2f + (float)rng.NextDouble() * 0.3f), (ltr ? 1 : -1) * (16 + (float)rng.NextDouble() * 10) * ui, (float)(rng.NextDouble() - 0.5f) * 6 * ui, (float)t, 200000, (float)(rng.NextDouble() * 0.6 - 0.3) }); }
            for (int i = movers.Count - 1; i >= 0; i--)
            {
                float[] mv = movers[i]; mv[1] += mv[3] * fdt; mv[2] += mv[4] * fdt;
                if (mv[1] < -300 || mv[1] > w + 300 || t - mv[5] > mv[6]) movers.RemoveAt(i);
            }
            parX += (parTX - parX) * Math.Min(1, fdt * 3); parY += (parTY - parY) * Math.Min(1, fdt * 3);
            StepFestival(t, fdt);
            // hold the sky for a meteor shower (when not drawing a constellation)
            if (pDown && CMeteorHold && !showerFired && t - pDownAt > 700 && drawing.Count < 2 && Math.Abs(hx - pdx) < 14 * ui && Math.Abs(hy - pdy) < 14 * ui && InSky(pdx, pdy))
            { showerFired = true; ShowerAt(pdx, pdy, t); }
            if (hoverOn)
            {
                if (HFireflies)
                    for (int i = 0; i < fx.Length; i++)
                    {
                        float ddx = hx - fx[i], ddy = hy - fy[i], dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dd < 560 * ui && dd > 8) { float pull = (1 - dd / (560 * ui)) * 150 + 30; fvx[i] += ddx / dd * pull * ui * fdt; fvy[i] += ddy / dd * pull * ui * fdt; float lim = 60 * ui; fvx[i] = Math.Max(-lim, Math.Min(lim, fvx[i])); fvy[i] = Math.Max(-lim, Math.Min(lim, fvy[i])); }
                    }
                if (HBirds)
                    for (int i = 0; i < bx.Length; i++)
                        if (Math.Abs(bx[i] - hx) < 190 * ui && Math.Abs(by[i] - hy) < 150 * ui) { bv[i] = (bx[i] >= hx ? 1 : -1) * Math.Max(Math.Abs(bv[i]), 110 * ui); by[i] -= 50 * ui * fdt; }
                if (HGrassSnow && (dropKind == 2 || (tempKind == 2 && t < weatherUntil)))
                {
                    float[] sx = dropKind == 2 ? dx : tx, sy = dropKind == 2 ? dy : ty;
                    for (int i = 0; i < sx.Length; i++)
                    {
                        float ddx = sx[i] - hx, ddy = sy[i] - hy, dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (dd < 150 * ui && dd > 1) { float f = (1 - dd / (150 * ui)) * 140 * ui * fdt; sx[i] += (-ddy / dd + ddx / dd * 0.5f) * f; sy[i] += (ddx / dd + ddy / dd * 0.5f) * f; }
                    }
                }
                if (HWater && t > nextHoverRipple && OverWater(hx, hy))
                { nextHoverRipple = t + 170; Part q = new Part(); q.Kind = 3; q.X = hx; q.Y = hy; q.Size = 26 * ui; q.Life = 1.4f; parts.Add(q); }
            }
            for (int i = 0; i < bv.Length; i++) { float norm = 30 * ui; if (Math.Abs(bv[i]) > norm) bv[i] *= (float)Math.Pow(0.6, fdt); }
            if (rwx != null)
                for (int i = 0; i < rwx.Length; i++)
                {
                    rwy[i] += rwsp[i] * fdt;
                    if (rwKind == 1) rwx[i] -= rwsp[i] * fdt * 0.12f; else rwx[i] += (float)Math.Sin(t / 1200 + rwph[i]) * 14 * ui * fdt;
                    if (rwy[i] > h) { rwy[i] = -rwl[i]; rwx[i] = (float)rng.NextDouble() * w * 1.1f; }
                    if (hoverOn && HGrassSnow && rwKind == 2) { float ddx = rwx[i] - hx, ddy = rwy[i] - hy, dd = (float)Math.Sqrt(ddx * ddx + ddy * ddy); if (dd < 150 * ui && dd > 1) { float f = (1 - dd / (150 * ui)) * 140 * ui * fdt; rwx[i] += (-ddy / dd + ddx / dd * 0.5f) * f; rwy[i] += (ddx / dd + ddy / dd * 0.5f) * f; } }
                }
            if (cloudX != null) for (int i = 0; i < cloudX.Length; i++) { cloudX[i] += cloudV[i] * fdt; if (cloudX[i] > w + 300 * ui) cloudX[i] = -cloudBmp.Width * cloudS[i]; }
            if (realWeather == "storm" && t > nextFlash) { if (nextFlash > 0) flashAt = t; nextFlash = t + 8000 + rng.NextDouble() * 14000; }
            if (t < weatherUntil && tx != null)
                for (int i = 0; i < tx.Length; i++)
                {
                    ty[i] += tsp[i] * fdt;
                    if (tempKind == 1) tx[i] -= tsp[i] * fdt * 0.12f; else tx[i] += (float)Math.Sin(t / 1200 + tph[i]) * 14 * ui * fdt;
                    if (ty[i] > h) { ty[i] = -tl[i]; tx[i] = (float)rng.NextDouble() * w * 1.1f; }
                }
            if (Surprises && d.Surprises.Count > 0 && t > nextSurprise) { SpawnSurprise(t); nextSurprise = t + (SurpriseEveryMs > 0 ? SurpriseEveryMs : (15 + rng.NextDouble() * 25) * 60000); }
            // shooting stars
            if (AllowShootingStars && !d.NoShoot && dawnAmt < 0.4f && t > nextShot) { AddStreak(t, false); nextShot = t + (d.ShotEvery > 0 ? d.ShotEvery * (0.6 + rng.NextDouble() * 0.8) : 7000 + rng.Next(9000)); }
            if (TrailStyle != "none")
            {
                float life = TrailLength == "brief" ? 1f : TrailLength == "long" ? 6f : 3f;
                float rate = TrailStyle == "ribbon" ? 150 : TrailStyle == "sparks" ? 60 : 60;
                foreach (float[] st in streaks)
                {
                    if (t < st[4] || t > st[4] + st[5] || trailCount > 1200) continue;
                    float k = (float)((t - st[4]) / st[5]), hx2 = st[0] + st[2] * k, hy2 = st[1] + st[3] * k;
                    float want = rate * fdt; int n = (int)want + (rng.NextDouble() < want - (int)want ? 1 : 0);
                    for (int j = 0; j < n; j++)
                    {
                        Part q = new Part(); q.Col = rng.Next(TrailColours.Length); q.Seed = (float)rng.NextDouble() * 6;
                        float back = (float)rng.NextDouble() * 0.03f;   // spread the new grains a little behind the head
                        q.X = hx2 - st[2] * back + (float)(rng.NextDouble() - 0.5) * 6 * ui; q.Y = hy2 - st[3] * back + (float)(rng.NextDouble() - 0.5) * 6 * ui;
                        if (TrailStyle == "dust") { q.Kind = 6; q.VX = st[2] * 0.015f; q.VY = (5 + (float)rng.NextDouble() * 10) * ui * (life / 3f); q.Size = (1.2f + (float)rng.NextDouble() * 1.4f) * ui; q.Life = life * (0.6f + (float)rng.NextDouble() * 0.4f); }
                        else if (TrailStyle == "ribbon") { q.Kind = 7; q.Size = (6 + (float)rng.NextDouble() * 3) * ui; q.Life = life * 0.7f; }
                        else { q.Kind = 8; double a = rng.NextDouble() * 6.283; float sp = (30 + (float)rng.NextDouble() * 60) * ui; q.VX = (float)Math.Cos(a) * sp; q.VY = (float)Math.Sin(a) * sp - 15 * ui; q.Size = (1.1f + (float)rng.NextDouble() * 0.9f) * ui; q.Life = Math.Min(life, 0.7f + (float)rng.NextDouble() * life * 0.5f); }
                        parts.Add(q); trailCount++;
                    }
                }
            }
            for (int i = streaks.Count - 1; i >= 0; i--) if (t > streaks[i][4] + streaks[i][5]) streaks.RemoveAt(i);
        }

        public void Paint(Graphics g, double t)
        {
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            System.Drawing.Drawing2D.Matrix saved = g.Transform;
            g.TranslateTransform(parX, parY);
            g.DrawImageUnscaled(CurrentBg, 0, 0);
            if (d.Key != "space") DrawMoonPhase(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Region old = g.Clip;
            g.SetClip(skyRegion, CombineMode.Intersect);
            double since = t - shimmerStart;
            foreach (float[] s in twinkles)
            {
                double boost = since >= 0 && since < 7000 ? Math.Sin(Math.PI * since / 7000) * (0.5 + 0.5 * Math.Sin(t / 160 + s[3] * 3)) : 0;
                if (dawnAmt > 0.9f) break;
                if (hoverOn && HStars)
                {
                    float sd = (float)Math.Sqrt((s[0] - hx) * (s[0] - hx) + (s[1] - hy) * (s[1] - hy));
                    if (sd < 140 * ui) GlowSprite.Draw(g, s[0], s[1], s[2] * 7 + 4 * ui, 240, 232, 255, (1 - sd / (140 * ui)) * 0.9f);
                }
                float wdim = realWeather == "clear" ? 1 : realWeather == "cloudy" ? 0.6f : realWeather == "snow" ? 0.6f : 0.4f;
                int a = (int)(255 * wdim * (1 - dawnAmt) * Math.Min(1, 0.55 + 0.45 * Math.Sin(t / 1000 * s[4] + s[3]) + boost * 0.6));
                brush.Color = Color.FromArgb(Math.Max(0, Math.Min(255, a)), 236, 228, 244);
                g.FillEllipse(brush, s[0] - s[2], s[1] - s[2], s[2] * 2, s[2] * 2);
            }
            g.Clip = old;
            foreach (Drift dr in drifts) if (!dr.AfterFront) DrawDrift(g, dr, t);
            if (dawnAmt < 0.7f) foreach (Fade fd in fades) if (!fd.AfterFront) DrawFade(g, fd, t);
            g.SetClip(skyRegion, CombineMode.Intersect);
            DrawStreaks(g, t);
            g.Clip = old;
            DrawSprite(g, t);
            DrawMovers(g, t);
            if (d.LifeUnder) { DrawLife(g, t, 0); DrawDrops(g); }
            DrawFalls(g, t);
            DrawSways(g, t);
            if (front != null) g.DrawImageUnscaled(CurrentFront, frontAt.X, frontAt.Y);
            if (d.Key == "space") DrawMoonPhase(g);   // the photo moon is part of the space scene's front layer
            foreach (Drift dr in drifts) if (dr.AfterFront) DrawDrift(g, dr, t);
            if (dawnAmt < 0.7f) foreach (Fade fd in fades) if (fd.AfterFront) DrawFade(g, fd, t);
            if (d.LifeUnder) DrawLife(g, t, 1);
            else { DrawLife(g, t, 2); DrawDrops(g); }
            DrawCats(g, t);
            DrawFestival(g, t);
            DrawRealWeather(g, t);
            DrawPlay(g, t);
            g.Transform = saved;
        }

        void DrawSprite(Graphics g, double t)
        {
            if (train == null || !train.Active) return;
            g.DrawImageUnscaled(train.Bmp, (int)train.X, (int)train.Y);
            foreach (float[] wl in d.SpriteWheel)   // spokes turning as it rolls
            {
                float cx = train.X + wl[0] * sc, cy = train.Y + (wl[1] - train.CropY) * sc, r = wl[2] * sc;
                float ang = -train.X / Math.Max(1f, r);
                pen.Color = Color.FromArgb(200, 70, 56, 60); pen.Width = Math.Max(1f, 2 * sc);
                for (int k = 0; k < 3; k++) { float a = ang + k * 1.047f; g.DrawLine(pen, cx - (float)Math.Cos(a) * r * 0.85f, cy - (float)Math.Sin(a) * r * 0.85f, cx + (float)Math.Cos(a) * r * 0.85f, cy + (float)Math.Sin(a) * r * 0.85f); }
            }
            foreach (float[] gl in d.SpriteGlow)   // firebox and headlamp flicker
            {
                float cx = train.X + gl[0] * sc, cy = train.Y + (gl[1] - train.CropY) * sc, r = gl[5] * sc;
                double f = 0.7 + 0.2 * Math.Sin(t / 90 + gl[0]) + 0.1 * Math.Sin(t / 37);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(Alpha.Clamp(150 * f), (int)gl[2], (int)gl[3], (int)gl[4]);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, (int)gl[2], (int)gl[3], (int)gl[4]) };
                        g.FillPath(pb, gp);
                    }
                }
            }
        }

        // tonight's real moon phase, drawn over the painted moon
        void DrawMoonPhase(Graphics g)
        {
            if (moonPhase < 0 || dawnAmt > 0.95f || d.Moons.Count == 0) return;
            float[] mn = d.Moons[0]; PointF c = M(mn[0], mn[1]); float r = mn[2] * sc * 1.04f;
            if (moonShadow.IsEmpty)   // the sky colour in a ring just outside the moon, so the dark side melts into it
            {
                int rr = 0, gg = 0, bb = 0, n = 0;
                for (int i = 0; i < 16; i++)
                {
                    double ang = i * Math.PI / 8; float dist = r * (d.Key == "space" ? 1.6f : 2.4f);
                    int px = (int)(c.X + Math.Cos(ang) * dist), py = (int)(c.Y + Math.Sin(ang) * dist);
                    if (px < 0 || py < 0 || px >= bg.Width || py >= bg.Height) continue;
                    Color k2 = bg.GetPixel(px, py); rr += k2.R; gg += k2.G; bb += k2.B; n++;
                }
                if (n == 0) n = 1;
                moonShadow = Color.FromArgb(255, Math.Min(255, rr / n + 3), Math.Min(255, gg / n + 2), Math.Min(255, bb / n + 5));   // the faintest earthshine
            }
            int alpha = Alpha.Clamp(255 * (1 - dawnAmt));
            bool waxing = moonPhase < 0.5; double k = Math.Cos(2 * Math.PI * moonPhase);   // 1 new, 0 quarter, -1 full
            float ex = (float)Math.Abs(k) * r;
            // the dark part: the unlit half, plus (crescent) or minus (gibbous) the terminator ellipse
            using (GraphicsPath half = new GraphicsPath()) using (GraphicsPath term = new GraphicsPath())
            {
                half.AddPie(c.X - r - 0.5f, c.Y - r - 0.5f, r * 2 + 1, r * 2 + 1, waxing ? 90 : -90, 180);
                term.AddEllipse(c.X - ex, c.Y - r - 0.5f, Math.Max(0.01f, ex * 2), r * 2 + 1);
                using (Region dark = new Region(half))
                {
                    if (k > 0) dark.Union(term); else dark.Exclude(term);
                    if (d.Key != "space")   // painted moons may be crescents, so start from a full, plain moon
                        using (SolidBrush light = new SolidBrush(Color.FromArgb(alpha, 236, 230, 244))) g.FillEllipse(light, c.X - r, c.Y - r, r * 2, r * 2);
                    using (SolidBrush shade = new SolidBrush(Color.FromArgb(alpha, moonShadow))) g.FillRegion(shade, dark);
                }
            }
        }

        void DrawRealWeather(Graphics g, double t)
        {
            if (realWeather == "clear") return;
            Region old = g.Clip; if (skyRegion != null) g.SetClip(skyRegion, CombineMode.Intersect);
            if (cloudX != null)
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = realWeather == "cloudy" ? 0.8f : 0.95f; ia.SetColorMatrix(cm);
                    for (int i = 0; i < cloudX.Length; i++)
                        g.DrawImage(cloudBmp, new Rectangle((int)cloudX[i], (int)cloudY[i], (int)(cloudBmp.Width * cloudS[i]), (int)(cloudBmp.Height * cloudS[i])), 0, 0, cloudBmp.Width, cloudBmp.Height, GraphicsUnit.Pixel, ia);
                }
            if (realWeather == "storm" && t - flashAt < 900)   // silent lightning: the sky lights up softly, twice
            {
                double e = t - flashAt; float a = (float)(e < 120 ? 1 - e / 120 : e > 260 && e < 420 ? 0.6 * (1 - (e - 260) / 160) : 0);
                if (a > 0) { brush.Color = Color.FromArgb(Alpha.Clamp(70 * a), 220, 214, 255); g.FillRectangle(brush, 0, 0, w, h); }
            }
            g.Clip = old;
            if (realWeather == "fog")   // soft mist drifting low across the scene
                for (int i = 0; i < 6; i++)
                {
                    float x = (float)((t / 60.0 * (6 + i * 2) + i * 400) % (w + 800)) - 400, y = h * (0.55f + i * 0.06f);
                    GlowSprite.Draw(g, x, y, w * 0.35f, 210, 200, 225, 0.12f);
                    GlowSprite.Draw(g, x + w * 0.5f, y + 20 * ui, w * 0.3f, 210, 200, 225, 0.1f);
                }
            if (rwx != null)
            {
                if (rwKind == 1) { pen.Color = Color.FromArgb(realWeather == "storm" ? 95 : 70, 205, 198, 228); pen.Width = 1; for (int i = 0; i < rwx.Length; i++) g.DrawLine(pen, rwx[i], rwy[i], rwx[i] + rwl[i] * 0.12f, rwy[i] - rwl[i]); }
                else { brush.Color = Color.FromArgb(200, 242, 238, 250); for (int i = 0; i < rwx.Length; i++) g.FillEllipse(brush, rwx[i] - rwr[i], rwy[i] - rwr[i], rwr[i] * 2, rwr[i] * 2); }
            }
        }

        void DrawCats(Graphics g, double t)
        {
            foreach (float[] ct in d.Cats)
            {
                PointF c = M(ct[0], ct[1]); float cs = ct[2] * sc; bool hanging = ct.Length > 3 && ct[3] > 0;
                bool near = HCat && hoverOn && Math.Abs(hx - c.X) < cs * 1.6f && Math.Abs(hy - (c.Y - cs * 0.5f)) < cs * 1.4f;
                bool happy = t - catReactAt < 2500;
                double speed = happy ? 160 : near ? 260 : 1500, amp = happy ? 0.55 : near ? 0.4 : 0.16;
                float sw = (float)(Math.Sin(t / speed) * amp);
                pen.Color = Color.FromArgb(255, 10, 6, 12); pen.Width = Math.Max(2f, cs * (hanging ? 0.08f : 0.09f));
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                PointF[] tail;
                if (hanging)   // tail hanging over the porch rail, swinging
                    tail = new PointF[] { new PointF(c.X + cs * 0.3f, c.Y), new PointF(c.X + cs * (0.5f + sw * 0.4f), c.Y + cs * 0.3f), new PointF(c.X + cs * (0.45f + sw), c.Y + cs * 0.6f) };
                else           // tail lying on the deck, tip flicking up
                    tail = new PointF[] { new PointF(c.X + cs * 0.35f, c.Y - cs * 0.05f), new PointF(c.X + cs * 0.75f, c.Y - cs * (0.02f + sw * 0.15f)), new PointF(c.X + cs * 0.9f, c.Y - cs * (0.2f + sw * 0.5f)) };
                g.DrawCurve(pen, tail);
                pen.StartCap = LineCap.Flat; pen.EndCap = LineCap.Flat;
                if ((near || happy) && (t % 1400) < 220)   // an ear twitches
                {
                    brush.Color = Color.FromArgb(255, 10, 6, 12);
                    float side = ((int)(t / 1400) % 2 == 0) ? -1 : 1;
                    float ex = c.X + side * cs * 0.2f, ey = c.Y - cs * 1.2f;
                    g.FillPolygon(brush, new PointF[] { new PointF(ex - cs * 0.07f, ey), new PointF(ex + side * cs * 0.05f, ey - cs * 0.3f), new PointF(ex + cs * 0.07f, ey) });
                }
            }
        }

        void DrawPlay(Graphics g, double t)
        {
            // weather burst: a minute of rain or snow, easing in and out
            if (t < weatherUntil && tx != null)
            {
                float k = (float)Math.Min(1, Math.Min((t - weatherFrom) / 3000, (weatherUntil - t) / 4000));
                if (tempKind == 1) { pen.Color = Color.FromArgb(Alpha.Clamp(80 * k), 205, 198, 228); pen.Width = 1; for (int i = 0; i < tx.Length; i++) g.DrawLine(pen, tx[i], ty[i], tx[i] + tl[i] * 0.12f, ty[i] - tl[i]); }
                else { brush.Color = Color.FromArgb(Alpha.Clamp(210 * k), 242, 238, 250); for (int i = 0; i < tx.Length; i++) g.FillEllipse(brush, tx[i] - tr[i], ty[i] - tr[i], tr[i] * 2, tr[i] * 2); }
            }
            // constellations you drew (fade after two minutes), and the one being drawn
            for (int i = drawings.Count - 1; i >= 0; i--)
            {
                float age = (float)((t - drawings[i].Key) / 120000.0);
                if (age >= 1) { drawings.RemoveAt(i); continue; }
                DrawConstellation(g, drawings[i].Value, 1 - age * age);
            }
            if (drawing.Count > 0) DrawConstellation(g, drawing.ToArray(), 1);
            // the moon card
            if (t - moonCardAt < 7000 && moonCard != null && d.Moons.Count > 0)
            {
                float a = (float)Math.Min(1, Math.Min((t - moonCardAt) / 300, (7000 - (t - moonCardAt)) / 800));
                PointF c = M(d.Moons[0][0], d.Moons[0][1]); float r = d.Moons[0][2] * sc;
                if (cardFont == null) { cardFont = new Font("Segoe UI", 11f * ui, FontStyle.Regular, GraphicsUnit.Pixel); cardFontBig = new Font("Segoe UI Semibold", 14f * ui, FontStyle.Regular, GraphicsUnit.Pixel); }
                float lineH = 20 * ui, pad = 12 * ui, cw = 0;
                for (int i = 0; i < moonCard.Length; i++) cw = Math.Max(cw, g.MeasureString(moonCard[i], i == 0 ? cardFontBig : cardFont).Width);
                float bw = cw + pad * 2, bh = lineH * moonCard.Length + pad * 2 - 4 * ui;
                float bx0 = c.X + r * 1.6f, by0 = c.Y - bh / 2;
                if (bx0 + bw > w - 10) bx0 = c.X - r * 1.6f - bw;
                by0 = Math.Max(8, Math.Min(h - bh - 8, by0));
                using (GraphicsPath gp = RoundRect(bx0, by0, bw, bh, 12 * ui))
                {
                    brush.Color = Color.FromArgb(Alpha.Clamp(185 * a), 22, 14, 30); g.FillPath(brush, gp);
                    pen.Color = Color.FromArgb(Alpha.Clamp(90 * a), 200, 180, 230); pen.Width = 1; g.DrawPath(pen, gp);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < moonCard.Length; i++)
                {
                    brush.Color = Color.FromArgb(Alpha.Clamp((i == 0 ? 245 : 200) * a), 236, 228, 244);
                    g.DrawString(moonCard[i], i == 0 ? cardFontBig : cardFont, brush, bx0 + pad, by0 + pad - 2 * ui + i * lineH);
                }
            }
            // a caught shooting star: a little wish
            if (t - wishAt < 4000)
            {
                float a = (float)Math.Min(1, Math.Min((t - wishAt) / 300, (4000 - (t - wishAt)) / 1200));
                if (cardFontBig == null) { cardFont = new Font("Segoe UI", 11f * ui, FontStyle.Regular, GraphicsUnit.Pixel); cardFontBig = new Font("Segoe UI Semibold", 14f * ui, FontStyle.Regular, GraphicsUnit.Pixel); }
                string msg = "\u2726 make a wish";
                SizeF ms = g.MeasureString(msg, cardFontBig);
                float yy = wishY - 30 * ui - (float)((t - wishAt) / 4000) * 26 * ui;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                brush.Color = Color.FromArgb(Alpha.Clamp(240 * a), 255, 240, 210);
                g.DrawString(msg, cardFontBig, brush, Math.Max(8, Math.Min(w - ms.Width - 8, wishX - ms.Width / 2)), yy);
            }
        }

        void DrawConstellation(Graphics g, PointF[] pts, float a)
        {
            pen.Color = Color.FromArgb(Alpha.Clamp(140 * a), 210, 196, 240); pen.Width = Math.Max(1f, 1.2f * ui);
            for (int i = 1; i < pts.Length; i++) g.DrawLine(pen, pts[i - 1], pts[i]);
            foreach (PointF p in pts) GlowSprite.Draw(g, p.X, p.Y, 7 * ui, 236, 228, 255, 0.8f * a);
        }

        static GraphicsPath RoundRect(float x, float y, float w2, float h2, float r)
        {
            GraphicsPath gp = new GraphicsPath(); float dd = r * 2;
            gp.AddArc(x, y, dd, dd, 180, 90); gp.AddArc(x + w2 - dd, y, dd, dd, 270, 90);
            gp.AddArc(x + w2 - dd, y + h2 - dd, dd, dd, 0, 90); gp.AddArc(x, y + h2 - dd, dd, dd, 90, 90); gp.CloseFigure();
            return gp;
        }

        void DrawSurprise(Graphics g, float[] mv, double t)
        {
            float x = mv[1], y = mv[2], life = (float)((t - mv[5]) / mv[6]);
            float fadeIn = Math.Min(1, life * 8), fadeOut = Math.Min(1, (1 - life) * 8), a = Math.Max(0, Math.Min(fadeIn, fadeOut));
            Color sil = Color.FromArgb(Alpha.Clamp(255 * a), 16, 10, 20);
            if (mv[0] == 10)   // comet: bright head and a long soft tail pointing away from its travel
            {
                float dirx = mv[3] > 0 ? -1 : 1;
                for (int i = 14; i >= 0; i--) GlowSprite.Draw(g, x + dirx * i * 16 * ui, y - mv[4] * i * 0.4f, (10 + i * 2.2f) * ui, 200, 220, 255, a * 0.5f * (1 - i / 15f));
                GlowSprite.Draw(g, x, y, 12 * ui, 255, 255, 255, a);
            }
            else if (mv[0] == 11)   // owl: a dark silhouette gliding with slow wingbeats
            {
                float s = 22 * ui, flap = (float)Math.Sin(t / 170 + mv[7]) * s * 0.55f, dir = mv[3] > 0 ? 1 : -1;
                brush.Color = sil; pen.Color = sil; pen.Width = Math.Max(2f, s * 0.22f);
                g.FillEllipse(brush, x - s * 0.35f, y - s * 0.25f, s * 0.7f, s * 0.5f);
                g.FillEllipse(brush, x + dir * s * 0.25f - s * 0.18f, y - s * 0.4f, s * 0.36f, s * 0.36f);
                g.DrawLines(pen, new PointF[] { new PointF(x - s * 1.2f, y - flap), new PointF(x - s * 0.5f, y - s * 0.12f), new PointF(x, y) });
                g.DrawLines(pen, new PointF[] { new PointF(x, y), new PointF(x + s * 0.5f, y - s * 0.12f), new PointF(x + s * 1.2f, y - flap) });
            }
            else if (mv[0] == 12)   // fox: trotting along the ground, bushy tail, pointed ears
            {
                float s = 30 * ui * (0.6f + 0.6f * (y / h)), dir = mv[3] > 0 ? 1 : -1, step = (float)Math.Sin(t / 120) * s * 0.18f;
                brush.Color = sil; pen.Color = sil; pen.Width = Math.Max(1.5f, s * 0.08f);
                g.FillEllipse(brush, x - s * 0.6f, y - s * 0.55f, s * 1.2f, s * 0.42f);                       // body
                float hx2 = x + dir * s * 0.62f;
                g.FillEllipse(brush, hx2 - s * 0.2f, y - s * 0.75f, s * 0.4f, s * 0.32f);                     // head
                g.FillPolygon(brush, new PointF[] { new PointF(hx2 + dir * s * 0.15f, y - s * 0.65f), new PointF(hx2 + dir * s * 0.42f, y - s * 0.55f), new PointF(hx2 + dir * s * 0.12f, y - s * 0.5f) });   // snout
                g.FillPolygon(brush, new PointF[] { new PointF(hx2 - s * 0.12f, y - s * 0.7f), new PointF(hx2 - s * 0.06f, y - s * 0.98f), new PointF(hx2 + s * 0.02f, y - s * 0.72f) });                     // ear
                g.FillEllipse(brush, x - dir * s * 1.25f - s * 0.35f, y - s * 0.62f, s * 0.8f, s * 0.3f);    // tail
                g.DrawLine(pen, x - s * 0.35f, y - s * 0.2f, x - s * 0.35f + step, y); g.DrawLine(pen, x + s * 0.35f, y - s * 0.2f, x + s * 0.35f - step, y);   // legs
                g.DrawLine(pen, x - s * 0.2f, y - s * 0.2f, x - s * 0.2f - step, y); g.DrawLine(pen, x + s * 0.2f, y - s * 0.2f, x + s * 0.2f + step, y);
            }
            else if (mv[0] == 13)   // whale: a dark back arching out of the sea, a spout, then a splash
            {
                float s = 70 * sc, k = life, dir = mv[7];
                float rise = (float)Math.Sin(Math.Min(1, k / 0.75f) * Math.PI);
                if (rise > 0.02f)
                {
                    brush.Color = Color.FromArgb(255, 14, 10, 22);
                    float top = y - rise * s * 0.45f;
                    g.FillPie(brush, x - s, top, s * 2, rise * s * 0.9f + 2, 180, 180);
                    g.FillPolygon(brush, new PointF[] { new PointF(x + dir * s * 0.2f, top + s * 0.06f), new PointF(x + dir * s * 0.32f, top - s * 0.14f * rise), new PointF(x + dir * s * 0.42f, top + s * 0.1f) });
                }
                if (k > 0.15f && k < 0.55f)   // spout
                    for (int i = 0; i < 6; i++) GlowSprite.Draw(g, x - dir * s * 0.3f + (float)Math.Sin(i * 2.1) * 6 * ui, y - s * 0.5f - i * 12 * sc * (float)((k - 0.15) / 0.4), (6 + i) * ui, 220, 226, 245, 0.45f * (1 - i / 6f));
                if (k > 0.7f) GlowSprite.Draw(g, x, y, s * 0.9f * (k - 0.7f) / 0.3f + 4, 220, 226, 245, 0.5f * (1 - (k - 0.7f) / 0.3f));   // splash
            }
        }

        void DrawFalls(Graphics g, double t)
        {
            if (d.Falls.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (FallStreak st in fallStreaks)   // bright streaks sliding down each stream
            {
                float[] fl = d.Falls[st.Fall];
                float t0 = Math.Max(0, st.T - st.Len), t1 = Math.Min(1, st.T);
                if (t1 <= t0) continue;
                float width = fl[4] * sc;
                PointF a = M(fl[0] + (fl[2] - fl[0]) * t0, fl[1] + (fl[3] - fl[1]) * t0), b = M(fl[0] + (fl[2] - fl[0]) * t1, fl[1] + (fl[3] - fl[1]) * t1);
                float off = st.Off * width * (0.8f + 0.25f * t1);
                float al = (float)Math.Sin(Math.PI * Math.Max(0, Math.Min(1, (t0 + t1) / 2))) * (1 - Math.Abs(st.Off) * 1.2f);
                if (al <= 0.02f) continue;
                using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(a.X + off, a.Y - 1), new PointF(b.X + off, b.Y + 1), Color.FromArgb(0, 240, 242, 255), Color.FromArgb(Alpha.Clamp(150 * al), 240, 242, 255)))
                using (Pen p = new Pen(lb, Math.Max(1f, 1.6f * sc)))
                    g.DrawLine(p, a.X + off, a.Y, b.X + off, b.Y);
            }
            for (int i = 0; i < d.Falls.Count; i++)   // foam churning where each stream lands
            {
                float[] fl = d.Falls[i]; PointF b = M(fl[2], fl[3]);
                for (int k = 0; k < 3; k++)
                {
                    float jx = (float)Math.Sin(t / 230 + i * 3 + k * 2.1) * fl[4] * sc * 0.4f, jy = (float)Math.Sin(t / 310 + k) * 3 * sc;
                    GlowSprite.Draw(g, b.X + jx, b.Y + jy, fl[4] * sc * 0.55f, 238, 240, 252, 0.35f + 0.15f * (float)Math.Sin(t / 400 + i + k));
                }
            }
        }

        void DrawSways(Graphics g, double t)
        {
            foreach (Sway sy in sways)   // the layer is drawn in thin rows, each nudged by a slow wave, like wind moving through grass
            {
                int rows = 72, bh = sy.Bmp.Height, rh = Math.Max(1, bh / rows);
                for (int i = 0; i * rh < bh; i++)
                {
                    float up = 1 - (float)i / rows;   // the tops of the blades move more than the bottoms
                    float dx = (float)(Math.Sin(t / 1500.0 + i * 0.09) * 0.6 + Math.Sin(t / 2300.0 + i * 0.05 + 1.3) * 0.4) * sy.Amp * sc * (0.25f + 0.75f * up);
                    int hgt = Math.Min(rh, bh - i * rh);
                    float rowY = sy.Y + i * rh, R = 120 * ui;
                    if (hoverOn && HGrassSnow && Math.Abs(rowY - hy) < R * 1.4f)
                    {
                        // part the grass: the strip left of the mouse leans left, the strip right of it leans right
                        float push = (1 - Math.Abs(rowY - hy) / (R * 1.4f)) * 26 * ui;
                        float cx = hx - ox;
                        float leftW = Math.Max(0, cx), rightX = Math.Min(sy.Bmp.Width, cx);
                        if (leftW > 0) g.DrawImage(sy.Bmp, new RectangleF(ox + dx - push, rowY, leftW, hgt), new RectangleF(0, i * rh, leftW, hgt), GraphicsUnit.Pixel);
                        if (rightX < sy.Bmp.Width) g.DrawImage(sy.Bmp, new RectangleF(ox + dx + rightX + push, rowY, sy.Bmp.Width - rightX, hgt), new RectangleF(rightX, i * rh, sy.Bmp.Width - rightX, hgt), GraphicsUnit.Pixel);
                        continue;
                    }
                    g.DrawImage(sy.Bmp, new RectangleF(ox + dx, rowY, sy.Bmp.Width, hgt), new RectangleF(0, i * rh, sy.Bmp.Width, hgt), GraphicsUnit.Pixel);
                }
            }
        }

        void DrawMovers(Graphics g, double t)
        {
            foreach (float[] mv in movers)
            {
                float x = mv[1], y = mv[2];
                if (mv[0] >= 10) { DrawSurprise(g, mv, t); continue; }
                if (mv[0] == 3)   // aircraft far away: a steady white light with a red blink
                {
                    GlowSprite.Draw(g, x, y, 7 * ui, 255, 250, 240, 0.7f);
                    if ((t % 1300) < 160) GlowSprite.Draw(g, x - 6 * ui, y + 1 * ui, 9 * ui, 255, 70, 70, 0.9f);
                    continue;
                }
                if (mv[0] == 0)   // satellite: a steady point with a slow glint
                {
                    float a = (float)(0.6 + 0.4 * Math.Pow(Math.Max(0, Math.Sin(t / 2500 + x / 300)), 8));
                    brush.Color = Color.FromArgb(Alpha.Clamp(255 * a), 236, 236, 250); g.FillEllipse(brush, x - 1.6f * ui, y - 1.6f * ui, 3.2f * ui, 3.2f * ui);
                }
                else if (mv[0] == 1)   // debris: a small tumbling sliver
                {
                    float ang = (float)(t / 1000 * mv[7] * 2), r = 4 * ui;
                    PointF[] pts = { new PointF(x + (float)Math.Cos(ang) * r, y + (float)Math.Sin(ang) * r), new PointF(x + (float)Math.Cos(ang + 2.3) * r * 0.6f, y + (float)Math.Sin(ang + 2.3) * r * 0.6f), new PointF(x + (float)Math.Cos(ang + 3.9) * r * 0.8f, y + (float)Math.Sin(ang + 3.9) * r * 0.8f) };
                    brush.Color = Color.FromArgb(170, 150, 150, 170); g.FillPolygon(brush, pts);
                }
                else   // astronaut drifting by on a slow spin
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(x, y); g.RotateTransform((float)(t / 1000 * mv[7] * 20)); float u = 1.6f * ui;
                    brush.Color = Color.FromArgb(255, 210, 212, 222); g.FillRectangle(brush, -9 * u, -6 * u, 9 * u, 16 * u);   // backpack
                    brush.Color = Color.FromArgb(255, 236, 236, 244); g.FillEllipse(brush, -2 * u, -16 * u, 14 * u, 14 * u);   // helmet
                    brush.Color = Color.FromArgb(255, 200, 150, 60); g.FillEllipse(brush, 2 * u, -13 * u, 9 * u, 8 * u);     // gold visor
                    brush.Color = Color.FromArgb(255, 236, 236, 244); g.FillRectangle(brush, -1 * u, -3 * u, 13 * u, 16 * u); // body
                    g.FillRectangle(brush, 10 * u, -1 * u, 9 * u, 4 * u); g.FillRectangle(brush, -1 * u, 13 * u, 5 * u, 11 * u); g.FillRectangle(brush, 7 * u, 13 * u, 5 * u, 10 * u);
                    g.Restore(st);
                }
            }
        }

        void DrawFade(Graphics g, Fade fd, double t)
        {
            if (t - fd.LastMix > 100)   // re-blend ten times a second
            {
                fd.LastMix = t;
                int n = fd.Frames.Count;
                double pos = (t / 1000.0 / Math.Max(1f, fd.Period)) % n;
                int i = (int)pos, j = (i + 1) % n;
                float a = (float)(pos - i); a = a * a * (3 - 2 * a);   // ease in and out
                // the whole aurora slowly brightens and dims, with a faint flutter, like real aurora pulsing
                float glow = (float)(0.72 + 0.22 * (0.5 + 0.5 * Math.Sin(t / 2600.0)) + 0.06 * Math.Sin(t / 700.0)) + musicGlow * 0.12f;
                using (Graphics mg = Graphics.FromImage(fd.Mix))
                {
                    mg.Clear(Color.Transparent);
                    mg.CompositingMode = CompositingMode.SourceOver;
                    DrawWithAlpha(mg, fd.Frames[i], (1 - a) * glow);
                    DrawWithAlpha(mg, fd.Frames[j], a * glow);
                }
            }
            g.DrawImageUnscaled(fd.Mix, (int)ox, (int)fd.Y);
        }

        static void DrawWithAlpha(Graphics g, Bitmap b, float alpha)
        {
            if (alpha <= 0.01f) return;
            if (alpha >= 0.99f) { g.DrawImageUnscaled(b, 0, 0); return; }
            ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = alpha;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(b, new Rectangle(0, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }

        void DrawDrift(Graphics g, Drift dr, double t)
        {
            int bw = dr.Bmp.Width;
            int off = (int)((t / 1000.0 * dr.Speed * ui) % bw);
            int x = (int)ox - off;
            g.DrawImageUnscaled(dr.Bmp, x, (int)dr.Y);
            if (x + bw < w) g.DrawImageUnscaled(dr.Bmp, x + bw, (int)dr.Y);
        }

        void DrawTrails(Graphics g)
        {
            if (trailCount <= 0) return;
            Region old = g.Clip; if (skyRegion != null) g.SetClip(skyRegion, CombineMode.Intersect);
            foreach (Part q in parts)
            {
                if (q.Kind < 6 || q.Kind > 8 || q.Age < 0) continue;
                int[] c = TrailColours[q.Col]; float left = 1 - q.Age / q.Life;
                if (q.Kind == 6)   // glitter: a soft coloured glow with a bright twinkling core
                {
                    float tw = 0.45f + 0.55f * (float)Math.Abs(Math.Sin(q.Age * 7 + q.Seed * 6)), a = (float)Math.Pow(left, 1.1);
                    GlowSprite.Draw(g, q.X, q.Y, q.Size * 6f, c[0], c[1], c[2], a * 0.7f);
                    GlowSprite.Draw(g, q.X, q.Y, q.Size * 1.8f, 255, 255, 255, a * tw);
                }
                else if (q.Kind == 7) GlowSprite.Draw(g, q.X, q.Y, q.Size * 2.6f, c[0], c[1], c[2], (float)Math.Pow(left, 1.4) * 0.3f);
                else { GlowSprite.Draw(g, q.X, q.Y, q.Size * 5f, c[0], c[1], c[2], left * 0.8f); GlowSprite.Draw(g, q.X, q.Y, q.Size * 1.6f, 255, 250, 235, left); }
            }
            g.Clip = old;
        }

        void DrawStreaks(Graphics g, double t)
        {
            DrawTrails(g);
            foreach (float[] s in streaks)
            {
                if (t < s[4]) continue;
                float k = (float)((t - s[4]) / s[5]);
                float hx = s[0] + s[2] * k, hy = s[1] + s[3] * k, tx = hx - s[2] * s[6], ty = hy - s[3] * s[6];
                if (Math.Abs(hx - tx) + Math.Abs(hy - ty) < 2) continue;
                using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(hx, hy), new PointF(tx, ty), Color.FromArgb(Alpha.Clamp(230 * Math.Sin(k * Math.PI)), 255, 255, 255), Color.FromArgb(0, 236, 228, 244)))
                using (Pen p = new Pen(lb, s[7] * ui))
                    g.DrawLine(p, hx, hy, tx, ty);
            }
        }

        void DrawLife(Graphics g, double t, int phaseSel)
        {
            int k = 0;
            bool outside = phaseSel != 1, inside = phaseSel != 0;
            if (inside) DrawInside(g, t);
            if (!outside) return;
            for (int li = 0; li < d.Lights.Count; li++)
            {
                float[] L = d.Lights[li];
                float ph = phase[k++ % phase.Length];
                if (lightOff[li]) continue;
                double f = 0.5 + 0.5 * Math.Sin(t / (700 + ph * 90) + ph);
                PointF p = M(L[0], L[1]);
                float rr = Math.Max(1f, L[2] * sc) * (1.2f + 0.4f * (float)f);
                float near = hoverOn && HLamps ? Math.Max(0, 1 - (float)Math.Sqrt((p.X - hx) * (p.X - hx) + (p.Y - hy) * (p.Y - hy)) / (260 * ui)) : 0;
                GlowSprite.Draw(g, p.X, p.Y, rr * (4.5f + near * 4 + musicGlow), (int)L[3], (int)L[4], (int)L[5], 0.25f + 0.3f * (float)f + near * 0.4f + musicGlow * 0.15f);
                GlowSprite.Draw(g, p.X, p.Y, rr * 1.2f, 255, 240, 220, 0.6f + 0.4f * (float)f);
            }
            pen.Width = Math.Max(1f, sc);
            foreach (float[] S in d.Shimmers)
            {
                float ph = phase[k++ % phase.Length];
                double f = Math.Pow(Math.Max(0, Math.Sin(t / 650 + ph)), 3);
                if (f < 0.03) continue;
                PointF a = M(S[0], S[2]), c = M(S[1], S[2]);
                float drift = (float)Math.Sin(t / 1300 + ph) * 6 * sc;
                float mid = (a.X + c.X) / 2, half = (c.X - a.X) / 2 * (0.55f + 0.45f * (float)f);   // grows and shrinks with its brightness
                if (half < 2) continue;
                using (LinearGradientBrush gb = new LinearGradientBrush(new PointF(mid - half - 1, a.Y), new PointF(mid + half + 1, a.Y), Color.Transparent, Color.Transparent))
                {
                    ColorBlend cb = new ColorBlend(3);
                    Color core = Color.FromArgb(Alpha.Clamp(70 * f), 232, 222, 245);
                    cb.Colors = new Color[] { Color.FromArgb(0, core), core, Color.FromArgb(0, core) };
                    cb.Positions = new float[] { 0, 0.5f, 1 };
                    gb.InterpolationColors = cb;
                    g.FillRectangle(gb, mid - half + drift, a.Y - 0.6f * sc, half * 2, Math.Max(1f, 1.2f * sc));
                }
            }
            foreach (float[] R in d.Ripples)
            {
                float ph = phase[k++ % phase.Length];
                double f = 0.5 + 0.5 * Math.Sin(t / 520 + ph);
                PointF a = M(R[0], R[1]);
                float wob = (float)Math.Sin(t / 380 + ph * 2) * 2.5f * sc;
                float len = R[2] * sc;
                using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(a.X, a.Y - 1), new PointF(a.X, a.Y + len + 1), Color.FromArgb(0, (int)R[3], (int)R[4], (int)R[5]), Color.FromArgb(0, (int)R[3], (int)R[4], (int)R[5])))
                {
                    ColorBlend cb = new ColorBlend(3);
                    cb.Colors = new Color[] { Color.FromArgb(0, (int)R[3], (int)R[4], (int)R[5]), Color.FromArgb(Alpha.Clamp(18 + 34 * f), (int)R[3], (int)R[4], (int)R[5]), Color.FromArgb(0, (int)R[3], (int)R[4], (int)R[5]) };
                    cb.Positions = new float[] { 0, 0.45f, 1 }; lb.InterpolationColors = cb;
                    g.FillRectangle(lb, a.X + wob - 1.2f * sc, a.Y, 2.4f * sc, len);
                }
            }
            for (int wi = 0; wi < d.Windows.Count; wi++)   // firelight
            {
                float[] Wn = d.Windows[wi];
                float ph = phase[k++ % phase.Length];
                if (windowOff[wi]) { PointF po = M(Wn[0], Wn[1]); brush.Color = Color.FromArgb(255, 44, 30, 36); g.FillRectangle(brush, po.X, po.Y, Wn[2] * sc, Wn[3] * sc); continue; }
                double f = 0.6 + 0.25 * Math.Sin(t / 230 + ph) + 0.15 * Math.Sin(t / 97 + ph * 3);
                PointF p = M(Wn[0], Wn[1]);
                if (hoverOn && HLamps && Math.Abs(p.X - hx) < 240 * ui && Math.Abs(p.Y - hy) < 240 * ui) f += 0.45;
                float ww = Wn[2] * sc, hh = Wn[3] * sc;
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(p.X - ww * 2.5f, p.Y - hh * 2.5f, ww * 6, hh * 6);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(Alpha.Clamp(70 * f), 255, 170, 90);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, 255, 170, 90) };
                        g.FillPath(pb, gp);
                    }
                }
                brush.Color = Color.FromArgb(Alpha.Clamp(150 + 105 * Math.Min(1, f)), 255, 196, 115);
                g.FillRectangle(brush, p.X, p.Y, ww, hh);
            }
            for (int i = 0; i < d.Toggles.Count; i++)   // office windows
            {
                float[] T = d.Toggles[i];
                PointF p = M(T[0], T[1]);
                brush.Color = toggleOn[i] ? Color.FromArgb(255, (int)T[4], (int)T[5], (int)T[6]) : Color.FromArgb(255, (int)T[7], (int)T[8], (int)T[9]);
                g.FillRectangle(brush, p.X, p.Y, T[2] * sc, T[3] * sc);
            }
            foreach (float[] B in d.Beacons)   // blinking red light
            {
                double cyc = (t + B[0] * 7) % 1800;
                if (cyc > 260) continue;
                float a = (float)Math.Sin(Math.PI * cyc / 260);
                PointF p = M(B[0], B[1]);
                float r = 6 * sc;
                GlowSprite.Draw(g, p.X, p.Y, r * 6, 255, 60, 60, 0.45f * a);
                GlowSprite.Draw(g, p.X, p.Y, r * 1.6f, 255, 120, 110, a);
            }
            foreach (Part q in parts)
            {
                if (q.Kind >= 6 && q.Kind <= 8) continue;   // star trails are drawn with the shooting stars
                float life = q.Age / q.Life;
                if (q.Kind == 0 && d.LifeUnder) continue;   // drawn in the inside pass
                if (q.Kind == 0)   // smoke
                {
                    float s = q.Size * (1 + q.Age * 0.55f);
                    brush.Color = Color.FromArgb(Alpha.Clamp(55 * (1 - life) * Math.Min(1, q.Age * 2)), 196, 186, 214);
                    g.FillEllipse(brush, q.X - s, q.Y - s * 0.8f, s * 2, s * 1.6f);
                }
                else if (q.Kind == 1)   // sand
                {
                    brush.Color = Color.FromArgb(Alpha.Clamp(120 * (1 - life)), 214, 170, 170);
                    g.FillEllipse(brush, q.X - q.Size, q.Y - q.Size, q.Size * 2, q.Size * 2);
                }
                else if (q.Kind == 4)   // sparkles from a caught shooting star
                {
                    if (q.Age < 0) continue;
                    GlowSprite.Draw(g, q.X, q.Y, q.Size * 4, 255, 244, 214, (1 - life) * 0.8f);
                    continue;
                }
                else if (q.Kind == 5)   // little hearts from a happy cat
                {
                    if (q.Age < 0) continue;
                    float hs = q.Size, al = (1 - life);
                    brush.Color = Color.FromArgb(Alpha.Clamp(220 * al), 255, 150, 190);
                    g.FillEllipse(brush, q.X - hs, q.Y - hs * 0.5f, hs, hs); g.FillEllipse(brush, q.X, q.Y - hs * 0.5f, hs, hs);
                    g.FillPolygon(brush, new PointF[] { new PointF(q.X - hs * 0.95f, q.Y + hs * 0.05f), new PointF(q.X + hs * 0.95f, q.Y + hs * 0.05f), new PointF(q.X, q.Y + hs * 1.1f) });
                    continue;
                }
                else if (q.Kind == 3)   // ripple from a click on water
                {
                    if (q.Age < 0) continue;
                    float s3 = q.Size * (0.2f + life * 1.4f);
                    pen.Color = Color.FromArgb(Alpha.Clamp(150 * (1 - life)), 230, 222, 245); pen.Width = Math.Max(1f, 1.5f * ui);
                    g.DrawEllipse(pen, q.X - s3, q.Y - s3 * 0.3f, s3 * 2, s3 * 0.6f);
                }
                else   // puddle ripple
                {
                    if (q.Age < 0) continue;
                    float s = q.Size * (0.3f + life);
                    pen.Color = Color.FromArgb(Alpha.Clamp(120 * (1 - life)), 255, 190, 150);
                    g.DrawEllipse(pen, q.X - s, q.Y - s * 0.22f, s * 2, s * 0.44f);
                }
            }
            for (int i = 0; i < fx.Length; i++)   // fireflies
            {
                float a = (float)Math.Max(0, Math.Sin(t / 700 + fph[i] * 3));
                if (a < 0.05f) continue;
                float r = 2.2f * ui;
                GlowSprite.Draw(g, fx[i], fy[i], r * (7 + musicGlow * 2), 200, 255, 120, (0.4f + musicGlow * 0.15f) * a);
                GlowSprite.Draw(g, fx[i], fy[i], r * 1.6f, 240, 255, 190, a);
            }
            pen.Color = Color.FromArgb(255, 20, 12, 24); pen.Width = 2 * ui;
            for (int i = 0; i < bx.Length; i++)   // birds
            {
                float s = 11 * ui, flap = (float)Math.Sin(t / 160 + bph[i]) * s * 0.35f;
                g.DrawLines(pen, new PointF[] { new PointF(bx[i] - s, by[i] - flap), new PointF(bx[i] - s * 0.4f, by[i] - s * 0.05f), new PointF(bx[i], by[i] + s * 0.15f),
                                                new PointF(bx[i] + s * 0.4f, by[i] - s * 0.05f), new PointF(bx[i] + s, by[i] - flap) });
            }
            foreach (float[] B in d.Bobs)   // boat light bobbing on the water
            {
                PointF p = M(B[0], B[1]);
                float yb = p.Y + (float)Math.Sin(t / 900) * 1.5f * sc, r = 2.2f * ui;
                GlowSprite.Draw(g, p.X, yb, r * 6, (int)B[2], (int)B[3], (int)B[4], 0.35f);
                GlowSprite.Draw(g, p.X, yb, r * 1.5f, 255, 236, 200, 0.95f);
                for (int j = 0; j < 7; j++)   // its reflection, broken up by the swell
                {
                    float wob = (float)Math.Sin(t / 400 + j) * 2.5f * ui;
                    GlowSprite.Draw(g, p.X + wob, yb + (7 + j * 6) * ui, (3.2f - j * 0.25f) * ui, (int)B[2], (int)B[3], (int)B[4], (0.45f - j * 0.05f) * (0.6f + 0.4f * (float)Math.Sin(t / 300 + j)));
                }
            }
            for (int ci = 0; ci < d.Curtains.Count; ci++)   // aurora curtains shimmering
            {
                float[] C = d.Curtains[ci]; PointF p = M(C[0], C[1]);
                double f = 0.5 + 0.5 * Math.Sin(t / 900 + ci * 0.35) * Math.Sin(t / 2300 + ci * 0.11);
                float hh = C[2] * sc * (0.75f + 0.35f * (float)f);
                using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(p.X, p.Y + 1), new PointF(p.X, p.Y - hh - 1), Color.FromArgb(Alpha.Clamp(150 * f), (int)C[3], (int)C[4], (int)C[5]), Color.FromArgb(0, (int)C[3], (int)C[4], (int)C[5])))
                using (Pen cp = new Pen(lb, 5 * sc))
                    g.DrawLine(cp, p.X, p.Y, p.X + (float)Math.Sin(t / 1700 + ci) * 6 * sc, p.Y - hh);
            }
            foreach (float[] Wv in d.Waves)   // waves washing up the sand: a soft sheet of water with broken foam at its edge
            {
                float x1 = M(Wv[0], 0).X, x2 = M(Wv[1], 0).X, y = M(0, Wv[2]).Y, amp = Wv[3] * sc;
                int count = (int)Wv[4];
                for (int line = 0; line < count; line++)
                {
                    double cyc = (t / 6200.0 + line / (double)count) % 1.0;
                    float prog = (float)(1 - Math.Pow(1 - cyc, 2));
                    float yy = y - line * 12 * sc - amp * 1.2f + prog * amp * 3.6f;
                    float life = (float)Math.Sin(cyc * Math.PI) * (1 - line * 0.22f);
                    if (life < 0.04f) continue;
                    const int N = 48;
                    PointF[] edge = new PointF[N];
                    for (int i = 0; i < N; i++)
                    {
                        float xx = x1 + (x2 - x1) * i / (N - 1);
                        edge[i] = new PointF(xx, yy + (float)(Math.Sin(xx / (150 * sc) + line * 1.3 + t / 3400) * 6 + Math.Sin(xx / (53 * sc) + line) * 2.5) * sc);
                    }
                    // the thin sheet of water behind the foam line
                    PointF[] sheet = new PointF[N * 2];
                    for (int i = 0; i < N; i++) { sheet[i] = edge[i]; sheet[N * 2 - 1 - i] = new PointF(edge[i].X, edge[i].Y - amp * 1.4f); }
                    brush.Color = Color.FromArgb(Alpha.Clamp(28 * life), 200, 196, 230);
                    g.FillPolygon(brush, sheet);
                    // foam: broken into pieces of different brightness, never one hard line
                    for (int i = 0; i < N - 1; i++)
                    {
                        double n = 0.5 + 0.5 * Math.Sin(i * 1.7 + line * 4.1 + Math.Floor(cyc * 3) * 2.3);
                        float a = (float)(life * n);
                        if (a < 0.12f) continue;
                        pen.Color = Color.FromArgb(Alpha.Clamp(170 * a), 240, 236, 252); pen.Width = Math.Max(1f, (2.2f - line * 0.4f) * ui);
                        g.DrawLine(pen, edge[i], edge[i + 1]);
                        GlowSprite.Draw(g, (edge[i].X + edge[i + 1].X) / 2, (edge[i].Y + edge[i + 1].Y) / 2 + 2 * sc, 16 * sc, 236, 232, 250, 0.18f * a);
                    }
                }
            }
        }

        void DrawInside(Graphics g, double t)
        {
            foreach (float[] pl in d.Pulses)   // lamp and screen glow, breathing gently
            {
                if (pl[5] <= 0) continue;
                PointF p = M(pl[0], pl[1]);
                float r = 140 * sc;
                double f = 0.8 + 0.2 * Math.Sin(t / 1800 + pl[0]);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(p.X - r, p.Y - r, r * 2, r * 2);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(Alpha.Clamp(pl[5] * f), (int)pl[2], (int)pl[3], (int)pl[4]);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, (int)pl[2], (int)pl[3], (int)pl[4]) };
                        g.FillPath(pb, gp);
                    }
                }
            }
            if (d.LifeUnder)
                foreach (Part q in parts)
                {
                    if (q.Kind != 0) continue;
                    float life = q.Age / q.Life, s = q.Size * (1 + q.Age * 0.55f);
                    brush.Color = Color.FromArgb(Alpha.Clamp(55 * (1 - life) * Math.Min(1, q.Age * 2)), 214, 204, 226);
                    g.FillEllipse(brush, q.X - s, q.Y - s * 0.8f, s * 2, s * 1.6f);
                }
            for (int i = 0; i < gx.Length; i++)   // raindrops on the glass
            {
                float r = 2.6f * ui;
                brush.Color = Color.FromArgb(110, 224, 216, 244); g.FillEllipse(brush, gx[i] - r, gy[i] - r, r * 2, r * 2.6f);
                if (gv[i] > 0) { pen.Color = Color.FromArgb(50, 224, 216, 244); pen.Width = 1.2f * ui; g.DrawLine(pen, gx[i], gy[i] - r, gx[i] - (float)Math.Sin(t / 300 + gph[i]) * 2, gy[i] - 26 * ui); }
            }
        }

        void DrawDrops(Graphics g)
        {
            if (dropKind == 1)
            {
                pen.Color = Color.FromArgb(80, 205, 198, 228); pen.Width = 1;
                for (int i = 0; i < dx.Length; i++) g.DrawLine(pen, dx[i], dy[i], dx[i] + dlen[i] * 0.12f, dy[i] - dlen[i]);
            }
            else if (dropKind == 2)
            {
                brush.Color = Color.FromArgb(210, 242, 238, 250);
                for (int i = 0; i < dx.Length; i++) g.FillEllipse(brush, dx[i] - dr[i], dy[i] - dr[i], dr[i] * 2, dr[i] * 2);
            }
        }

        public void Dispose()
        {
            if (bg != null) bg.Dispose();
            if (front != null) front.Dispose();
            if (train != null) train.Bmp.Dispose();
            foreach (Bitmap b in new Bitmap[] { bgDawn, bgMix, frontDawn, frontMix }) if (b != null) b.Dispose();
            foreach (Sway sy in sways) sy.Bmp.Dispose();
            if (cardFont != null) cardFont.Dispose(); if (cardFontBig != null) cardFontBig.Dispose();
            if (cloudBmp != null) cloudBmp.Dispose();
            foreach (Drift dr in drifts) dr.Bmp.Dispose();
            foreach (Fade fd in fades) { foreach (Bitmap b in fd.Frames) b.Dispose(); fd.Mix.Dispose(); }
            if (skyClip != null) skyClip.Dispose();
            if (skyRegion != null) skyRegion.Dispose();
            if (d != null && d.Background != null) d.Background.Dispose();
            brush.Dispose(); pen.Dispose();
        }
    }

    // ---------------------------------------------------------------- sky manager
    static class SkyManager
    {
        static List<SkyForm> forms = new List<SkyForm>();
        public static bool Active { get { return forms.Count > 0; } }

        public static string SceneKey = "sky";
        static readonly Random pick = new Random();

        public static void Start(Settings s)
        {
            if (Active) return;
            SceneKey = s.Scene == "rotate" ? SceneData.Keys[pick.Next(SceneData.Keys.Length)] : s.Scene;
            uint startTick = NativeSky.LastInputTick();
            foreach (Screen sc in Screen.AllScreens)
            {
                SkyForm f = new SkyForm(s, sc, sc.Primary, startTick);
                forms.Add(f);
                f.Show();
            }
            Cursor.Hide();
            foreach (SkyForm f in forms) if (f.IsPrimary) f.Activate();
        }

        public static void StopAll()
        {
            if (!Active) return;
            foreach (SkyForm f in forms) f.BeginFadeOut();
        }

        public static void Closed(SkyForm f)
        {
            forms.Remove(f);
            if (forms.Count == 0)
            {
                Cursor.Show();
                DesktopIcons.Show();
            }
        }
    }

    // ---------------------------------------------------------------- real constellations (simplified shapes)
    class ConstellationShape
    {
        public string Name, Fact;
        public double Ra, Dec;   // rough center: right ascension in hours, declination in degrees
        public float[] Pts;      // x, y pairs in a 0..1 box
        public int[] Lines;      // index pairs
        public ConstellationShape(string name, double ra, double dec, string fact, float[] pts, int[] lines)
        { Name = name; Ra = ra; Dec = dec; Fact = fact; Pts = pts; Lines = lines; }

        public static readonly ConstellationShape[] All = {
            new ConstellationShape("Orion", 5.6, 5, "Look for the hunter's belt: three stars in a neat row, easy to spot even from a city.",
                new float[] { 0.50f,0.00f, 0.20f,0.15f, 0.75f,0.20f, 0.38f,0.50f, 0.50f,0.47f, 0.62f,0.44f, 0.30f,0.88f, 0.82f,0.82f },
                new int[] { 0,1, 0,2, 1,3, 2,5, 3,4, 4,5, 3,6, 5,7 }),
            new ConstellationShape("Big Dipper", 11.8, 55, "Follow the two stars at the end of the bowl and they point to the North Star.",
                new float[] { 0.95f,0.25f, 0.92f,0.55f, 0.66f,0.62f, 0.63f,0.36f, 0.44f,0.30f, 0.26f,0.22f, 0.02f,0.34f },
                new int[] { 0,1, 1,2, 2,3, 3,0, 3,4, 4,5, 5,6 }),
            new ConstellationShape("Little Dipper", 15.0, 77, "Polaris, the North Star, sits at the tip of its handle and barely moves all night.",
                new float[] { 0.02f,0.10f, 0.22f,0.20f, 0.40f,0.32f, 0.56f,0.46f, 0.82f,0.40f, 0.88f,0.70f, 0.62f,0.76f },
                new int[] { 0,1, 1,2, 2,3, 3,4, 4,5, 5,6, 6,3 }),
            new ConstellationShape("Cassiopeia", 1.0, 60, "A bright W, or an M, depending on the hour, circling the North Star all year.",
                new float[] { 0.00f,0.30f, 0.25f,0.75f, 0.50f,0.45f, 0.75f,0.80f, 1.00f,0.22f },
                new int[] { 0,1, 1,2, 2,3, 3,4 }),
            new ConstellationShape("Cygnus", 20.6, 42, "The swan flies along the Milky Way. Its tail star, Deneb, is a giant very far away.",
                new float[] { 0.50f,0.00f, 0.50f,0.40f, 0.52f,1.00f, 0.08f,0.34f, 0.92f,0.30f },
                new int[] { 0,1, 1,2, 3,1, 1,4 }),
            new ConstellationShape("Lyra", 18.8, 36, "Small but easy to find, thanks to Vega, one of the brightest stars in the sky.",
                new float[] { 0.50f,0.00f, 0.40f,0.32f, 0.62f,0.36f, 0.34f,0.86f, 0.58f,0.92f },
                new int[] { 0,1, 0,2, 1,2, 1,3, 2,4, 3,4 }),
            new ConstellationShape("Leo", 10.7, 15, "Find a backwards question mark, the lion's mane, with bright Regulus at its base.",
                new float[] { 0.10f,0.82f, 0.15f,0.56f, 0.26f,0.36f, 0.22f,0.18f, 0.12f,0.05f, 0.95f,0.70f, 0.66f,0.46f, 0.68f,0.74f },
                new int[] { 0,1, 1,2, 2,3, 3,4, 2,6, 6,5, 5,7, 7,0 }),
            new ConstellationShape("Scorpius", 16.9, -30, "A long curling tail and a red heart star, Antares, sitting low in the south.",
                new float[] { 0.00f,0.05f, 0.06f,0.20f, 0.03f,0.36f, 0.20f,0.24f, 0.32f,0.34f, 0.42f,0.50f, 0.50f,0.66f, 0.56f,0.82f, 0.66f,0.95f, 0.82f,0.98f, 0.95f,0.88f, 0.92f,0.74f },
                new int[] { 0,3, 1,3, 2,3, 3,4, 4,5, 5,6, 6,7, 7,8, 8,9, 9,10, 10,11 }),
            new ConstellationShape("Southern Cross", 12.5, -60, "The smallest constellation, and a compass for finding south.",
                new float[] { 0.50f,0.00f, 0.50f,1.00f, 0.15f,0.45f, 0.82f,0.55f },
                new int[] { 0,1, 2,3 }),
            new ConstellationShape("Pleiades", 3.8, 24, "The Seven Sisters: a tiny misty cluster that looks clearer if you glance just beside it.",
                new float[] { 0.20f,0.30f, 0.38f,0.22f, 0.50f,0.40f, 0.66f,0.30f, 0.58f,0.58f, 0.40f,0.62f, 0.80f,0.50f },
                new int[] { })
        };
    }

    // ---------------------------------------------------------------- which constellation is best tonight
    class TonightInfo
    {
        public ConstellationShape Shape;
        public string Where, When;
    }

    static class GentleStart
    {
        public static void Play()
        {
            try
            {
                List<Form> forms = new List<Form>();
                foreach (Screen sc in Screen.AllScreens) { GentleForm f = new GentleForm(sc.Bounds); f.Show(); forms.Add(f); }
                // runs on its own small message loop and closes itself; then everything is freed
                System.Windows.Forms.Timer done = new System.Windows.Forms.Timer(); done.Interval = 200;
                done.Tick += delegate { if (forms.TrueForAll(delegate (Form f) { return f.IsDisposed; })) { done.Stop(); done.Dispose(); Application.ExitThread(); } };
                done.Start();
                Application.Run();
            }
            catch { }
        }
    }

    class GentleForm : Form
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly float[] sx, sy, sr, sAt;
        const double StarsEnd = 2600, FadeStart = 3000, FadeEnd = 4800;
        public GentleForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Bounds = bounds; BackColor = Color.FromArgb(8, 6, 14); DoubleBuffered = true;
            Random r = new Random();
            int n = Math.Max(120, bounds.Width * bounds.Height / 9000);
            sx = new float[n]; sy = new float[n]; sr = new float[n]; sAt = new float[n];
            for (int i = 0; i < n; i++) { sx[i] = (float)r.NextDouble() * bounds.Width; sy[i] = (float)r.NextDouble() * bounds.Height; sr[i] = 0.6f + (float)Math.Pow(r.NextDouble(), 3) * 1.8f; sAt[i] = (float)(r.NextDouble() * StarsEnd); }
            tick.Interval = 33; tick.Tick += delegate { Step(); }; tick.Start();
        }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return cp; }   // layered, click-through, no taskbar button, never takes focus
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        void Step()
        {
            double t = clock.Elapsed.TotalMilliseconds;
            if (t > FadeStart) Opacity = Math.Max(0, 1 - (t - FadeStart) / (FadeEnd - FadeStart));
            Invalidate();
            if (t > FadeEnd) { tick.Stop(); tick.Dispose(); Close(); Dispose(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            double t = clock.Elapsed.TotalMilliseconds;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.White))
                for (int i = 0; i < sx.Length; i++)
                {
                    double age = t - sAt[i]; if (age < 0) continue;
                    float a = (float)Math.Min(1, age / 500);
                    b.Color = Color.FromArgb((int)(235 * a), 236, 228, 250);
                    e.Graphics.FillEllipse(b, sx[i] - sr[i], sy[i] - sr[i], sr[i] * 2, sr[i] * 2);
                }
        }
    }

    // Special days. Diwali and Holi follow the lunar calendar; dates from Drik Panchang (checked through 2033).
    static class Festivals
    {
        static readonly int[,] Diwali = { { 2026, 11, 8 }, { 2027, 10, 29 }, { 2028, 10, 17 }, { 2029, 11, 5 }, { 2030, 10, 26 }, { 2031, 11, 14 }, { 2032, 11, 2 }, { 2033, 10, 22 } };
        static readonly int[,] Holi = { { 2026, 3, 4 }, { 2027, 3, 22 }, { 2028, 3, 11 }, { 2029, 3, 1 }, { 2030, 3, 20 }, { 2031, 3, 9 }, { 2032, 3, 27 }, { 2033, 3, 16 }, { 2034, 3, 5 }, { 2035, 3, 24 } };
        static bool Near(int[,] table, DateTime day, int before, int after)
        {
            for (int i = 0; i < table.GetLength(0); i++)
            {
                double diff = (day.Date - new DateTime(table[i, 0], table[i, 1], table[i, 2])).TotalDays;
                if (diff >= -before && diff <= after) return true;
            }
            return false;
        }
        // which special day it is (or null); the birthday wins if two fall together
        public static string Today(Settings s, DateTime now)
        {
            if (s.FestBirthday && s.BirthMonth > 0 && now.Month == s.BirthMonth && now.Day == s.BirthDay) return "birthday";
            if (s.FestDiwali && Near(Diwali, now, 1, 1)) return "diwali";   // the evening before, the day, and the day after
            if (s.FestHoli && Near(Holi, now, 1, 0)) return "holi";         // Holika Dahan and the day of colours
            if (s.FestNewYear && now.Month == 12 && now.Day == 31) return "newyear";
            return null;
        }
    }

    static class SceneExport
    {
        // renders the wallpaper's scene at 3840x2160 as it looks right now (dawn, weather, moon, special day), in the background
        public static void SaveAsync(Settings s, string key)
        {
            System.Threading.Thread th = new System.Threading.Thread(delegate ()
            {
                try
                {
                    SceneData data = SceneData.Load(SceneData.Resolve(key, s), s); if (data == null) return;
                    using (SceneRenderer r = new SceneRenderer())
                    {
                        r.Parallax = false; r.Build(data, 3840, 2160, 2f);
                        r.SetDawn(SunClock.DawnAmount(s, DateTime.Now), true);
                        r.SetWeather(s.SceneWeather ? WeatherService.SceneKind(s) : "clear");
                        r.SetMoonPhase(s.RealMoon ? Moon.Phase(DateTime.UtcNow) : -1);
                        r.SetFestival(Festivals.Today(s, DateTime.Now));
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Nightfall");
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, "Nightfall " + key + " " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".png");
                        using (Bitmap bmp = new Bitmap(3840, 2160)) using (Graphics g = Graphics.FromImage(bmp))
                        {
                            for (int f = 0; f < 90; f++) { r.Step(f * 40, 0.04); r.Paint(g, f * 40); }   // let rain, fireflies and smoke settle in
                            bmp.Save(file, ImageFormat.Png);
                        }
                        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\"");
                    }
                }
                catch { }
            });
            th.IsBackground = true; th.Start();
        }
    }

    static class MoonCard
    {
        public static string[] Lines(Settings s)
        {
            double p = Moon.Phase(DateTime.UtcNow);
            string name = Moon.Name(p);
            List<string> lines = new List<string>();
            lines.Add(name);
            lines.Add(Moon.PercentLit(p) + "% lit");
            double toFull = ((0.5 - p) % 1 + 1) % 1 * 29.53, toNew = ((1 - p) % 1 + 1) % 1 * 29.53;
            if (name == "Full moon") lines.Add("New moon in " + Math.Max(1, (int)Math.Round(toNew)) + " days");
            else lines.Add("Full moon in " + Math.Max(1, (int)Math.Round(toFull)) + " days");
            try { TonightInfo ti = TonightSky.Get(s); if (ti != null) lines.Add("Tonight: " + ti.Shape.Name + ", " + ti.Where); } catch { }
            return lines.ToArray();
        }
    }

    static class TonightSky
    {
        static TonightInfo cached;
        static string cachedKey;

        static double Altitude(double latDeg, double lonDeg, double raHours, double decDeg, DateTime utc, out double azimuthDeg)
        {
            double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
            double gmst = (18.697374558 + 24.06570982441908 * d) % 24;
            if (gmst < 0) gmst += 24;
            double lst = gmst + lonDeg / 15.0;
            double ha = (lst - raHours) * 15 * Math.PI / 180;
            double lat = latDeg * Math.PI / 180, dec = decDeg * Math.PI / 180;
            double sinAlt = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(ha);
            double alt = Math.Asin(Math.Max(-1, Math.Min(1, sinAlt)));
            double az = Math.Atan2(Math.Sin(ha), Math.Cos(ha) * Math.Sin(lat) - Math.Tan(dec) * Math.Cos(lat));
            azimuthDeg = (az * 180 / Math.PI + 180 + 360) % 360;   // measured from north, clockwise
            return alt * 180 / Math.PI;
        }

        static string Direction(double az)
        {
            string[] names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
            return names[(int)Math.Round(az / 45.0) % 8];
        }

        public static TonightInfo Get(Settings s)
        {
            DateTime now = DateTime.Now;
            DateTime night = now.Hour < 6 ? now.Date.AddDays(-1) : now.Date;   // after midnight still counts as tonight
            double lat = double.IsNaN(s.Lat) ? 22 : s.Lat;
            double lon = double.IsNaN(s.Lon) ? TimeZoneInfo.Local.GetUtcOffset(now).TotalHours * 15 : s.Lon;
            string key = night.ToString("yyyyMMdd") + "|" + lat.ToString("0.0") + "|" + lon.ToString("0.0");
            if (key == cachedKey) return cached;

            List<TonightInfo> good = new List<TonightInfo>();
            List<double> heights = new List<double>();
            foreach (ConstellationShape c in ConstellationShape.All)
            {
                double best = -90, bestAz = 0; int bestHour = 21;
                for (int h = 19; h <= 25; h++)
                {
                    DateTime local = night.AddHours(h);
                    double az;
                    double alt = Altitude(lat, lon, c.Ra, c.Dec, local.ToUniversalTime(), out az);
                    if (alt > best) { best = alt; bestAz = az; bestHour = h; }
                }
                if (best < 25) continue;
                TonightInfo info = new TonightInfo();
                info.Shape = c;
                info.Where = best > 75 ? "high overhead" : "look " + Direction(bestAz);
                int hr = bestHour % 24;
                info.When = "best around " + (hr == 0 ? "midnight" : (hr > 12 ? (hr - 12) + " pm" : hr + (hr == 12 ? " pm" : " am")));
                good.Add(info);
                heights.Add(best);
            }
            if (good.Count == 0) { cachedKey = key; cached = null; return null; }
            // a different well-placed one each night, picked from the higher ones
            int[] idx = new int[good.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(heights.ToArray(), idx);
            Array.Reverse(idx);
            int choices = Math.Min(4, idx.Length);
            cached = good[idx[night.DayOfYear % choices]];
            cachedKey = key;
            return cached;
        }

        // tiny drawing of the shape, for the widget
        public static void DrawMini(Graphics g, ConstellationShape c, float x, float y, float size, Color color)
        {
            using (Pen p = new Pen(Color.FromArgb(150, color), Math.Max(1f, size / 14f)))
                for (int i = 0; i + 1 < c.Lines.Length; i += 2)
                    g.DrawLine(p, x + c.Pts[c.Lines[i] * 2] * size, y + c.Pts[c.Lines[i] * 2 + 1] * size,
                                  x + c.Pts[c.Lines[i + 1] * 2] * size, y + c.Pts[c.Lines[i + 1] * 2 + 1] * size);
            using (SolidBrush b = new SolidBrush(color))
                for (int i = 0; i < c.Pts.Length; i += 2)
                {
                    float r = Math.Max(0.9f, size / 11f);
                    g.FillEllipse(b, x + c.Pts[i] * size - r, y + c.Pts[i + 1] * size - r, r * 2, r * 2);
                }
        }
    }

    // ---------------------------------------------------------------- the sky itself
    class SkyForm : Form
    {
        class Star { public float x, y, r, phase, speed; }
        class Streak { public float x, y, vx, vy, tail, width; public double start, duration; }
        class Drop { public float x, y, len, speed, phase, r; }

        class Palette
        {
            public Color Top, Mid, Bottom, Glow, Ridge1, Ridge2, Ridge3, Cloud;
            public float Stars;
            public Palette(int[] top, int[] mid, int[] bot, int[] glow, int[] r1, int[] r2, int[] r3, int[] cloud, float stars)
            {
                Top = Color.FromArgb(top[0], top[1], top[2]); Mid = Color.FromArgb(mid[0], mid[1], mid[2]); Bottom = Color.FromArgb(bot[0], bot[1], bot[2]);
                Glow = Color.FromArgb(glow[0], glow[1], glow[2], glow[3]);
                Ridge1 = Color.FromArgb(r1[0], r1[1], r1[2]); Ridge2 = Color.FromArgb(r2[0], r2[1], r2[2]); Ridge3 = Color.FromArgb(r3[0], r3[1], r3[2]);
                Cloud = Color.FromArgb(cloud[0], cloud[1], cloud[2]);
                Stars = stars;
            }
        }
        static readonly Dictionary<string, Palette> Palettes = new Dictionary<string, Palette> {
            { "night", new Palette(new[] {13,7,17}, new[] {20,11,24}, new[] {34,18,42}, new[] {60,150,70,110}, new[] {44,26,51}, new[] {29,17,34}, new[] {18,10,22}, new[] {78,60,92}, 1f) },
            { "dusk",  new Palette(new[] {18,10,26}, new[] {36,18,44}, new[] {90,42,68}, new[] {90,220,110,90}, new[] {46,26,48}, new[] {31,16,34}, new[] {19,9,20}, new[] {110,70,96}, 0.7f) },
            { "dawn",  new Palette(new[] {21,22,50}, new[] {51,40,79}, new[] {124,80,112}, new[] {80,240,160,130}, new[] {45,36,64}, new[] {31,24,46}, new[] {20,15,30}, new[] {140,110,150}, 0.45f) },
            { "day",   new Palette(new[] {27,40,72}, new[] {44,61,102}, new[] {74,90,134}, new[] {50,170,190,235}, new[] {38,48,78}, new[] {27,34,56}, new[] {18,23,38}, new[] {150,160,195}, 0.12f) }
        };

        readonly Settings settings;
        public readonly bool IsPrimary;
        readonly uint startInputTick;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer();
        readonly Random rng = new Random();

        Bitmap background, textLayer, thoughtLayer, fog, clouds, aurora, starLayer;
        Bitmap[] ridgeLayers = new Bitmap[3];
        float[] ridgeOffsets = new float[3];
        float starOffset;
        int dimAlpha;
        string shownSong;
        // the real constellation currently fading in or out
        ConstellationShape activeShape;
        PointF[] activePts;
        PointF activeLabel;
        double activeStart, nextConstellation;
        int lastShape = -1;
        int lastHour = -1;
        SceneRenderer sceneR;
        double lastDawnCheck = -1e9;
        int rainDiv = 9000;
        Font labelFont;
        readonly SolidBrush twinkleBrush = new SolidBrush(Color.White);
        double shimmerStart = -999999;
        string phase = "night";
        float cloudOffset, auroraOffset;
        bool lockedPc;
        List<Star> twinklers = new List<Star>();
        List<Streak> streaks = new List<Streak>();
        List<Drop> drops = new List<Drop>();
        string weather = "clear";
        string currentMinute = "";
        string thought = "";
        double thoughtChangedAt = -999999, nextShot, nextShower, nextFlash, flash, lastFrameMs;
        float fogOffset;
        bool fadingOut;
        float scale = 1f;

        public SkyForm(Settings s, Screen screen, bool primary, uint startTick)
        {
            settings = s;
            IsPrimary = primary;
            startInputTick = startTick;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(13, 7, 17);
            DoubleBuffered = true;
            Opacity = 0;
            Text = "Nightfall";
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;

            weather = WeatherService.Kind(s);
            phase = CurrentPhase();
            if (SkyManager.SceneKey != "sky")
            {
                SceneData sd = SceneData.Load(SkyManager.SceneKey, settings);
                if (sd != null) { sceneR = new SceneRenderer(); sceneR.AllowShootingStars = settings.SkyShooting; sceneR.TrainGapSeconds = settings.TrainGap(); sceneR.Parallax = false; sceneR.ClickStars = sceneR.ClickLights = sceneR.ClickWater = false; sceneR.Build(sd, Width, Height, scale); weather = "clear";  sceneR.SetDawn(SunClock.DawnAmount(settings, DateTime.Now), true); sceneR.TrailStyle = settings.TrailStyle; sceneR.TrailLength = settings.TrailLength;
                    sceneR.SetWeather(settings.SceneWeather ? WeatherService.SceneKind(settings) : "clear"); sceneR.SetMoonPhase(settings.RealMoon ? Moon.Phase(DateTime.UtcNow) : -1); sceneR.SetFestival(Festivals.Today(settings, DateTime.Now)); }
            }
            if (sceneR != null) { background = new Bitmap(1, 1); }
            else
            {
                BuildBackground();
                BuildExtras();
            }
            BuildWeather();
            nextShot = 4000 + rng.Next(6000);
            nextShower = 60000 + rng.Next(60000);
            nextFlash = 6000 + rng.Next(9000);
            nextConstellation = 40000 + rng.Next(50000);
            if (IsPrimary && settings.SkyThoughts) { thought = Thoughts.Next(); thoughtChangedAt = 0; }

            frame.Interval = 40;
            frame.Tick += delegate { Step(); };
            frame.Start();
        }

        string CurrentPhase()
        {
            if (settings.SkyColors != "auto") return "night";
            DateTime now = DateTime.Now;
            double rise = 6 * 60, set = 18 * 60 + 30;
            if (WeatherService.Sunrise.Date == now.Date) rise = WeatherService.Sunrise.TimeOfDay.TotalMinutes;
            if (WeatherService.Sunset.Date == now.Date) set = WeatherService.Sunset.TimeOfDay.TotalMinutes;
            double m = now.TimeOfDay.TotalMinutes;
            if (m >= rise - 45 && m < rise + 45) return "dawn";
            if (m >= rise + 45 && m < set - 60) return "day";
            if (m >= set - 60 && m < set + 45) return "dusk";
            return "night";
        }

        // ---------- static layers
        void BuildBackground()
        {
            int w = Width, h = Height;
            Palette pal = Palettes[phase];
            if (background != null) background.Dispose();
            if (starLayer != null) starLayer.Dispose();
            for (int i = 0; i < 3; i++) if (ridgeLayers[i] != null) ridgeLayers[i].Dispose();
            twinklers.Clear();
            Random srng = new Random(7);   // same sky every rebuild

            // sky gradient, horizon glow and the moon (these don't move)
            background = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(background))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush sky = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.Black, Color.Black, 90f))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Colors = new Color[] { pal.Top, pal.Mid, pal.Bottom };
                    blend.Positions = new float[] { 0f, 0.45f, 1f };
                    sky.InterpolationColors = blend;
                    g.FillRectangle(sky, 0, 0, w, h);
                }
                using (GraphicsPath glowPath = new GraphicsPath())
                {
                    glowPath.AddEllipse(w * 0.2f, h * 0.68f, w * 0.7f, h * 0.28f);
                    using (PathGradientBrush glow = new PathGradientBrush(glowPath))
                    {
                        glow.CenterColor = pal.Glow;
                        glow.SurroundColors = new Color[] { Color.FromArgb(0, pal.Glow) };
                        g.FillPath(glow, glowPath);
                    }
                }
                if (settings.SkyMoon)
                {
                    double mp = Moon.Phase(DateTime.UtcNow);
                    if (settings.MoonStyle == "large")
                        Moon.DrawDetailed(g, w * 0.83f, h * 0.18f, Math.Max(34, Math.Min(w, h) * 0.05f), mp, pal.Mid);
                    else
                        Moon.Draw(g, w * 0.82f, h * 0.17f, Math.Max(20, Math.Min(44, Math.Min(w, h) * 0.033f)), mp, true);
                }
            }

            // stars on their own layer so they can drift (the layer tiles left to right)
            starLayer = new Bitmap(w, (int)(h * 0.76f), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(starLayer))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int count = (int)(w * h / 5200f);
                using (SolidBrush star = new SolidBrush(Color.FromArgb(Alpha.Clamp(190 * pal.Stars), 236, 228, 244)))
                {
                    for (int i = 0; i < count; i++)
                    {
                        float x = (float)srng.NextDouble() * w;
                        float y = (float)Math.Pow(srng.NextDouble(), 1.35) * h * 0.74f;
                        float r = srng.NextDouble() < 0.08 ? 1.3f + (float)srng.NextDouble() * 0.6f : 0.4f + (float)srng.NextDouble() * 0.8f;
                        r *= scale;
                        if (srng.NextDouble() < 0.18)
                        {
                            Star t = new Star();
                            t.x = x; t.y = y; t.r = r;
                            t.phase = (float)(srng.NextDouble() * Math.PI * 2);
                            t.speed = 0.6f + (float)srng.NextDouble() * 1.2f;
                            twinklers.Add(t);
                        }
                        else if (pal.Stars > 0.05f)
                        {
                            g.FillEllipse(star, x - r, y - r, r * 2, r * 2);
                            if (x < r * 2) g.FillEllipse(star, x + w - r, y - r, r * 2, r * 2);   // seamless wrap
                        }
                    }
                }
            }

            // three mountain layers, each tiling seamlessly
            ridgeLayers[0] = BuildRidge(w, h, pal.Ridge1, new float[] { 0, 210, 180, 170, 330, 190, 520, 120, 640, 140, 800, 60, 930, 120, 1090, 100, 1260, 150, 1430, 110, 1600, 210 });
            ridgeLayers[1] = BuildRidge(w, h, pal.Ridge2, new float[] { 0, 260, 220, 225, 400, 245, 600, 205, 820, 240, 1000, 210, 1220, 245, 1420, 220, 1600, 260 });
            ridgeLayers[2] = BuildRidge(w, h, pal.Ridge3, new float[] { 0, 310, 260, 290, 520, 305, 780, 285, 1060, 300, 1320, 288, 1600, 310 });

            dimAlpha = weather == "rain" || weather == "storm" ? 110 : weather == "fog" ? 70 : weather == "snow" ? 50 : 0;
        }

        Bitmap BuildRidge(int w, int h, Color color, float[] pts)
        {
            float top = h * 0.66f, sx = w / 1600f, sy = (h - top) / 360f;
            Bitmap bmp = new Bitmap(w, (int)Math.Ceiling(h - top), PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                List<PointF> poly = new List<PointF>();
                for (int i = 0; i < pts.Length; i += 2) poly.Add(new PointF(pts[i] * sx, pts[i + 1] * sy));
                poly.Add(new PointF(w + 2, bmp.Height));
                poly.Add(new PointF(-2, bmp.Height));
                using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, poly.ToArray());
            }
            return bmp;
        }

        static void DrawTiled(Graphics g, Bitmap layer, float offset, int y)
        {
            int x = -(int)offset;
            g.DrawImageUnscaled(layer, x, y);
            if (x + layer.Width < g.VisibleClipBounds.Right + 1) g.DrawImageUnscaled(layer, x + layer.Width, y);
        }

        void BuildExtras()
        {
            if (clouds != null) { clouds.Dispose(); clouds = null; }
            if (aurora != null) { aurora.Dispose(); aurora = null; }
            Palette pal = Palettes[phase];
            Random r = new Random(11);
            if (settings.SkyExtras == "clouds" || settings.SkyExtras == "both")
            {
                int cw = (int)(Width * 1.6f), ch = (int)(Height * 0.45f);
                clouds = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(clouds))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (int c = 0; c < 10; c++)
                    {
                        float cx = (float)r.NextDouble() * cw, cy = (0.15f + (float)r.NextDouble() * 0.6f) * ch;
                        float size = (90 + (float)r.NextDouble() * 160) * scale;
                        for (int part = 0; part < 14; part++)
                        {
                            float px = cx + ((float)r.NextDouble() - 0.5f) * size * 3.2f;
                            float py = cy + ((float)r.NextDouble() - 0.5f) * size * 0.2f;
                            float rx = size * (0.5f + (float)r.NextDouble() * 0.7f), ry = rx * 0.2f;
                            for (int wrap = -1; wrap <= 1; wrap++)   // draw wrapped copies so the texture tiles
                            {
                                float ox = px + wrap * cw;
                                if (ox + rx < 0 || ox - rx > cw) continue;
                                using (GraphicsPath p = new GraphicsPath())
                                {
                                    p.AddEllipse(ox - rx, py - ry, rx * 2, ry * 2);
                                    using (PathGradientBrush b = new PathGradientBrush(p))
                                    {
                                        b.CenterColor = Color.FromArgb(20, pal.Cloud);
                                        b.SurroundColors = new Color[] { Color.FromArgb(0, pal.Cloud) };
                                        g.FillPath(b, p);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            if (settings.SkyExtras == "aurora" || settings.SkyExtras == "both")
            {
                int aw = Width, ah = (int)(Height * 0.5f);
                aurora = new Bitmap(aw, ah, PixelFormat.Format32bppPArgb);
                float strength = phase == "day" ? 0.25f : phase == "night" ? 1f : 0.6f;
                using (Graphics g = Graphics.FromImage(aurora))
                {
                    int step = Math.Max(2, (int)(2 * scale));
                    for (int x = 0; x < aw; x += step)
                    {
                        double u = (double)x / aw * Math.PI * 2;      // whole periods, so it tiles
                        float baseY = ah * (0.55f + 0.10f * (float)Math.Sin(u * 2) + 0.05f * (float)Math.Sin(u * 5 + 1.3));
                        float band = ah * (0.30f + 0.12f * (float)Math.Sin(u * 3 + 0.7));
                        double wave = Math.Max(0, Math.Sin(u * 3 + 2.1)) * 0.75 + Math.Max(0, Math.Sin(u * 7 + 0.4)) * 0.25;
                        float glow = (float)(wave * wave);              // curtains with gaps between them
                        if (glow < 0.02f) continue;
                        float mix = (float)(0.5 + 0.5 * Math.Sin(u * 2 + 0.4));
                        int cr = (int)(60 + 90 * (1 - mix)), cg = (int)(200 * mix + 90 * (1 - mix)), cb = (int)(170 * mix + 220 * (1 - mix));
                        int a = (int)(75 * glow * strength);
                        RectangleF up = new RectangleF(x, baseY - band, step, band);
                        using (LinearGradientBrush lb = new LinearGradientBrush(up, Color.FromArgb(0, cr, cg, cb), Color.FromArgb(a, cr, cg, cb), 90f))
                            g.FillRectangle(lb, up);
                        float fade = band * 0.18f;                     // soft lower edge
                        RectangleF down = new RectangleF(x, baseY - 1, step, fade);
                        using (LinearGradientBrush lb = new LinearGradientBrush(down, Color.FromArgb(a, cr, cg, cb), Color.FromArgb(0, cr, cg, cb), 90f))
                            g.FillRectangle(lb, down);
                    }
                }
            }
        }

        static void FillRidge(Graphics g, int w, int h, Color color, float[] pts)
        {
            float top = h * 0.66f, sx = w / 1600f, sy = (h - top) / 360f;
            List<PointF> poly = new List<PointF>();
            for (int i = 0; i < pts.Length; i += 2) poly.Add(new PointF(pts[i] * sx, top + pts[i + 1] * sy));
            poly.Add(new PointF(w, h));
            poly.Add(new PointF(0, h));
            using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, poly.ToArray());
        }

        void BuildWeather()
        {
            drops.Clear();
            float area = Width * Height;
            if (weather == "rain" || weather == "storm")
            {
                int n = (int)(area / (weather == "storm" ? 5500f : (float)rainDiv));
                for (int i = 0; i < n; i++)
                {
                    Drop d = new Drop();
                    d.x = (float)rng.NextDouble() * Width; d.y = (float)rng.NextDouble() * Height;
                    d.len = (10 + (float)rng.NextDouble() * 10) * scale; d.speed = (650 + (float)rng.NextDouble() * 350) * scale;
                    drops.Add(d);
                }
            }
            else if (weather == "snow")
            {
                int n = (int)(area / 14000f);
                for (int i = 0; i < n; i++)
                {
                    Drop d = new Drop();
                    d.x = (float)rng.NextDouble() * Width; d.y = (float)rng.NextDouble() * Height;
                    d.r = (1 + (float)rng.NextDouble() * 1.6f) * scale; d.speed = (25 + (float)rng.NextDouble() * 35) * scale;
                    d.phase = (float)rng.NextDouble() * 6;
                    drops.Add(d);
                }
            }
            else if (weather == "fog")
            {
                fog = new Bitmap((int)(Width * 1.5f), (int)(Height * 0.6f), PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(fog))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    for (int i = 0; i < 26; i++)
                    {
                        float x = (float)rng.NextDouble() * fog.Width, y = (0.3f + (float)rng.NextDouble() * 0.6f) * fog.Height;
                        float r = (120 + (float)rng.NextDouble() * 200) * scale;
                        using (GraphicsPath p = new GraphicsPath())
                        {
                            p.AddEllipse(x - r, y - r, r * 2, r * 2);
                            using (PathGradientBrush b = new PathGradientBrush(p))
                            {
                                b.CenterColor = Color.FromArgb(22, 205, 195, 222);
                                b.SurroundColors = new Color[] { Color.FromArgb(0, 205, 195, 222) };
                                g.FillPath(b, p);
                            }
                        }
                    }
                }
            }
        }

        // builds text centered on x = 0, one line at a time so every line is centered
        GraphicsPath MakeText(string text, string family, float px)
        {
            GraphicsPath result = new GraphicsPath();
            FontFamily ff;
            try { ff = new FontFamily(family); }
            catch { ff = new FontFamily("Segoe UI"); }
            using (ff)
            {
                string[] lines = text.Split('\n');
                float y = 0;
                foreach (string line in lines)
                {
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        p.AddString(line.Length == 0 ? " " : line, ff, (int)FontStyle.Regular, px, new PointF(0, 0), StringFormat.GenericTypographic);
                        RectangleF b = p.GetBounds();
                        using (Matrix m = new Matrix())
                        {
                            m.Translate(-b.X - b.Width / 2, y);
                            p.Transform(m);
                        }
                        result.AddPath(p, false);
                    }
                    y += px * 1.45f;
                }
            }
            return result;
        }

        void BuildTextLayer()
        {
            if (textLayer != null) textLayer.Dispose();
            textLayer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            if (!settings.SkyClock) return;
            DateTime now = DateTime.Now;
            using (Graphics g = Graphics.FromImage(textLayer))
            using (GraphicsPath time = MakeText(settings.Use24h ? now.ToString("HH:mm") : now.ToString("h:mm"), settings.Font, Math.Min(Width, Height) * 0.16f))
            using (GraphicsPath date = MakeText(now.ToString("dddd, d MMMM"), "Segoe UI Semilight", 22 * scale))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF tb = time.GetBounds();
                using (Matrix m = new Matrix()) { m.Translate(Width / 2f, Height * (sceneR != null && sceneR.ClockTop > 0 ? sceneR.ClockTop : 0.24f) - tb.Y); time.Transform(m); }
                RectangleF tb2 = time.GetBounds();
                RectangleF db = date.GetBounds();
                using (Matrix m = new Matrix()) { m.Translate(Width / 2f, tb2.Bottom + 30 * scale - db.Y); date.Transform(m); }
                using (SolidBrush b = new SolidBrush(Color.FromArgb(240, 243, 238, 247))) g.FillPath(b, time);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 203, 191, 214))) g.FillPath(b, date);
                if (!string.IsNullOrEmpty(shownSong))
                {
                    string song = shownSong.Length > 60 ? shownSong.Substring(0, 59) + "\u2026" : shownSong;
                    using (GraphicsPath sp = MakeText("\u266A  " + song, "Segoe UI Semilight", 16 * scale))
                    {
                        RectangleF db2 = date.GetBounds();
                        RectangleF sb = sp.GetBounds();
                        using (Matrix m2 = new Matrix()) { m2.Translate(Width / 2f, db2.Bottom + 22 * scale - sb.Y); sp.Transform(m2); }
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 167, 150, 181))) g.FillPath(b, sp);
                    }
                }
            }
        }

        void BuildThoughtLayer()
        {
            if (thoughtLayer != null) { thoughtLayer.Dispose(); thoughtLayer = null; }
            if (string.IsNullOrEmpty(thought)) return;
            using (GraphicsPath p = MakeText(thought, "Segoe UI Semilight", 19 * scale))
            {
                RectangleF b = p.GetBounds();
                thoughtLayer = new Bitmap((int)Math.Ceiling(b.Width) + 8, (int)Math.Ceiling(b.Height) + 8, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(thoughtLayer))
                using (Matrix m = new Matrix())
                using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 190, 176, 204)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    m.Translate(4 - b.X, 4 - b.Y);
                    p.Transform(m);
                    g.FillPath(br, p);
                }
            }
        }

        // ---------- animation
        void Step()
        {
            double t = clock.Elapsed.TotalMilliseconds;
            double dt = Math.Min(0.1, (t - lastFrameMs) / 1000.0);
            lastFrameMs = t;

            // anything touched after the first second ends the sky
            if (!fadingOut && t > 1000 && NativeSky.LastInputTick() != startInputTick) { SkyManager.StopAll(); }

            if (fadingOut)
            {
                Opacity = Math.Max(0, Opacity - dt / 0.35);
                if (Opacity <= 0.01) { frame.Stop(); Close(); return; }
            }
            else if (Opacity < 1) Opacity = Math.Min(1, Opacity + dt / 1.6);

            string minute = DateTime.Now.ToString("HH:mm");
            if (minute != currentMinute)
            {
                currentMinute = minute;
                if (IsPrimary) BuildTextLayer();
                string p = CurrentPhase();
                if (p != phase && sceneR == null) { phase = p; BuildBackground(); BuildExtras(); }
            }
            if (IsPrimary && !lockedPc && settings.LockEnabled && settings.LockAfter > 0 && t > settings.LockAfter * 60000.0 && NowPlaying.Last == null)
            {
                lockedPc = true;
                NativeSky.LockWorkStation();
            }
            if (settings.SkyParallax && sceneR == null)
            {
                starOffset = (starOffset + (float)dt * 2.2f * scale) % Width;
                float[] speeds = { 4f, 7f, 11f };
                for (int i = 0; i < 3; i++) ridgeOffsets[i] = (ridgeOffsets[i] + (float)dt * speeds[i] * scale) % Width;
            }
            int hour = DateTime.Now.Hour;
            if (lastHour == -1) lastHour = hour;
            if (hour != lastHour) { lastHour = hour; if (settings.SkyHourly) HourShimmer(t); }
            if (IsPrimary)
            {
                string song = settings.SkyPlaying ? NowPlaying.Last : null;
                if (song != shownSong) { shownSong = song; BuildTextLayer(); }
                if (settings.SkyConstellations && sceneR == null) UpdateConstellation(t);
            }
            if (clouds != null) cloudOffset = (cloudOffset + (float)dt * 9 * scale) % clouds.Width;
            if (aurora != null) auroraOffset = (auroraOffset + (float)dt * 6 * scale) % aurora.Width;
            if (IsPrimary && settings.SkyThoughts && t - thoughtChangedAt > 60000) { thought = Thoughts.Next(); thoughtChangedAt = t; BuildThoughtLayer(); }
            if (IsPrimary && thoughtLayer == null && !string.IsNullOrEmpty(thought)) BuildThoughtLayer();

            if (sceneR != null)
            {
                if (t - lastDawnCheck > 1000) { lastDawnCheck = t; sceneR.SetDawn(SunClock.DawnAmount(settings, DateTime.Now)); }
                sceneR.Step(t, dt);
            }
            if (sceneR == null && settings.SkyShooting && t > nextShot)
            {
                Streak s = new Streak();
                s.x = Width * (0.35f + (float)rng.NextDouble() * 0.6f); s.y = Height * (0.05f + (float)rng.NextDouble() * (sceneR != null ? 0.12f : 0.25f));
                s.vx = -(260 + (float)rng.NextDouble() * 160) * scale; s.vy = (110 + (float)rng.NextDouble() * 60) * scale;
                s.tail = 0.25f; s.width = 1.6f; s.start = t; s.duration = 900;
                streaks.Add(s);
                nextShot = t + 7000 + rng.Next(9000);
            }
            if (settings.SkyShowers && t > nextShower)
            {
                if (sceneR != null) sceneR.StartShower(t); else StartShower(t);
                nextShower = t + 180000 + rng.Next(180000);
            }

            UpdateWeather(t, (float)dt);
            Invalidate();
        }

        const double ConstFadeIn = 3000, ConstHold = 22000, ConstFadeOut = 4000;

        void UpdateConstellation(double t)
        {
            if (activeShape != null)
            {
                if (t - activeStart > ConstFadeIn + ConstHold + ConstFadeOut) { activeShape = null; nextConstellation = t + 90000 + rng.Next(150000); }
                return;
            }
            if (t < nextConstellation) return;

            int pick = -1;
            if (lastShape == -1)
            {
                TonightInfo tonight = TonightSky.Get(settings);   // open with tonight's constellation
                if (tonight != null) pick = Array.IndexOf(ConstellationShape.All, tonight.Shape);
            }
            if (pick < 0)
                do { pick = rng.Next(ConstellationShape.All.Length); } while (pick == lastShape && ConstellationShape.All.Length > 1);
            lastShape = pick;
            activeShape = ConstellationShape.All[pick];

            // place it in open sky, away from the clock and the moon
            float size = Math.Min(Width, Height) * (0.20f + (float)rng.NextDouble() * 0.08f);
            float cx, cy;
            if (rng.NextDouble() < 0.6) { cx = Width * (0.09f + (float)rng.NextDouble() * 0.14f); cy = Height * (0.14f + (float)rng.NextDouble() * 0.24f); }
            else { size *= 0.85f; cx = Width * (0.76f + (float)rng.NextDouble() * 0.10f); cy = Height * (0.42f + (float)rng.NextDouble() * 0.08f); }
            double rot = (rng.NextDouble() - 0.5) * 0.6;
            int n = activeShape.Pts.Length / 2;
            activePts = new PointF[n];
            float maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float px = (activeShape.Pts[i * 2] - 0.5f) * size, py = (activeShape.Pts[i * 2 + 1] - 0.5f) * size;
                float rx = (float)(px * Math.Cos(rot) - py * Math.Sin(rot)), ry = (float)(px * Math.Sin(rot) + py * Math.Cos(rot));
                activePts[i] = new PointF(cx + rx + starOffset, cy + ry);   // stored in star-layer space so it drifts with the stars
                maxY = Math.Max(maxY, cy + ry);
            }
            activeLabel = new PointF(cx + starOffset, maxY + 18 * scale);
            activeStart = t;
        }

        void DrawConstellation(Graphics g, double t, float starDim)
        {
            if (activeShape == null || activePts == null) return;
            double age = t - activeStart;
            float a = (float)Math.Min(1, Math.Min(age / ConstFadeIn, (ConstFadeIn + ConstHold + ConstFadeOut - age) / ConstFadeOut));
            if (a <= 0.01f) return;
            a *= Math.Max(0.35f, starDim);
            float shift = -starOffset;
            int lineCount = activeShape.Lines.Length / 2;
            double drawn = Math.Max(0, Math.Min(lineCount, (age - 1500) / 4000.0 * lineCount));
            using (Pen p = new Pen(Color.FromArgb(Alpha.Clamp(120 * a), 214, 200, 238), 1.1f * scale))
            {
                for (int i = 0; i < lineCount; i++)
                {
                    double part = drawn - i;
                    if (part <= 0) break;
                    PointF s1 = activePts[activeShape.Lines[i * 2]], s2 = activePts[activeShape.Lines[i * 2 + 1]];
                    float f = (float)Math.Min(1, part);
                    g.DrawLine(p, Wrap(s1.X + shift), s1.Y, Wrap(s1.X + shift) + (s2.X - s1.X) * f, s1.Y + (s2.Y - s1.Y) * f);
                }
            }
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(Alpha.Clamp(45 * a), 236, 228, 244)))
            using (SolidBrush star = new SolidBrush(Color.FromArgb(Alpha.Clamp(255 * a), 248, 244, 255)))
            {
                foreach (PointF pt in activePts)
                {
                    float x = Wrap(pt.X + shift), r = 2.1f * scale;
                    g.FillEllipse(glow, x - r * 3, pt.Y - r * 3, r * 6, r * 6);
                    g.FillEllipse(star, x - r, pt.Y - r, r * 2, r * 2);
                }
            }
            if (labelFont == null) labelFont = new Font("Segoe UI Semilight", 11f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Alpha.Clamp(170 * a), 203, 191, 214)))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(activeShape.Name, labelFont, b, Wrap(activeLabel.X + shift), activeLabel.Y, fmt);
            }
        }

        float Wrap(float x)
        {
            float w = Width;
            while (x < -w * 0.2f) x += w;
            while (x > w * 1.2f) x -= w;
            return x;
        }

        // a quiet marker of the hour: the stars shimmer, a few shooting stars, and a constellation appears
        void HourShimmer(double t)
        {
            if (sceneR != null) { sceneR.Shimmer(t); return; }
            shimmerStart = t;
            for (int i = 0; i < 4; i++)
            {
                Streak s = new Streak();
                s.x = Width * (0.4f + (float)rng.NextDouble() * 0.55f); s.y = Height * (0.04f + (float)rng.NextDouble() * 0.2f);
                s.vx = -(240 + (float)rng.NextDouble() * 160) * scale; s.vy = (100 + (float)rng.NextDouble() * 60) * scale;
                s.tail = 0.28f; s.width = 1.5f; s.start = t + 600 + i * 450 + rng.Next(250); s.duration = 950;
                streaks.Add(s);
            }
            if (IsPrimary && settings.SkyConstellations && activeShape == null) nextConstellation = t + 2500;
        }

        void StartShower(double t)
        {
            int count = 10 + rng.Next(6);
            float rx = Width * (0.25f + (float)rng.NextDouble() * 0.5f), ry = Height * (0.04f + (float)rng.NextDouble() * 0.12f);
            double spread = Math.Min(7000, 1200 + count * 150);
            for (int i = 0; i < count; i++)
            {
                double angle = 0.2 + rng.NextDouble() * (Math.PI - 0.4);
                float dist = (30 + (float)rng.NextDouble() * 110) * scale, travel = (260 + (float)rng.NextDouble() * 260) * scale;
                Streak s = new Streak();
                s.x = rx + (float)Math.Cos(angle) * dist; s.y = ry + (float)Math.Sin(angle) * dist;
                s.vx = (float)Math.Cos(angle) * travel; s.vy = (float)Math.Sin(angle) * travel;
                s.tail = 0.35f + (float)rng.NextDouble() * 0.2f; s.width = 1.2f + (float)rng.NextDouble();
                s.start = t + rng.NextDouble() * spread; s.duration = 700 + rng.Next(500);
                streaks.Add(s);
            }
        }

        void UpdateWeather(double t, float dt)
        {
            if (weather == "rain" || weather == "storm")
            {
                foreach (Drop d in drops)
                {
                    d.y += d.speed * dt; d.x -= d.speed * dt * 0.12f;
                    if (d.y > Height) { d.y = -d.len; d.x = (float)rng.NextDouble() * Width * 1.1f; }
                }
                if (weather == "storm")
                {
                    if (t > nextFlash) { flash = 1; nextFlash = t + 8000 + rng.Next(12000); }
                    flash = Math.Max(0, flash - dt * 3);
                }
            }
            else if (weather == "snow")
            {
                foreach (Drop d in drops)
                {
                    d.y += d.speed * dt;
                    d.x += (float)Math.Sin(t / 1200 + d.phase) * 12 * dt;
                    if (d.y > Height) { d.y = -4; d.x = (float)rng.NextDouble() * Width; }
                }
            }
            else if (weather == "fog" && fog != null)
            {
                fogOffset = (fogOffset + dt * 12 * scale) % (fog.Width / 3f);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintFrame(e); }
            catch (Exception) { }   // a single frame that can't be drawn is skipped; the next one carries on
        }

        void PaintFrame(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            double t = clock.Elapsed.TotalMilliseconds;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(background, 0, 0);
            if (sceneR != null) { sceneR.Paint(g, t); DrawTextLayers(g, t); return; }
            float starDim = (weather == "rain" || weather == "storm" ? 0.35f : weather == "fog" ? 0.55f : weather == "snow" ? 0.65f : 1f) * Palettes[phase].Stars;
            if (starLayer != null) DrawTiled(g, starLayer, starOffset, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Star s in twinklers)
            {
                double since = t - shimmerStart;
                double boost = since >= 0 && since < 7000 ? Math.Sin(Math.PI * since / 7000) * (0.5 + 0.5 * Math.Sin(t / 160 + s.phase * 3)) : 0;
                int a = (int)(255 * Math.Min(1, starDim * (0.55 + 0.45 * Math.Sin(t / 1000 * s.speed + s.phase)) + boost * 0.6));
                float x = s.x - starOffset;
                if (x < -4) x += Width;
                twinkleBrush.Color = Color.FromArgb(Math.Max(0, Math.Min(255, a)), 236, 228, 244);
                g.FillEllipse(twinkleBrush, x - s.r, s.y - s.r, s.r * 2, s.r * 2);
            }
            if (IsPrimary) DrawConstellation(g, t, starDim);
            if (aurora != null)
            {
                int ay = (int)(Height * 0.12f), ax = (int)-auroraOffset;
                g.DrawImageUnscaled(aurora, ax, ay);
                g.DrawImageUnscaled(aurora, ax + aurora.Width, ay);
            }
            if (clouds != null)
            {
                int cy = (int)(Height * 0.04f), cx = (int)-cloudOffset;
                g.DrawImageUnscaled(clouds, cx, cy);
                if (cx + clouds.Width < Width) g.DrawImageUnscaled(clouds, cx + clouds.Width, cy);
            }
            int ridgeTop = (int)(Height * 0.66f);
            for (int i = 0; i < 3; i++) if (ridgeLayers[i] != null) DrawTiled(g, ridgeLayers[i], ridgeOffsets[i], ridgeTop);
            if (dimAlpha > 0) using (SolidBrush d = new SolidBrush(Color.FromArgb(dimAlpha, 13, 7, 17))) g.FillRectangle(d, 0, 0, Width, Height);

            DrawStreaks(g, t);
            DrawWeatherLayer(g);
            DrawTextLayers(g, t);
        }

        void DrawStreaks(Graphics g, double t)
        {
            for (int i = streaks.Count - 1; i >= 0; i--)
            {
                Streak s = streaks[i];
                if (t > s.start + s.duration) { streaks.RemoveAt(i); continue; }
                if (t < s.start) continue;
                float k = (float)((t - s.start) / s.duration);
                float hx = s.x + s.vx * k, hy = s.y + s.vy * k;
                float tx = hx - s.vx * s.tail, ty = hy - s.vy * s.tail;
                if (Math.Abs(hx - tx) + Math.Abs(hy - ty) < 2) continue;
                int alpha = (int)(240 * Math.Sin(k * Math.PI));
                using (LinearGradientBrush lb = new LinearGradientBrush(new PointF(hx, hy), new PointF(tx, ty), Color.FromArgb(alpha, 255, 255, 255), Color.FromArgb(0, 236, 228, 244)))
                using (Pen p = new Pen(lb, s.width * scale))
                    g.DrawLine(p, hx, hy, tx, ty);
            }
        }

        void DrawWeatherLayer(Graphics g)
        {
            if (weather == "rain" || weather == "storm")
            {
                using (Pen p = new Pen(Color.FromArgb(80, 205, 198, 228), 1))
                    foreach (Drop d in drops) g.DrawLine(p, d.x, d.y, d.x + d.len * 0.12f, d.y - d.len);
                if (flash > 0.01)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(Alpha.Clamp(36 * flash), 230, 225, 255)))
                        g.FillRectangle(b, 0, 0, Width, Height);
            }
            else if (weather == "snow")
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 240, 236, 248)))
                    foreach (Drop d in drops) g.FillEllipse(b, d.x - d.r, d.y - d.r, d.r * 2, d.r * 2);
            }
            else if (weather == "fog" && fog != null)
            {
                g.DrawImageUnscaled(fog, (int)-fogOffset, (int)(Height * 0.45f));
                g.DrawImageUnscaled(fog, (int)(-fog.Width / 3f + fogOffset * 0.6f), (int)(Height * 0.45f + 30 * scale));
            }
        }

        void DrawTextLayers(Graphics g, double t)
        {
            if (IsPrimary && textLayer != null) g.DrawImageUnscaled(textLayer, 0, 0);
            if (IsPrimary && thoughtLayer != null)
            {
                double age = t - thoughtChangedAt;
                float a = (float)Math.Min(1, Math.Min(age / 1500.0, (60000 - age) / 1500.0));
                if (a > 0.01)
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = a;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cm);
                        int x = (Width - thoughtLayer.Width) / 2, y = (int)(Height * 0.9f) - thoughtLayer.Height;
                        g.DrawImage(thoughtLayer, new Rectangle(x, y, thoughtLayer.Width, thoughtLayer.Height), 0, 0, thoughtLayer.Width, thoughtLayer.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public void BeginFadeOut() { fadingOut = true; }

        protected override void OnKeyDown(KeyEventArgs e) { SkyManager.StopAll(); }
        protected override void OnMouseDown(MouseEventArgs e) { SkyManager.StopAll(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            frame.Stop();
            if (background != null) background.Dispose();
            if (textLayer != null) textLayer.Dispose();
            if (thoughtLayer != null) thoughtLayer.Dispose();
            if (fog != null) fog.Dispose();
            if (starLayer != null) starLayer.Dispose();
            for (int i = 0; i < 3; i++) if (ridgeLayers[i] != null) ridgeLayers[i].Dispose();
            if (clouds != null) clouds.Dispose();
            if (aurora != null) aurora.Dispose();
            if (labelFont != null) labelFont.Dispose();
            if (sceneR != null) sceneR.Dispose();
            twinkleBrush.Dispose();
            base.OnFormClosed(e);
            SkyManager.Closed(this);
        }
    }

    // ---------------------------------------------------------------- settings window

    // A grid of scene previews to choose from.
    class ScenePickerForm : Form
    {
        public string Chosen;
        public static Image Thumb(string key)
        {
            try
            {
                string f = Path.Combine(SceneData.Folder, "thumbs", key + ".jpg");
                if (File.Exists(f)) using (Image im = Image.FromFile(f)) return new Bitmap(im);
            }
            catch { }
            return null;
        }
        public static string NameOf(string key)
        {
            if (key == "sky") return "Nightfall sky (classic)";
            if (key == "rotate") return "A different scene each time";
            int i = Array.IndexOf(SceneData.Keys, key);
            return i >= 0 ? SceneData.Names[i] : key;
        }

        public ScenePickerForm(string current, bool includeExtras, Color bg, Color fg)
        {
            Text = "Choose a scene";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = bg; ForeColor = fg; Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;
            KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            FlowLayoutPanel grid = new FlowLayoutPanel();
            grid.Dock = DockStyle.Fill; grid.AutoScroll = true; grid.Padding = new Padding(12);
            List<string> keys = new List<string>();
            if (includeExtras) keys.Add("sky");
            keys.AddRange(SceneData.Keys);
            if (includeExtras) keys.Add("rotate");
            Button focus = null;
            foreach (string k in keys)
            {
                string key = k;
                Button b = new Button();
                b.Size = new Size(176, 124);
                b.Margin = new Padding(6);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = key == current ? 2 : 1;
                b.FlatAppearance.BorderColor = key == current ? Color.FromArgb(200, 170, 230) : Color.FromArgb(60, 44, 70);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 32, 56);
                Image t = Thumb(key);
                if (t != null) { b.Image = new Bitmap(t, new Size(160, 90)); t.Dispose(); }
                b.Text = NameOf(key);
                b.TextImageRelation = TextImageRelation.ImageAboveText;
                b.ForeColor = fg;
                b.Click += delegate { Chosen = key; DialogResult = DialogResult.OK; Close(); };
                grid.Controls.Add(b);
                if (key == current) focus = b;
            }
            Controls.Add(grid);
            ClientSize = new Size(4 * 188 + 24 + SystemInformation.VerticalScrollBarWidth, Math.Min(4 * 136 + 24, Screen.PrimaryScreen.WorkingArea.Height - 120));
            if (focus != null) Shown += delegate { focus.Focus(); grid.ScrollControlIntoView(focus); };
        }
    }

    class AwaySettingsForm : Form
    {
        readonly Settings settings;
        readonly Action onChange;
        NumericUpDown iconsMin, skyMin;
        CheckBox shooting, showers, moon, clockBox, thoughts, pauseMedia, playingBox, parallaxBox, constBox, hourlyBox;
        ComboBox weatherBox, colorsBox, extrasBox, moonBox, wallModeBox;
        CheckBox wallOn, wallBattery, wallSaver, wallStars, wallLights, wallWater, wallParallax;
        CheckBox pFireflies, pStars, pCat, pWater, pGrassSnow, pBirds, pLamps, pMeteor, pMoon, pTrain, pCatMug, pCatch, pDraw, pWeather, pSurprises;
        ComboBox hoverBox, trailBox, trailLenBox;
        CheckBox gentleBox, sceneWeatherBox, realMoonBox, fDiwali, fHoli, fNewYear, fBirthday, musicBox;
        ComboBox birthMonthBox, birthDayBox, monitorsBox;
        ComboBox trainBox, roomBox, timeBox;
        NumericUpDown wallBelow;
        CheckBox h24Box, playingLineBox, bigMoonBox, tonightBox, alignBox, startupBox, centerBox;
        ComboBox lookBox, lookMaxBox;
        readonly List<FlowLayoutPanel> pages = new List<FlowLayoutPanel>();
        string sceneValue, wallSceneValue;
        readonly List<Button> tabButtons = new List<Button>();
        FlowLayoutPanel tabBar;
        Panel scroller;
        NumericUpDown lockMin;
        CheckBox lockBox;
        TextBox cityBox;
        Label status;

        static readonly Color Bg = Color.FromArgb(26, 15, 31);
        static readonly Color Fg = Color.FromArgb(236, 228, 244);
        static readonly Color Muted = Color.FromArgb(167, 150, 181);

        public AwaySettingsForm(Settings s, Action changed)
        {
            settings = s;
            onChange = changed;
            Text = "Moon Clock settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = false;
            Padding = new Padding(0);

            tabBar = new FlowLayoutPanel();
            tabBar.Dock = DockStyle.Top;
            tabBar.AutoSize = true;
            tabBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            tabBar.Padding = new Padding(14, 10, 14, 0);
            tabBar.BackColor = Color.FromArgb(34, 21, 40);
            // settings scroll inside this panel, so the window always fits on screen
            scroller = new Panel();
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            scroller.Padding = new Padding(18, 14, 18, 6);

            // ---------------- Clock tab
            FlowLayoutPanel col = NewPage("Clock");
            col.Controls.Add(Heading("On the desktop"));
            h24Box = Check("Use 24-hour time", s.Use24h); col.Controls.Add(h24Box);
            playingLineBox = Check("Show what's playing", s.ShowPlaying); col.Controls.Add(playingLineBox);
            gentleBox = Check("Gentle start: the night sky fades in when Windows starts", s.GentleStart); col.Controls.Add(gentleBox);
            bigMoonBox = Check("Large moon beside the time", s.BigMoon); col.Controls.Add(bigMoonBox);
            tonightBox = Check("Show tonight's constellation", s.ShowTonight); col.Controls.Add(tonightBox);
            alignBox = Check("Align to the right", s.AlignRight); col.Controls.Add(alignBox);
            startupBox = Check("Start with Windows", ClockWindow.IsStartup()); col.Controls.Add(startupBox);
            col.Controls.Add(Heading("Taskbar"));
            centerBox = Check(TaskbarSupport.Supported ? "Center taskbar icons" : "Center taskbar icons (Windows 10 only)", s.CenterTaskbar && TaskbarSupport.Supported);
            centerBox.Enabled = TaskbarSupport.Supported; col.Controls.Add(centerBox);
            lookBox = Combo(new string[] { "Clear", "Blur", "Acrylic (night tint)", "Windows default" }, new string[] { "clear", "blur", "acrylic", "off" }, s.TaskbarStyle);
            col.Controls.Add(Row("Taskbar look", lookBox, ""));
            lookMaxBox = Combo(new string[] { "Keep the same look", "Clear", "Blur", "Acrylic (night tint)", "Windows default" }, new string[] { "same", "clear", "blur", "acrylic", "normal" }, s.TaskbarMaximized);
            col.Controls.Add(Row("When a window is maximized", lookMaxBox, ""));
            lookBox.Enabled = lookMaxBox.Enabled = TaskbarSupport.Supported;
            col.Controls.Add(Hint("Tip: double-click the moon in the tray to open these settings."));

            // ---------------- Away mode tab
            col = NewPage("Away mode");
            col.Controls.Add(Heading("When you're away"));
            iconsMin = Number(s.IconsAfter);
            col.Controls.Add(Row("Hide desktop icons after", iconsMin, "minutes"));
            skyMin = Number(s.SkyAfter);
            col.Controls.Add(Row("Show the night sky after", skyMin, "minutes"));
            col.Controls.Add(Hint("Set either one to 0 to turn it off. Moving the mouse or pressing a key brings everything back."));
            pauseMedia = Check("Don't show the sky while music or video is playing", s.PauseForMedia);
            col.Controls.Add(pauseMedia);
            lockBox = Check("Lock my PC while I'm away", s.LockEnabled && s.LockAfter > 0);
            col.Controls.Add(lockBox);
            lockMin = Number(s.LockAfter > 0 ? s.LockAfter : 20);
            lockMin.Minimum = 1;
            Control lockRow = Row("      after the sky has shown for", lockMin, "minutes");
            col.Controls.Add(lockRow);
            col.Controls.Add(Hint("It never locks while music or video is playing."));
            lockMin.Enabled = lockBox.Checked;
            lockBox.CheckedChanged += delegate { lockMin.Enabled = lockBox.Checked; };

            // ---------------- Scenes tab
            col = NewPage("Scenes");
            col.Controls.Add(Heading("Look"));
            List<string> sl = new List<string>(), sv = new List<string>();
            sl.Add("Nightfall sky (classic)"); sv.Add("sky");
            for (int i = 0; i < SceneData.Keys.Length; i++) { sl.Add(SceneData.Names[i]); sv.Add(SceneData.Keys[i]); }
            sl.Add("A different scene each time"); sv.Add("rotate");
            sceneValue = s.Scene;
            col.Controls.Add(PickerRow("Scene", sceneValue, true, delegate(string k) { sceneValue = k; }));
            col.Controls.Add(Hint("Illustrated scenes have their own moon and weather, so the options below apply to the classic sky."));
            colorsBox = Combo(new string[] { "Always night (classic)", "Follow the time of day" }, new string[] { "night", "auto" }, s.SkyColors);
            col.Controls.Add(Row("Sky colors", colorsBox, ""));
            extrasBox = Combo(new string[] { "Nothing extra (classic)", "Drifting clouds", "Soft aurora", "Clouds and aurora" }, new string[] { "none", "clouds", "aurora", "both" }, s.SkyExtras);
            col.Controls.Add(Row("Extras", extrasBox, ""));
            moonBox = Combo(new string[] { "Small (classic)", "Real moon (NASA photo)" }, new string[] { "classic", "large" }, s.MoonStyle);
            col.Controls.Add(Row("Moon", moonBox, ""));
            parallaxBox = Check("Gentle parallax (stars, clouds and mountains drift)", s.SkyParallax); col.Controls.Add(parallaxBox);

            col = pages[1];   // these belong with away mode
            col.Controls.Add(Heading("In the sky"));
            shooting = Check("Shooting stars", s.SkyShooting); col.Controls.Add(shooting);
            showers = Check("Meteor showers now and then", s.SkyShowers); col.Controls.Add(showers);
            moon = Check("Tonight's moon", s.SkyMoon); col.Controls.Add(moon);
            clockBox = Check("Clock and date", s.SkyClock); col.Controls.Add(clockBox);
            thoughts = Check("A poetic line that changes every minute", s.SkyThoughts); col.Controls.Add(thoughts);
            playingBox = Check("What's playing, under the date", s.SkyPlaying); col.Controls.Add(playingBox);
            constBox = Check("Real constellations now and then", s.SkyConstellations); col.Controls.Add(constBox);
            hourlyBox = Check("A gentle shimmer at the top of each hour", s.SkyHourly); col.Controls.Add(hourlyBox);

            col = pages[2];
            col.Controls.Add(Heading("Scene options"));
            sceneWeatherBox = Check("Match the real weather (rain, storms, snow, mist, clouds)", s.SceneWeather); col.Controls.Add(sceneWeatherBox);
            realMoonBox = Check("Show tonight's real moon phase", s.RealMoon); col.Controls.Add(realMoonBox);
            timeBox = Combo(new string[] { "Follow the real sunrise", "Follow the real sunrise and sunset", "Always night", "Always dawn" },
                            new string[] { "sunrise", "both", "night", "dawn" }, s.TimeOfDay);
            col.Controls.Add(Row("Time of day", timeBox, ""));
            col.Controls.Add(Hint("Scenes turn to dawn colours around sunrise where you are (set your city in the Weather tab)."));
            trainBox = Combo(new string[] { "Every few minutes", "Every 10 minutes", "Always, one after another" }, new string[] { "few", "ten", "loop" }, s.TrainEvery);
            col.Controls.Add(Row("Steam Train passes", trainBox, ""));
            roomBox = Combo(new string[] { "Rainy City", "Mountain Lake", "Snowy Forest", "Beach and Sea", "A different view each time", "Match the weather outside" },
                            new string[] { "city", "lake", "snow", "sea", "random", "weather" }, s.RoomView);
            col.Controls.Add(Row("Cozy Room window", roomBox, ""));

            // ---------------- Wallpaper tab
            col = NewPage("Wallpaper");
            col.Controls.Add(Heading("Live wallpaper"));
            wallOn = Check("Play a live scene behind my desktop icons", s.WallpaperOn); col.Controls.Add(wallOn);
            col.Controls.Add(Hint("It only moves while your desktop is showing. When any window is open, it freezes and uses no power."));
            wallModeBox = Combo(new string[] { "One scene I choose", "A different scene each day", "A different scene each time I unlock" }, new string[] { "fixed", "daily", "unlock" }, s.WallMode);
            col.Controls.Add(Row("Which scene", wallModeBox, ""));
            monitorsBox = Combo(new string[] { "A different scene on each screen", "The same scene on every screen" }, new string[] { "different", "same" }, s.WallMonitors);
            monitorsBox.Width = 250;
            col.Controls.Add(Row("With more than one screen", monitorsBox, ""));
            wallSceneValue = Array.IndexOf(SceneData.Keys, s.WallScene) >= 0 ? s.WallScene : "aurora";
            Control wallPick = PickerRow("Scene", wallSceneValue, false, delegate(string k) { wallSceneValue = k; });
            col.Controls.Add(wallPick);
            wallBattery = Check("Pause when running on battery", s.WallPauseOnBattery); col.Controls.Add(wallBattery);
            wallSaver = Check("Pause when battery saver is on", s.WallPauseSaver); col.Controls.Add(wallSaver);
            wallBelow = Number(s.WallPauseBelow); wallBelow.Maximum = 100;
            col.Controls.Add(Row("Pause when battery is below", wallBelow, "% (0 = never)"));
            col.Controls.Add(Hint("Clicks, hover effects and surprises are in the Play tab."));
            EventHandler wallSync = delegate { wallPick.Enabled = wallModeBox.SelectedIndex == 0; };
            wallModeBox.SelectedIndexChanged += wallSync; wallSync(null, null);

            // ---------------- Play tab: everything interactive on the live wallpaper, tidy and grouped
            col = NewPage("Play");
            col.Controls.Add(Hint("These work on the live wallpaper, on empty desktop space."));
            col.Controls.Add(Heading("Hover"));
            hoverBox = Combo(new string[] { "While holding Ctrl", "Always on", "Off" }, new string[] { "ctrl", "always", "off" }, s.PlayHover);
            col.Controls.Add(Row("Hover effects work", hoverBox, ""));
            pFireflies = Check("Fireflies drift toward you", s.PlayFireflies); pStars = Check("Stars sparkle as you pass", s.PlayStars);
            pCat = Check("The cat notices you", s.PlayCat); pWater = Check("Ripples follow you on water", s.PlayWater);
            pGrassSnow = Check("Grass parts, snow swirls", s.PlayGrassSnow); pBirds = Check("Birds scatter", s.PlayBirds);
            pLamps = Check("Lamps glow brighter", s.PlayLamps);
            col.Controls.Add(TwoCols(pFireflies, pStars, pCat, pWater, pGrassSnow, pBirds, pLamps));
            col.Controls.Add(Heading("Clicks"));
            wallStars = Check("Sky: a shooting star", s.WallClickStars); wallLights = Check("Windows and lamps: on or off", s.WallClickLights);
            wallWater = Check("Water: ripples", s.WallClickWater); pMeteor = Check("Hold the sky: meteor shower", s.PlayMeteorHold);
            pMoon = Check("Moon: phase and tonight's sky", s.PlayMoon); pCatch = Check("Catch a shooting star", s.PlayCatch);
            pTrain = Check("Track: send the train", s.PlayTrain); pCatMug = Check("Cat and mug react", s.PlayCatMug);
            pDraw = Check("Drag across stars: draw a constellation", s.PlayDraw); pWeather = Check("Double-click the sky: rain or snow", s.PlayWeather);
            col.Controls.Add(TwoCols(wallStars, wallLights, wallWater, pMeteor, pMoon, pCatch, pTrain, pCatMug, pDraw, pWeather));
            col.Controls.Add(Heading("Extras"));
            trailBox = Combo(new string[] { "Glittering dust", "Glowing ribbon", "Sparkler sparks", "None" }, new string[] { "dust", "ribbon", "sparks", "none" }, s.TrailStyle);
            col.Controls.Add(Row("Shooting star trails", trailBox, ""));
            trailLenBox = Combo(new string[] { "Brief (about 1 second)", "Lingering (about 3 seconds)", "Long (up to 6 seconds)" }, new string[] { "brief", "linger", "long" }, s.TrailLength);
            col.Controls.Add(Row("Trails last", trailLenBox, ""));
            pSurprises = Check("Rare surprises (a comet, an owl, a fox, a whale)", s.PlaySurprises); col.Controls.Add(pSurprises);
            wallParallax = Check("Move the mouse to gently shift the view", s.WallParallax); col.Controls.Add(wallParallax);
            musicBox = Check("Glow softly while music is playing", s.PlayMusicGlow); col.Controls.Add(musicBox);
            col.Controls.Add(Heading("Special days"));
            fDiwali = Check("Diwali: diyas and lanterns", s.FestDiwali); fHoli = Check("Holi: clouds of colour", s.FestHoli);
            fNewYear = Check("New Year's Eve: fireworks", s.FestNewYear); fBirthday = Check("My birthday: lanterns and a heart", s.FestBirthday);
            col.Controls.Add(TwoCols(fDiwali, fHoli, fNewYear, fBirthday));
            string[] months = { "Not set", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
            string[] monthKeys = new string[13]; for (int i = 0; i < 13; i++) monthKeys[i] = i.ToString();
            birthMonthBox = Combo(months, monthKeys, s.BirthMonth.ToString());
            string[] days = new string[31], dayKeys = new string[31]; for (int i = 0; i < 31; i++) { days[i] = (i + 1).ToString(); dayKeys[i] = (i + 1).ToString(); }
            birthDayBox = Combo(days, dayKeys, Math.Max(1, s.BirthDay).ToString());
            FlowLayoutPanel bday = new FlowLayoutPanel(); bday.AutoSize = true; bday.WrapContents = false; bday.Margin = new Padding(0);
            bday.Controls.Add(Row("Birthday", birthMonthBox, "")); bday.Controls.Add(birthDayBox); birthDayBox.Margin = new Padding(6, 6, 0, 0); birthDayBox.Width = 60;
            col.Controls.Add(bday);

            // ---------------- Weather tab
            col = NewPage("Weather");
            col.Controls.Add(Heading("Weather"));
            weatherBox = new ComboBox();
            weatherBox.DropDownStyle = ComboBoxStyle.DropDownList;
            weatherBox.Width = 300;
            weatherBox.Items.AddRange(new object[] { "Match the weather outside", "Always clear", "Always rain", "Always storm", "Always snow", "Always fog" });
            string[] keys = { "auto", "clear", "rain", "storm", "snow", "fog" };
            weatherBox.SelectedIndex = Math.Max(0, Array.IndexOf(keys, s.Weather));
            col.Controls.Add(weatherBox);

            FlowLayoutPanel cityRow = new FlowLayoutPanel();
            cityRow.AutoSize = true; cityRow.WrapContents = false;
            cityBox = new TextBox();
            cityBox.Width = 200;
            cityBox.Text = s.City;
            Button find = Btn("Save city");
            find.Click += delegate { FindCity(); };
            cityRow.Controls.Add(cityBox);
            cityRow.Controls.Add(find);
            col.Controls.Add(cityRow);
            status = Hint(WeatherService.Status(s));
            col.Controls.Add(status);

            // buttons stay pinned at the bottom, always visible
            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Padding = new Padding(18, 10, 18, 12);
            buttons.BackColor = Color.FromArgb(34, 21, 40);
            Button preview = Btn("Preview the sky");
            preview.Click += delegate { Apply(); SkyManager.Start(settings); };
            Button save = Btn("Save");
            save.Click += delegate { Apply(); Close(); };
            Button cancel = Btn("Cancel");
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(preview);
            buttons.Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;

            Controls.Add(scroller);
            Controls.Add(buttons);
            Controls.Add(tabBar);
            scroller.BringToFront();   // fill the space between the tabs and the button bar
            ShowPage(0);

            // size: fits the tallest tab, but never taller than the screen
            int barH = 0;
            Size content = Size.Empty;
            for (int i = 0; i < pages.Count; i++)   // lay out each tab for real, then measure it
            {
                ShowPage(i);
                pages[i].PerformLayout();
                Size ps = pages[i].GetPreferredSize(Size.Empty);
                content = new Size(Math.Max(content.Width, ps.Width), Math.Max(content.Height, ps.Height + pages[i].Margin.Vertical + 8));
            }
            ShowPage(0);
            content.Width = Math.Max(content.Width, tabBar.GetPreferredSize(Size.Empty).Width - 18);
            barH += tabBar.GetPreferredSize(Size.Empty).Height;
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            barH += buttons.GetPreferredSize(Size.Empty).Height;
            int wantW = content.Width + scroller.Padding.Horizontal + SystemInformation.VerticalScrollBarWidth + 4;
            int wantH = content.Height + scroller.Padding.Vertical + barH;
            ClientSize = new Size(Math.Min(wantW, work.Width - 40), Math.Min(wantH, (int)(work.Height * 0.9)));
            MinimumSize = new Size(Math.Min(Width, 360), Math.Min(Height, 300));
            FormBorderStyle = FormBorderStyle.Sizable;   // can be resized if you like
            MaximizeBox = false;
        }

        // a small preview of the chosen scene, its name, and a button that opens the picker
        Control PickerRow(string label, string current, bool extras, Action<string> chosen)
        {
            FlowLayoutPanel row = new FlowLayoutPanel();
            row.AutoSize = true; row.WrapContents = false; row.Margin = new Padding(0, 4, 0, 6);
            Label l = new Label(); l.Text = label; l.AutoSize = true; l.Margin = new Padding(3, 30, 8, 0); l.ForeColor = Fg;
            PictureBox pb = new PictureBox(); pb.Size = new Size(128, 72); pb.SizeMode = PictureBoxSizeMode.Zoom; pb.Margin = new Padding(0, 0, 10, 0);
            pb.Cursor = Cursors.Hand;
            Label name = new Label(); name.AutoSize = true; name.ForeColor = Fg; name.Margin = new Padding(0, 14, 0, 0);
            Button choose = Btn("Choose...");
            FlowLayoutPanel right = new FlowLayoutPanel(); right.FlowDirection = FlowDirection.TopDown; right.AutoSize = true; right.WrapContents = false;
            right.Controls.Add(name); right.Controls.Add(choose);
            Action<string> show = delegate(string k)
            {
                Image old = pb.Image; pb.Image = ScenePickerForm.Thumb(k); if (old != null) old.Dispose();
                name.Text = ScenePickerForm.NameOf(k);
            };
            show(current);
            EventHandler open = delegate
            {
                using (ScenePickerForm f = new ScenePickerForm(current, extras, Bg, Fg))
                    if (f.ShowDialog(this) == DialogResult.OK && f.Chosen != null) { current = f.Chosen; show(current); chosen(current); }
            };
            choose.Click += open; pb.Click += open;
            row.Controls.Add(l); row.Controls.Add(pb); row.Controls.Add(right);
            return row;
        }

        // checkboxes laid out in two tidy columns
        Control TwoCols(params CheckBox[] boxes)
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.ColumnCount = 2; t.AutoSize = true; t.AutoSizeMode = AutoSizeMode.GrowAndShrink; t.Margin = new Padding(0, 0, 0, 4);
            // a fixed first column so every two-column group on the tab lines up
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));   // the form's DPI scaling enlarges this on bigger displays t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < boxes.Length; i++) { boxes[i].Margin = new Padding(3, 2, 18, 2); t.Controls.Add(boxes[i], i % 2, i / 2); }
            return t;
        }

        FlowLayoutPanel NewPage(string title)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.FlowDirection = FlowDirection.TopDown;
            p.WrapContents = false;
            p.AutoSize = true;
            pages.Add(p);
            int index = pages.Count - 1;
            Button b = new Button();
            b.Text = title;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 32, 56);
            b.AutoSize = true;
            b.Padding = new Padding(8, 4, 8, 6);
            b.Margin = new Padding(0, 0, 4, 0);
            b.ForeColor = Muted;
            b.Cursor = Cursors.Hand;
            b.TabStop = true;
            b.Click += delegate { ShowPage(index); };
            tabButtons.Add(b);
            tabBar.Controls.Add(b);
            return p;
        }

        void ShowPage(int index)
        {
            scroller.SuspendLayout();
            scroller.Controls.Clear();
            scroller.Controls.Add(pages[index]);
            scroller.AutoScrollPosition = Point.Empty;
            scroller.ResumeLayout();
            for (int i = 0; i < tabButtons.Count; i++)
            {
                bool on = i == index;
                tabButtons[i].BackColor = on ? Bg : Color.FromArgb(34, 21, 40);
                tabButtons[i].ForeColor = on ? Fg : Muted;
                tabButtons[i].Font = new Font(Font, on ? FontStyle.Bold : FontStyle.Regular);
            }
        }

        // Ctrl+Tab / Ctrl+Shift+Tab move between tabs, like other Windows settings windows
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.Control) != 0 && (keyData & Keys.KeyCode) == Keys.Tab)
            {
                int cur = 0;
                for (int i = 0; i < pages.Count; i++) if (scroller.Controls.Contains(pages[i])) cur = i;
                int next = (keyData & Keys.Shift) != 0 ? (cur + pages.Count - 1) % pages.Count : (cur + 1) % pages.Count;
                ShowPage(next);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void FindCity()
        {
            string name = cityBox.Text.Trim();
            if (name.Length == 0) { status.Text = "Enter a city name first."; cityBox.Focus(); return; }
            status.Text = "Looking up your city...";
            try
            {
                string display; double lat, lon;
                if (WeatherService.FindCity(name, out display, out lat, out lon))
                {
                    settings.City = display; settings.Lat = lat; settings.Lon = lon;
                    cityBox.Text = display;
                    settings.Save();
                    WeatherService.Refresh(settings, true);
                    status.Text = "Saved. Weather will match " + display + ".";
                }
                else status.Text = "Couldn't find \"" + name + "\". Try a nearby larger city.";
            }
            catch { status.Text = "Couldn't reach the weather service. Check your connection and try again."; }
        }

        void Apply()
        {
            string[] keys = { "auto", "clear", "rain", "storm", "snow", "fog" };
            settings.IconsAfter = (int)iconsMin.Value;
            settings.SkyAfter = (int)skyMin.Value;
            settings.PauseForMedia = pauseMedia.Checked;
            settings.SkyShooting = shooting.Checked;
            settings.SkyShowers = showers.Checked;
            settings.SkyMoon = moon.Checked;
            settings.SkyClock = clockBox.Checked;
            settings.SkyThoughts = thoughts.Checked;
            settings.Weather = keys[Math.Max(0, weatherBox.SelectedIndex)];
            settings.SkyColors = (string)((object[])colorsBox.Tag)[colorsBox.SelectedIndex];
            settings.SkyExtras = (string)((object[])extrasBox.Tag)[extrasBox.SelectedIndex];
            settings.MoonStyle = (string)((object[])moonBox.Tag)[moonBox.SelectedIndex];
            settings.Scene = sceneValue;
            settings.LockAfter = (int)lockMin.Value;
            settings.LockEnabled = lockBox.Checked;
            settings.Use24h = h24Box.Checked; settings.ShowPlaying = playingLineBox.Checked; settings.BigMoon = bigMoonBox.Checked;
            settings.ShowTonight = tonightBox.Checked; settings.AlignRight = alignBox.Checked;
            if (TaskbarSupport.Supported)
            {
                settings.CenterTaskbar = centerBox.Checked;
                settings.TaskbarStyle = (string)((object[])lookBox.Tag)[lookBox.SelectedIndex];
                settings.TaskbarMaximized = (string)((object[])lookMaxBox.Tag)[lookMaxBox.SelectedIndex];
            }
            if (startupBox.Checked != ClockWindow.IsStartup()) ClockWindow.SetStartup(startupBox.Checked);
            settings.WallpaperOn = wallOn.Checked;
            settings.WallMode = (string)((object[])wallModeBox.Tag)[wallModeBox.SelectedIndex];
            settings.WallScene = wallSceneValue;
            settings.WallPauseOnBattery = wallBattery.Checked;
            settings.WallPauseSaver = wallSaver.Checked;
            settings.WallPauseBelow = (int)wallBelow.Value;
            settings.TrainEvery = (string)((object[])trainBox.Tag)[trainBox.SelectedIndex];
            settings.RoomView = (string)((object[])roomBox.Tag)[roomBox.SelectedIndex];
            settings.TimeOfDay = (string)((object[])timeBox.Tag)[timeBox.SelectedIndex];
            settings.SceneWeather = sceneWeatherBox.Checked; settings.RealMoon = realMoonBox.Checked; settings.GentleStart = gentleBox.Checked;
            settings.PlayHover = (string)((object[])hoverBox.Tag)[hoverBox.SelectedIndex];
            settings.TrailStyle = (string)((object[])trailBox.Tag)[trailBox.SelectedIndex];
            settings.PlayMusicGlow = musicBox.Checked; settings.FestDiwali = fDiwali.Checked; settings.FestHoli = fHoli.Checked; settings.FestNewYear = fNewYear.Checked; settings.FestBirthday = fBirthday.Checked;
            settings.BirthMonth = int.Parse((string)((object[])birthMonthBox.Tag)[birthMonthBox.SelectedIndex]); settings.BirthDay = int.Parse((string)((object[])birthDayBox.Tag)[birthDayBox.SelectedIndex]);
            settings.WallMonitors = (string)((object[])monitorsBox.Tag)[monitorsBox.SelectedIndex];
            settings.TrailLength = (string)((object[])trailLenBox.Tag)[trailLenBox.SelectedIndex];
            settings.PlayFireflies = pFireflies.Checked; settings.PlayStars = pStars.Checked; settings.PlayCat = pCat.Checked; settings.PlayWater = pWater.Checked;
            settings.PlayGrassSnow = pGrassSnow.Checked; settings.PlayBirds = pBirds.Checked; settings.PlayLamps = pLamps.Checked;
            settings.PlayMeteorHold = pMeteor.Checked; settings.PlayMoon = pMoon.Checked; settings.PlayTrain = pTrain.Checked; settings.PlayCatMug = pCatMug.Checked;
            settings.PlayCatch = pCatch.Checked; settings.PlayDraw = pDraw.Checked; settings.PlayWeather = pWeather.Checked; settings.PlaySurprises = pSurprises.Checked;
            settings.WallClickStars = wallStars.Checked; settings.WallClickLights = wallLights.Checked;
            settings.WallClickWater = wallWater.Checked; settings.WallParallax = wallParallax.Checked;
            settings.SkyPlaying = playingBox.Checked;
            settings.SkyParallax = parallaxBox.Checked;
            settings.SkyConstellations = constBox.Checked;
            settings.SkyHourly = hourlyBox.Checked;
            settings.Save();
            WeatherService.Refresh(settings, false);
            LiveWallpaper.Apply(settings);
            if (onChange != null) onChange();
        }

        // ---------- small control helpers
        Label Heading(string text)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 10.5f);
            l.Margin = new Padding(0, 12, 0, 4);
            return l;
        }
        Label Hint(string text)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = true; l.MaximumSize = new Size(380, 0);
            l.ForeColor = Muted; l.Font = new Font("Segoe UI", 9f);
            l.Margin = new Padding(0, 2, 0, 6);
            return l;
        }
        CheckBox Check(string text, bool on)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Checked = on; c.AutoSize = true;
            return c;
        }
        ComboBox Combo(string[] labels, string[] values, string current)
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Width = 210;
            c.Items.AddRange(labels);
            c.Tag = values;
            c.SelectedIndex = Math.Max(0, Array.IndexOf(values, current));
            return c;
        }
        NumericUpDown Number(int value)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = 0; n.Maximum = 240; n.Width = 60;
            n.Value = Math.Max(0, Math.Min(240, value));
            return n;
        }
        Button Btn(string text)
        {
            Button b = new Button();
            b.Text = text; b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 66, 90);
            b.BackColor = Color.FromArgb(40, 26, 46);
            b.Margin = new Padding(0, 4, 8, 0);
            return b;
        }
        Control Row(string label, Control input, string unit)
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true; p.WrapContents = false;
            Label a = new Label(); a.Text = label; a.AutoSize = true; a.Margin = new Padding(0, 6, 8, 0);
            Label b = new Label(); b.Text = unit; b.AutoSize = true; b.Margin = new Padding(6, 6, 0, 0); b.ForeColor = Muted;
            p.Controls.Add(a); p.Controls.Add(input); p.Controls.Add(b);
            return p;
        }
    }
}
