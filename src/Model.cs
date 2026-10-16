using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ChromaPanel
{
    /// <summary>Brightness / contrast / gamma for one colour channel.</summary>
    public class Adjust
    {
        public double Brightness = 50.0;   // 0..100, 50 = neutral
        public double Contrast = 50.0;     // 0..100, 50 = neutral
        public double Gamma = 1.0;         // 0.30..2.80, 1.0 = neutral

        public Adjust Clone()
        {
            return new Adjust { Brightness = Brightness, Contrast = Contrast, Gamma = Gamma };
        }

        public bool Equals(Adjust o)
        {
            return o != null && Brightness == o.Brightness && Contrast == o.Contrast && Gamma == o.Gamma;
        }

        public bool IsNeutral
        {
            get { return Brightness == 50.0 && Contrast == 50.0 && Math.Abs(Gamma - 1.0) < 0.0001; }
        }
    }

    /// <summary>Which of the three per-channel curves a slider drives.</summary>
    public enum Adjustable { Brightness, Contrast, Gamma }

    /// <summary>Desktop filters. Values are stored in settings, so append only.</summary>
    public enum SharpenFilter { Cas = 0, Rcas = 1, Luma = 2, Adaptive = 3, Anime4K = 4 }

    /// <summary>A complete look: three channels plus the two NVAPI values.</summary>
    public class ColourSet
    {
        public Adjust R = new Adjust();
        public Adjust G = new Adjust();
        public Adjust B = new Adjust();
        public int Vibrance = 50;
        public int Hue = 0;

        public Adjust this[char ch]
        {
            get
            {
                if (ch == 'R') return R;
                if (ch == 'G') return G;
                return B;
            }
        }

        public ColourSet Clone()
        {
            return new ColourSet { R = R.Clone(), G = G.Clone(), B = B.Clone(), Vibrance = Vibrance, Hue = Hue };
        }

        /// <summary>
        /// Writes ONE property to the given channels and leaves the rest alone.
        ///
        /// This is deliberately narrow. Writing all three properties whenever
        /// any slider moved meant that, with "All channels" selected, nudging
        /// contrast also stamped the brightness and gamma shown on screen
        /// (which are read from red) onto green and blue - silently destroying
        /// any per-channel adjustment.
        /// </summary>
        public void Set(Adjustable p, char[] channels, double value)
        {
            if (channels == null) return;
            foreach (char c in channels)
            {
                Adjust a = this[c];
                switch (p)
                {
                    case Adjustable.Brightness: a.Brightness = value; break;
                    case Adjustable.Contrast: a.Contrast = value; break;
                    case Adjustable.Gamma: a.Gamma = value; break;
                }
            }
        }

        public double Get(Adjustable p, char channel)
        {
            Adjust a = this[channel];
            switch (p)
            {
                case Adjustable.Brightness: return a.Brightness;
                case Adjustable.Contrast: return a.Contrast;
                default: return a.Gamma;
            }
        }

        /// <summary>True when this one property is the same on all three channels.</summary>
        public bool Uniform(Adjustable p)
        {
            return Get(p, 'R') == Get(p, 'G') && Get(p, 'G') == Get(p, 'B');
        }

        public bool IsNeutral
        {
            get { return R.IsNeutral && G.IsNeutral && B.IsNeutral && Vibrance == 50 && Hue == 0; }
        }

        public bool ChannelsMatch
        {
            get { return R.Equals(G) && G.Equals(B); }
        }

        public object ToJson()
        {
            var d = new Dictionary<string, object>();
            d["r"] = Chan(R);
            d["g"] = Chan(G);
            d["b"] = Chan(B);
            d["vibrance"] = Vibrance;
            d["hue"] = Hue;
            return d;
        }

        static object Chan(Adjust a)
        {
            var d = new Dictionary<string, object>();
            d["brightness"] = a.Brightness;
            d["contrast"] = a.Contrast;
            d["gamma"] = a.Gamma;
            return d;
        }

        public static ColourSet FromJson(object o)
        {
            var cs = new ColourSet();
            if (o == null) return cs;
            cs.R = ChanFrom(Json.Get(o, "r") ?? Json.Get(o, "R"));
            cs.G = ChanFrom(Json.Get(o, "g") ?? Json.Get(o, "G"));
            cs.B = ChanFrom(Json.Get(o, "b") ?? Json.Get(o, "B"));
            cs.Vibrance = Json.Int(Json.Get(o, "vibrance") ?? Json.Get(o, "Vib"), 50);
            cs.Hue = Json.Int(Json.Get(o, "hue"), 0);
            cs.Clamp();
            return cs;
        }

        static Adjust ChanFrom(object o)
        {
            var a = new Adjust();
            if (o == null) return a;
            // Long names are the current schema; single letters are the old
            // PowerShell schema (B = brightness, C = contrast, G = gamma).
            a.Brightness = Json.Num(Json.Get(o, "brightness") ?? Json.Get(o, "B"), 50.0);
            a.Contrast = Json.Num(Json.Get(o, "contrast") ?? Json.Get(o, "C"), 50.0);
            a.Gamma = Json.Num(Json.Get(o, "gamma") ?? Json.Get(o, "G"), 1.0);
            return a;
        }

        public void Clamp()
        {
            foreach (char c in new[] { 'R', 'G', 'B' })
            {
                Adjust a = this[c];
                a.Brightness = Clamp(a.Brightness, 0, 100);
                a.Contrast = Clamp(a.Contrast, 0, 100);
                a.Gamma = Clamp(a.Gamma, 0.30, 2.80);
            }
            Vibrance = (int)Clamp(Vibrance, 0, 100);
            Hue = ((Hue % 360) + 360) % 360;
        }

        static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return lo;
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    public class Profile
    {
        public string Name = "";
        public ColourSet Set = new ColourSet();

        public Profile() { }
        public Profile(string name, ColourSet set) { Name = name; Set = set; }
    }

    /// <summary>Turns a ColourSet into the 768-entry ramp GDI wants.</summary>
    public static class Ramp
    {
        public static ushort[] Build(ColourSet s)
        {
            var ramp = new ushort[768];
            char[] order = { 'R', 'G', 'B' };
            for (int c = 0; c < 3; c++)
            {
                Adjust a = s[order[c]];
                double bOff = (a.Brightness - 50.0) / 100.0;
                double cMul = a.Contrast / 50.0;
                double invG = 1.0 / (a.Gamma <= 0 ? 1.0 : a.Gamma);
                int baseIdx = c * 256;
                for (int i = 0; i < 256; i++)
                {
                    double v = Math.Pow(i / 255.0, invG);
                    v = (v - 0.5) * cMul + 0.5 + bOff;
                    if (v < 0.0) v = 0.0;
                    if (v > 1.0) v = 1.0;
                    ramp[baseIdx + i] = (ushort)Math.Round(v * 65535.0);
                }
            }
            return ramp;
        }
    }

    /// <summary>Everything that gets persisted. Written automatically, never by a Save button.</summary>
    public class AppSettings
    {
        public const string Version = "1.1.0";

        public List<Profile> Profiles = new List<Profile>();
        public string DefaultProfile = "";
        public bool ApplyOnLaunch = true;
        public bool ReapplyOnDisplayChange = true;
        public bool MinimiseToTray = true;
        public bool StartMinimised;
        public bool LinkAllMonitors;
        public string LastMonitorKey = "";
        public string LastProfile = "";

        // Blackout (OLED screen-off that coexists with OBS)
        public string BlackoutHotkey = "Ctrl+Alt+B";
        public string BlackoutTarget = "";          // "" = every display, else a monitor key
        public bool BlackoutHideFromCapture = true; // keep OBS recording what is underneath

        // Desktop sharpening (overlay, see DesktopSharpen.cs)
        public bool DesktopSharpenEnabled;
        public string DesktopSharpenFilter = "cas";  // cas | rcas | luma | adaptive | anime4k
        public int DesktopSharpenStrength = 40;      // 0..100
        public string DesktopSharpenTarget = "";     // "" = every display, else a monitor key

        static readonly string[] FilterNames = { "cas", "rcas", "luma", "adaptive", "anime4k" };

        /// <summary>Unknown names (a newer or hand-edited file) fall back to CAS.</summary>
        public SharpenFilter DesktopFilter
        {
            get
            {
                for (int i = 0; i < FilterNames.Length; i++)
                    if (string.Equals(DesktopSharpenFilter, FilterNames[i], StringComparison.OrdinalIgnoreCase))
                        return (SharpenFilter)i;
                return SharpenFilter.Cas;
            }
            set
            {
                int i = (int)value;
                DesktopSharpenFilter = i >= 0 && i < FilterNames.Length ? FilterNames[i] : "cas";
            }
        }

        // Live per-monitor values, keyed by MonitorInfo.Key.
        public Dictionary<string, ColourSet> Monitors = new Dictionary<string, ColourSet>(StringComparer.OrdinalIgnoreCase);
        // Human-readable name for each key, so the file stays legible.
        public Dictionary<string, string> MonitorNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Which preset each display is on, so switching displays switches preset too.
        public Dictionary<string, string> MonitorProfile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ChromaPanel");
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public Profile Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Profile p in Profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>
        /// Which preset this display is on. Falls back to the default preset,
        /// which is what a display the user has never touched should show.
        /// </summary>
        public string ProfileForMonitor(string key)
        {
            string n;
            if (!string.IsNullOrEmpty(key) && MonitorProfile.TryGetValue(key, out n) && Find(n) != null)
                return n;
            if (Find(DefaultProfile) != null) return DefaultProfile;
            return Profiles.Count > 0 ? Profiles[0].Name : null;
        }

        public ColourSet ForMonitor(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "*";
            ColourSet s;
            if (!Monitors.TryGetValue(key, out s))
            {
                s = new ColourSet();
                Monitors[key] = s;
            }
            return s;
        }

        // ------------------------------------------------------------------
        public static AppSettings Load()
        {
            var s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    s.Read(File.ReadAllText(FilePath, Encoding.UTF8));
                }
                else
                {
                    // First run: pull profiles across from the PowerShell build.
                    string legacy = LegacyProfilesPath();
                    if (legacy != null) s.ReadLegacy(File.ReadAllText(legacy, Encoding.UTF8));
                }
            }
            catch { /* a damaged file must never stop the app opening */ }

            if (s.Profiles.Count == 0)
                s.Profiles.Add(new Profile("Default", new ColourSet()));
            if (s.Find(s.DefaultProfile) == null)
                s.DefaultProfile = s.Profiles[0].Name;
            return s;
        }

        static string LegacyProfilesPath()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                foreach (string name in new[] { "profiles.json" })
                {
                    string p = Path.Combine(exeDir, name);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        void Read(string text)
        {
            object root = Json.Read(text);

            var profs = Json.Get(root, "profiles") as List<object>;
            if (profs != null)
            {
                foreach (object p in profs)
                {
                    string n = Json.Str(Json.Get(p, "name"), null);
                    if (string.IsNullOrEmpty(n) || Find(n) != null) continue;
                    Profiles.Add(new Profile(n, ColourSet.FromJson(Json.Get(p, "set"))));
                }
            }

            DefaultProfile = Json.Str(Json.Get(root, "defaultProfile"), "");
            LastProfile = Json.Str(Json.Get(root, "lastProfile"), "");
            LastMonitorKey = Json.Str(Json.Get(root, "lastMonitorKey"), "");
            ApplyOnLaunch = Json.Bool(Json.Get(root, "applyOnLaunch"), true);
            ReapplyOnDisplayChange = Json.Bool(Json.Get(root, "reapplyOnDisplayChange"), true);
            MinimiseToTray = Json.Bool(Json.Get(root, "minimiseToTray"), true);
            StartMinimised = Json.Bool(Json.Get(root, "startMinimised"), false);
            LinkAllMonitors = Json.Bool(Json.Get(root, "linkAllMonitors"), false);
            // A missing key means "default shortcut"; an empty string means the user cleared it.
            object hk = Json.Get(root, "blackoutHotkey");
            BlackoutHotkey = hk == null ? "Ctrl+Alt+B" : (hk as string ?? "");
            BlackoutTarget = Json.Str(Json.Get(root, "blackoutTarget"), "");
            BlackoutHideFromCapture = Json.Bool(Json.Get(root, "blackoutHideFromCapture"), true);
            DesktopSharpenEnabled = Json.Bool(Json.Get(root, "desktopSharpenEnabled"), false);
            DesktopSharpenFilter = Json.Str(Json.Get(root, "desktopSharpenFilter"), "cas");
            DesktopSharpenStrength = Math.Max(0, Math.Min(100, Json.Int(Json.Get(root, "desktopSharpenStrength"), 40)));
            DesktopSharpenTarget = Json.Str(Json.Get(root, "desktopSharpenTarget"), "");

            var mons = Json.Get(root, "monitors") as Dictionary<string, object>;
            if (mons != null)
                foreach (KeyValuePair<string, object> kv in mons)
                    Monitors[kv.Key] = ColourSet.FromJson(kv.Value);

            var names = Json.Get(root, "monitorNames") as Dictionary<string, object>;
            if (names != null)
                foreach (KeyValuePair<string, object> kv in names)
                    MonitorNames[kv.Key] = Json.Str(kv.Value, "");

            var mp = Json.Get(root, "monitorProfile") as Dictionary<string, object>;
            if (mp != null)
                foreach (KeyValuePair<string, object> kv in mp)
                    MonitorProfile[kv.Key] = Json.Str(kv.Value, "");
        }

        /// <summary>Old schema: the whole file was { "ProfileName": { R/G/B/Vib } }.</summary>
        void ReadLegacy(string text)
        {
            var root = Json.Read(text) as Dictionary<string, object>;
            if (root == null) return;
            foreach (KeyValuePair<string, object> kv in root)
            {
                if (string.IsNullOrEmpty(kv.Key) || Find(kv.Key) != null) continue;
                Profiles.Add(new Profile(kv.Key, ColourSet.FromJson(kv.Value)));
            }
            if (Profiles.Count > 0) DefaultProfile = Profiles[0].Name;
        }

        public string Save()
        {
            try
            {
                var root = new Dictionary<string, object>();

                var list = new List<object>();
                foreach (Profile p in Profiles)
                {
                    var d = new Dictionary<string, object>();
                    d["name"] = p.Name;
                    d["set"] = p.Set.ToJson();
                    list.Add(d);
                }
                root["profiles"] = list;
                root["defaultProfile"] = DefaultProfile ?? "";
                root["lastProfile"] = LastProfile ?? "";
                root["lastMonitorKey"] = LastMonitorKey ?? "";
                root["applyOnLaunch"] = ApplyOnLaunch;
                root["reapplyOnDisplayChange"] = ReapplyOnDisplayChange;
                root["minimiseToTray"] = MinimiseToTray;
                root["startMinimised"] = StartMinimised;
                root["linkAllMonitors"] = LinkAllMonitors;
                root["blackoutHotkey"] = BlackoutHotkey ?? "";
                root["blackoutTarget"] = BlackoutTarget ?? "";
                root["blackoutHideFromCapture"] = BlackoutHideFromCapture;
                root["desktopSharpenEnabled"] = DesktopSharpenEnabled;
                root["desktopSharpenFilter"] = DesktopSharpenFilter ?? "cas";
                root["desktopSharpenStrength"] = DesktopSharpenStrength;
                root["desktopSharpenTarget"] = DesktopSharpenTarget ?? "";

                var mons = new Dictionary<string, object>();
                foreach (KeyValuePair<string, ColourSet> kv in Monitors) mons[kv.Key] = kv.Value.ToJson();
                root["monitors"] = mons;

                var names = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in MonitorNames) names[kv.Key] = kv.Value;
                root["monitorNames"] = names;

                var mp = new Dictionary<string, object>();
                foreach (KeyValuePair<string, string> kv in MonitorProfile) mp[kv.Key] = kv.Value;
                root["monitorProfile"] = mp;

                Directory.CreateDirectory(Dir);
                // Write to a temp file first so a crash mid-write cannot leave
                // a truncated settings file behind.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Write(root), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ------------------------------------------------------------------
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "ChromaPanel";

        public static bool GetStartWithWindows()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        public static string SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return "cannot open Run key";
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        k.SetValue(RunValue, "\"" + exe + "\" --tray");
                    }
                    else if (k.GetValue(RunValue) != null)
                    {
                        k.DeleteValue(RunValue, false);
                    }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Describe(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }
}
