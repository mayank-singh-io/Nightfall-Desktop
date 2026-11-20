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

    // ---------------------------------------------------------------- sky manager
    static class SkyManager
    {
        static List<SkyForm> forms = new List<SkyForm>();
        public static bool Active { get { return forms.Count > 0; } }

        public static void Start(Settings s)
        {
            if (Active) return;
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
            BuildBackground();
            BuildExtras();
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
                using (SolidBrush star = new SolidBrush(Color.FromArgb((int)(190 * pal.Stars), 236, 228, 244)))
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
            float area = Width * Height;
            if (weather == "rain" || weather == "storm")
            {
                int n = (int)(area / (weather == "storm" ? 5500f : 9000f));
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
                using (Matrix m = new Matrix()) { m.Translate(Width / 2f, Height * 0.24f - tb.Y); time.Transform(m); }
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
                if (p != phase) { phase = p; BuildBackground(); BuildExtras(); }
            }
            if (IsPrimary && !lockedPc && settings.LockAfter > 0 && t > settings.LockAfter * 60000.0 && NowPlaying.Last == null)
            {
                lockedPc = true;
                NativeSky.LockWorkStation();
            }
            if (settings.SkyParallax)
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
                if (settings.SkyConstellations) UpdateConstellation(t);
            }
            if (clouds != null) cloudOffset = (cloudOffset + (float)dt * 9 * scale) % clouds.Width;
            if (aurora != null) auroraOffset = (auroraOffset + (float)dt * 6 * scale) % aurora.Width;
            if (IsPrimary && settings.SkyThoughts && t - thoughtChangedAt > 60000) { thought = Thoughts.Next(); thoughtChangedAt = t; BuildThoughtLayer(); }
            if (IsPrimary && thoughtLayer == null && !string.IsNullOrEmpty(thought)) BuildThoughtLayer();

            if (settings.SkyShooting && t > nextShot)
            {
                Streak s = new Streak();
                s.x = Width * (0.35f + (float)rng.NextDouble() * 0.6f); s.y = Height * (0.05f + (float)rng.NextDouble() * 0.25f);
                s.vx = -(260 + (float)rng.NextDouble() * 160) * scale; s.vy = (110 + (float)rng.NextDouble() * 60) * scale;
                s.tail = 0.25f; s.width = 1.6f; s.start = t; s.duration = 900;
                streaks.Add(s);
                nextShot = t + 7000 + rng.Next(9000);
            }
            if (settings.SkyShowers && t > nextShower)
            {
                StartShower(t);
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
            using (Pen p = new Pen(Color.FromArgb((int)(120 * a), 214, 200, 238), 1.1f * scale))
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
            using (SolidBrush glow = new SolidBrush(Color.FromArgb((int)(45 * a), 236, 228, 244)))
            using (SolidBrush star = new SolidBrush(Color.FromArgb((int)(255 * a), 248, 244, 255)))
            {
                foreach (PointF pt in activePts)
                {
                    float x = Wrap(pt.X + shift), r = 2.1f * scale;
                    g.FillEllipse(glow, x - r * 3, pt.Y - r * 3, r * 6, r * 6);
                    g.FillEllipse(star, x - r, pt.Y - r, r * 2, r * 2);
                }
            }
            if (labelFont == null) labelFont = new Font("Segoe UI Semilight", 11f);
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(170 * a), 203, 191, 214)))
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
            Graphics g = e.Graphics;
            double t = clock.Elapsed.TotalMilliseconds;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(background, 0, 0);
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

            if (weather == "rain" || weather == "storm")
            {
                using (Pen p = new Pen(Color.FromArgb(80, 205, 198, 228), 1))
                    foreach (Drop d in drops) g.DrawLine(p, d.x, d.y, d.x + d.len * 0.12f, d.y - d.len);
                if (flash > 0.01)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(36 * flash), 230, 225, 255)))
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
            twinkleBrush.Dispose();
            base.OnFormClosed(e);
            SkyManager.Closed(this);
        }
    }

    // ---------------------------------------------------------------- settings window
    class AwaySettingsForm : Form
    {
        readonly Settings settings;
        readonly Action onChange;
        NumericUpDown iconsMin, skyMin;
        CheckBox shooting, showers, moon, clockBox, thoughts, pauseMedia, playingBox, parallaxBox, constBox, hourlyBox;
        ComboBox weatherBox, colorsBox, extrasBox, moonBox;
        NumericUpDown lockMin;
        TextBox cityBox;
        Label status;

        static readonly Color Bg = Color.FromArgb(26, 15, 31);
        static readonly Color Fg = Color.FromArgb(236, 228, 244);
        static readonly Color Muted = Color.FromArgb(167, 150, 181);

        public AwaySettingsForm(Settings s, Action changed)
        {
            settings = s;
            onChange = changed;
            Text = "Moon Clock: away mode";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg; ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18);

            FlowLayoutPanel col = new FlowLayoutPanel();
            col.FlowDirection = FlowDirection.TopDown;
            col.WrapContents = false;
            col.AutoSize = true;
            Controls.Add(col);

            col.Controls.Add(Heading("When you're away"));
            iconsMin = Number(s.IconsAfter);
            col.Controls.Add(Row("Hide desktop icons after", iconsMin, "minutes"));
            skyMin = Number(s.SkyAfter);
            col.Controls.Add(Row("Show the night sky after", skyMin, "minutes"));
            col.Controls.Add(Hint("Set either one to 0 to turn it off. Moving the mouse or pressing a key brings everything back."));
            pauseMedia = Check("Don't show the sky while music or video is playing", s.PauseForMedia);
            col.Controls.Add(pauseMedia);
            lockMin = Number(s.LockAfter);
            col.Controls.Add(Row("Lock my PC after the sky has shown for", lockMin, "minutes"));

            col.Controls.Add(Heading("Look"));
            colorsBox = Combo(new string[] { "Always night (classic)", "Follow the time of day" }, new string[] { "night", "auto" }, s.SkyColors);
            col.Controls.Add(Row("Sky colors", colorsBox, ""));
            extrasBox = Combo(new string[] { "Nothing extra (classic)", "Drifting clouds", "Soft aurora", "Clouds and aurora" }, new string[] { "none", "clouds", "aurora", "both" }, s.SkyExtras);
            col.Controls.Add(Row("Extras", extrasBox, ""));
            moonBox = Combo(new string[] { "Small (classic)", "Real moon (NASA photo)" }, new string[] { "classic", "large" }, s.MoonStyle);
            col.Controls.Add(Row("Moon", moonBox, ""));
            parallaxBox = Check("Gentle parallax (stars, clouds and mountains drift)", s.SkyParallax); col.Controls.Add(parallaxBox);

            col.Controls.Add(Heading("In the sky"));
            shooting = Check("Shooting stars", s.SkyShooting); col.Controls.Add(shooting);
            showers = Check("Meteor showers now and then", s.SkyShowers); col.Controls.Add(showers);
            moon = Check("Tonight's moon", s.SkyMoon); col.Controls.Add(moon);
            clockBox = Check("Clock and date", s.SkyClock); col.Controls.Add(clockBox);
            thoughts = Check("A poetic line that changes every minute", s.SkyThoughts); col.Controls.Add(thoughts);
            playingBox = Check("What's playing, under the date", s.SkyPlaying); col.Controls.Add(playingBox);
            constBox = Check("Real constellations now and then", s.SkyConstellations); col.Controls.Add(constBox);
            hourlyBox = Check("A gentle shimmer at the top of each hour", s.SkyHourly); col.Controls.Add(hourlyBox);

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

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Margin = new Padding(0, 14, 0, 0);
            Button preview = Btn("Preview the sky");
            preview.Click += delegate { Apply(); SkyManager.Start(settings); };
            Button save = Btn("Save");
            save.Click += delegate { Apply(); Close(); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(preview);
            col.Controls.Add(buttons);
            AcceptButton = save;
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
            settings.LockAfter = (int)lockMin.Value;
            settings.SkyPlaying = playingBox.Checked;
            settings.SkyParallax = parallaxBox.Checked;
            settings.SkyConstellations = constBox.Checked;
            settings.SkyHourly = hourlyBox.Checked;
            settings.Save();
            WeatherService.Refresh(settings, false);
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
