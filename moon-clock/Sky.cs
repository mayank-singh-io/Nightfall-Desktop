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

        public static void Refresh(Settings s, bool force)
        {
            if (double.IsNaN(s.Lat)) return;
            if (!force && (DateTime.Now - fetched).TotalMinutes < 30) return;
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
        public string Dir, Rain = "none", Snow = "none", FrontFile;
        public bool NoShoot, LifeUnder;
        public List<string[]> Sprites = new List<string[]>();
        public List<float[]> SpriteSmoke = new List<float[]>(), SpriteGlow = new List<float[]>(), SpriteWheel = new List<float[]>();
        public List<float[]> Pulses = new List<float[]>(), GlassDrops = new List<float[]>(), Water = new List<float[]>(), Curtains = new List<float[]>();
        public float SatelliteEvery, DebrisEvery, AstronautEvery, PlaneEvery, ShotEvery;
        public bool NoDawn;
        public string DawnFile, DawnFront;
        public List<float[]> Falls = new List<float[]>();
        public List<string[]> Sways = new List<string[]>();
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
                d.Dir = dir;
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
        class Part { public float X, Y, VX, VY, Size, Age, Life, Seed; public int Kind; }   // 0 smoke, 1 sand, 2 puddle ripple

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

        // mouse position as -1..1 across the screen; the scene shifts a little the other way
        public void SetPointer(float nx, float ny) { if (Parallax) { parTX = -nx * 12 * ui; parTY = -ny * 7 * ui; } else { parTX = parTY = 0; } }

        // a click at screen coordinates; returns true when it did something
        public bool Click(float x, float y, double t)
        {
            x -= parX; y -= parY;
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
                if (q.Age > q.Life) { parts.RemoveAt(i); continue; }
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
                float lim = 18 * ui;
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
                if (mv[1] < -200 || mv[1] > w + 200 || t - mv[5] > mv[6]) movers.RemoveAt(i);
            }
            parX += (parTX - parX) * Math.Min(1, fdt * 3); parY += (parTY - parY) * Math.Min(1, fdt * 3);
            // shooting stars
            if (AllowShootingStars && !d.NoShoot && dawnAmt < 0.4f && t > nextShot) { AddStreak(t, false); nextShot = t + (d.ShotEvery > 0 ? d.ShotEvery * (0.6 + rng.NextDouble() * 0.8) : 7000 + rng.Next(9000)); }
            for (int i = streaks.Count - 1; i >= 0; i--) if (t > streaks[i][4] + streaks[i][5]) streaks.RemoveAt(i);
        }

        public void Paint(Graphics g, double t)
        {
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            System.Drawing.Drawing2D.Matrix saved = g.Transform;
            g.TranslateTransform(parX, parY);
            g.DrawImageUnscaled(CurrentBg, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Region old = g.Clip;
            g.SetClip(skyRegion, CombineMode.Intersect);
            double since = t - shimmerStart;
            foreach (float[] s in twinkles)
            {
                double boost = since >= 0 && since < 7000 ? Math.Sin(Math.PI * since / 7000) * (0.5 + 0.5 * Math.Sin(t / 160 + s[3] * 3)) : 0;
                if (dawnAmt > 0.9f) break;
                int a = (int)(255 * (1 - dawnAmt) * Math.Min(1, 0.55 + 0.45 * Math.Sin(t / 1000 * s[4] + s[3]) + boost * 0.6));
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
            foreach (Drift dr in drifts) if (dr.AfterFront) DrawDrift(g, dr, t);
            if (dawnAmt < 0.7f) foreach (Fade fd in fades) if (fd.AfterFront) DrawFade(g, fd, t);
            if (d.LifeUnder) DrawLife(g, t, 1);
            else { DrawLife(g, t, 2); DrawDrops(g); }
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
                    g.DrawImage(sy.Bmp, new RectangleF(ox + dx, sy.Y + i * rh, sy.Bmp.Width, hgt), new RectangleF(0, i * rh, sy.Bmp.Width, hgt), GraphicsUnit.Pixel);
                }
            }
        }

        void DrawMovers(Graphics g, double t)
        {
            foreach (float[] mv in movers)
            {
                float x = mv[1], y = mv[2];
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
            if (t - fd.LastMix > 160)   // re-blend a few times a second; the aurora changes slowly
            {
                fd.LastMix = t;
                int n = fd.Frames.Count;
                double pos = (t / 1000.0 / Math.Max(1f, fd.Period)) % n;
                int i = (int)pos, j = (i + 1) % n;
                float a = (float)(pos - i); a = a * a * (3 - 2 * a);   // ease in and out
                using (Graphics mg = Graphics.FromImage(fd.Mix))
                {
                    mg.Clear(Color.Transparent);
                    mg.CompositingMode = CompositingMode.SourceOver;
                    DrawWithAlpha(mg, fd.Frames[i], 1 - a);
                    DrawWithAlpha(mg, fd.Frames[j], a);
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

        void DrawStreaks(Graphics g, double t)
        {
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
                GlowSprite.Draw(g, p.X, p.Y, rr * 4.5f, (int)L[3], (int)L[4], (int)L[5], 0.25f + 0.3f * (float)f);
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
                GlowSprite.Draw(g, fx[i], fy[i], r * 7, 200, 255, 120, 0.4f * a);
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
                if (sd != null) { sceneR = new SceneRenderer(); sceneR.AllowShootingStars = settings.SkyShooting; sceneR.TrainGapSeconds = settings.TrainGap(); sceneR.Parallax = false; sceneR.ClickStars = sceneR.ClickLights = sceneR.ClickWater = false; sceneR.Build(sd, Width, Height, scale); weather = "clear";  sceneR.SetDawn(SunClock.DawnAmount(settings, DateTime.Now), true);}
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
            wallSceneValue = Array.IndexOf(SceneData.Keys, s.WallScene) >= 0 ? s.WallScene : "aurora";
            Control wallPick = PickerRow("Scene", wallSceneValue, false, delegate(string k) { wallSceneValue = k; });
            col.Controls.Add(wallPick);
            wallBattery = Check("Pause when running on battery", s.WallPauseOnBattery); col.Controls.Add(wallBattery);
            wallSaver = Check("Pause when battery saver is on", s.WallPauseSaver); col.Controls.Add(wallSaver);
            wallBelow = Number(s.WallPauseBelow); wallBelow.Maximum = 100;
            col.Controls.Add(Row("Pause when battery is below", wallBelow, "% (0 = never)"));
            col.Controls.Add(Hint("On empty desktop space:"));
            wallStars = Check("Click the sky for a shooting star", s.WallClickStars); col.Controls.Add(wallStars);
            wallLights = Check("Click windows and lamps to switch them on or off", s.WallClickLights); col.Controls.Add(wallLights);
            wallWater = Check("Click water for ripples", s.WallClickWater); col.Controls.Add(wallWater);
            wallParallax = Check("Move the mouse to gently shift the view", s.WallParallax); col.Controls.Add(wallParallax);
            EventHandler wallSync = delegate { wallPick.Enabled = wallModeBox.SelectedIndex == 0; };
            wallModeBox.SelectedIndexChanged += wallSync; wallSync(null, null);

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
