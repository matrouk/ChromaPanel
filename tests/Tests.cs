using System;
using System.Collections.Generic;
using System.IO;

namespace ChromaPanel
{
    internal static class Tests
    {
        static int fails;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (!ok) fails++;
        }

        static void Main()
        {
            // ---- ramp maths ----
            var neutral = new ColourSet();
            ushort[] r = Ramp.Build(neutral);
            Check(r.Length == 768, "ramp is 768 entries");
            Check(r[0] == 0 && r[255] == 65535, "neutral R spans 0..65535");
            Check(r[256] == 0 && r[511] == 65535, "neutral G spans 0..65535");
            Check(r[512] == 0 && r[767] == 65535, "neutral B spans 0..65535");

            bool linear = true;
            for (int i = 0; i < 256; i++)
            {
                int expect = (int)Math.Round(i / 255.0 * 65535.0);
                if (Math.Abs(r[i] - expect) > 1) linear = false;
            }
            Check(linear, "neutral ramp is linear");

            // red-only edit must not disturb green or blue
            var redOnly = new ColourSet();
            redOnly.R.Brightness = 70;
            redOnly.R.Gamma = 1.6;
            ushort[] r2 = Ramp.Build(redOnly);
            bool gbUntouched = true, rChanged = false;
            for (int i = 0; i < 256; i++)
            {
                if (r2[256 + i] != r[256 + i] || r2[512 + i] != r[512 + i]) gbUntouched = false;
                if (r2[i] != r[i]) rChanged = true;
            }
            Check(gbUntouched, "red-only edit leaves G and B byte-identical");
            Check(rChanged, "red-only edit actually changes R");

            // clamping
            var extreme = new ColourSet();
            extreme.R.Brightness = 100; extreme.R.Contrast = 100;
            ushort[] r3 = Ramp.Build(extreme);
            bool inRange = true;
            for (int i = 0; i < 768; i++) if (r3[i] > 65535) inRange = false;
            Check(inRange, "extreme values stay in range");

            // ---- channel interaction (All vs R/G/B) ----
            char[] ALL = { 'R', 'G', 'B' };
            char[] RED = { 'R' };

            // Per-channel edit, then an unrelated slider moved in "All" mode.
            // The unrelated properties must survive.
            var mix = new ColourSet();
            mix.Set(Adjustable.Brightness, RED, 70);          // red-only tint
            mix.Set(Adjustable.Contrast, ALL, 60);            // then contrast on all
            Check(mix.R.Brightness == 70, "All-channel contrast leaves red brightness alone");
            Check(mix.G.Brightness == 50 && mix.B.Brightness == 50,
                  "All-channel contrast does not stamp brightness onto G and B");
            Check(mix.R.Contrast == 60 && mix.G.Contrast == 60 && mix.B.Contrast == 60,
                  "All-channel contrast reaches every channel");
            Check(mix.R.Gamma == 1.0 && mix.G.Gamma == 1.0 && mix.B.Gamma == 1.0,
                  "All-channel contrast leaves gamma alone");

            // A single-channel edit must not touch the other two.
            var one = new ColourSet();
            one.Set(Adjustable.Gamma, RED, 1.8);
            Check(one.R.Gamma == 1.8, "single-channel gamma applies");
            Check(one.G.Gamma == 1.0 && one.B.Gamma == 1.0, "single-channel gamma spares G and B");
            Check(one.R.Brightness == 50 && one.R.Contrast == 50,
                  "single-channel gamma spares that channel's other properties");

            // Levelling: moving a property in All mode makes just that one uniform.
            var lvl = new ColourSet();
            lvl.Set(Adjustable.Brightness, RED, 70);
            lvl.Set(Adjustable.Gamma, new[] { 'G' }, 1.5);
            Check(!lvl.Uniform(Adjustable.Brightness) && !lvl.Uniform(Adjustable.Gamma),
                  "Uniform reports both properties as differing");
            lvl.Set(Adjustable.Brightness, ALL, 55);
            Check(lvl.Uniform(Adjustable.Brightness), "All-mode brightness levels brightness");
            Check(!lvl.Uniform(Adjustable.Gamma), "...without levelling gamma");
            Check(lvl.G.Gamma == 1.5, "the per-channel gamma survives");

            // The ramp must reflect an isolated per-channel change.
            var tint = new ColourSet();
            tint.Set(Adjustable.Brightness, RED, 70);
            ushort[] tr = Ramp.Build(tint);
            ushort[] flat = Ramp.Build(new ColourSet());
            bool gbSame = true, rMoved = false;
            for (int i = 0; i < 256; i++)
            {
                if (tr[256 + i] != flat[256 + i] || tr[512 + i] != flat[512 + i]) gbSame = false;
                if (tr[i] != flat[i]) rMoved = true;
            }
            Check(gbSame && rMoved, "a red-only brightness tint shows up in R alone");

            // ---- blackout shortcut ----
            Hotkey hk = Hotkey.Parse("Ctrl+Alt+B");
            Check(hk.Key == System.Windows.Forms.Keys.B &&
                  hk.Modifiers == (Hotkey.MOD_CONTROL | Hotkey.MOD_ALT), "Ctrl+Alt+B parses");
            Check(hk.ToString() == "Ctrl+Alt+B", "Ctrl+Alt+B round-trips");
            Check(Hotkey.Parse("ctrl + shift + f9").ToString() == "Ctrl+Shift+F9", "parsing ignores case and spaces");
            Check(Hotkey.Parse("Ctrl+Alt+5").ToString() == "Ctrl+Alt+5", "digit keys round-trip");
            Check(Hotkey.Parse("Ctrl+=").ToString() == "Ctrl+=", "punctuation round-trips");
            Check(Hotkey.Parse("").IsEmpty && Hotkey.Parse("Ctrl+Nonsense").IsEmpty, "blank or bad text means no shortcut");
            Check(Hotkey.Parse("Pause").IsUsable && Hotkey.Parse("F13").IsUsable, "lone Pause / F-keys are allowed");
            Check(!Hotkey.Parse("B").IsUsable, "a lone letter is refused (too easy to hit while typing)");
            Check(!Hotkey.Parse("Ctrl").IsUsable, "a lone modifier is refused");

            // ---- blackout targeting ----
            var d1 = new MonitorInfo { DevicePath = @"\\?\DISPLAY#AAA1111#1&2&3&UID1#{x}", Bounds = new System.Drawing.Rectangle(-2560, 0, 2560, 1440) };
            var d2 = new MonitorInfo { DevicePath = @"\\?\DISPLAY#BBB2222#1&2&3&UID2#{x}", Bounds = new System.Drawing.Rectangle(0, 0, 2560, 1440) };
            var mons = new List<MonitorInfo> { d1, d2 };
            Check(Blackout.AreasFor(mons, "").Count == 2, "no target blacks out every display");
            var only2 = Blackout.AreasFor(mons, d2.Key);
            Check(only2.Count == 1 && only2[0].X == 0, "a target blacks out just that display");
            Check(Blackout.AreasFor(mons, "DISPLAY#GONE#0").Count == 2,
                  "a target that is no longer connected falls back to every display");

            // ---- JSON round trip ----
            var cs = new ColourSet();
            cs.R.Brightness = 61.5; cs.G.Contrast = 44; cs.B.Gamma = 1.35;
            cs.Vibrance = 73; cs.Hue = 200;
            ColourSet back = ColourSet.FromJson(Json.Read(Json.Write(cs.ToJson())));
            Check(back.R.Brightness == 61.5 && back.G.Contrast == 44 && back.B.Gamma == 1.35,
                  "ColourSet survives a JSON round trip");
            Check(back.Vibrance == 73 && back.Hue == 200, "vibrance and hue survive a round trip");

            // ---- legacy schema ----
            string legacy = "{\"s\":{\"G\":{\"C\":50,\"G\":1,\"B\":59},\"R\":{\"C\":50,\"G\":1,\"B\":59},"
                          + "\"Vib\":50,\"B\":{\"C\":50,\"G\":1,\"B\":59}}}";
            var root = Json.Read(legacy) as Dictionary<string, object>;
            Check(root != null && root.Count == 1, "legacy file parses");
            ColourSet old = ColourSet.FromJson(Json.Get(root, "s"));
            Check(old.R.Brightness == 59 && old.R.Contrast == 50 && old.R.Gamma == 1.0,
                  "legacy single-letter schema maps to the right fields");

            // ---- clamping on load ----
            ColourSet bad = ColourSet.FromJson(Json.Read(
                "{\"r\":{\"brightness\":9999,\"contrast\":-5,\"gamma\":99},\"vibrance\":500,\"hue\":-90}"));
            Check(bad.R.Brightness == 100 && bad.R.Contrast == 0 && bad.R.Gamma == 2.80,
                  "out-of-range values clamp on load");
            Check(bad.Vibrance == 100 && bad.Hue == 270, "vibrance clamps and hue wraps");

            // ---- damaged file must not throw ----
            bool threw = false;
            try { ColourSet.FromJson(Json.Read("{\"r\":")); } catch { threw = true; }
            Check(threw, "truncated JSON raises (and AppSettings.Load catches it)");

            // ---- settings round trip ----
            var s = new AppSettings();
            s.Profiles.Add(new Profile("Night", cs.Clone()));
            s.DefaultProfile = "Night";
            s.Monitors["DISPLAY#AAA1111#1&2&3&UID100"] = cs.Clone();
            s.Monitors["DISPLAY#BBB2222#1&2&3&UID200"] = new ColourSet();
            Check(s.Monitors.Count == 2, "two distinct monitor keys stay distinct");
            Check(s.Find("night") != null, "profile lookup is case-insensitive");
            Check(s.Find("nope") == null, "unknown profile returns null");

            // ---- a key this version does not know survives a save ----
            var newer = AppSettings.Parse("{\"profiles\":[{\"name\":\"A\",\"set\":{}}],\"futureThing\":{\"x\":7},\"desktopSharpenStrength\":55}");
            newer.DesktopSharpenStrength = 60;
            var saved = Json.Read(newer.ToText());
            Check(Json.Int(Json.Get(Json.Get(saved, "futureThing"), "x"), 0) == 7, "unknown keys are written back untouched");
            Check(Json.Int(Json.Get(saved, "desktopSharpenStrength"), 0) == 60, "known keys still take the new value");
            Check(Json.Get(Json.Read(new AppSettings().ToText()), "futureThing") == null, "a fresh settings object writes no stray keys");

            // ---- sharpening over fullscreen apps ----
            Check(new AppSettings().DesktopSharpenFullscreen, "fullscreen games are sharpened by default");
            Check(AppSettings.Parse("{}").DesktopSharpenFullscreen, "a 1.1.0 settings file (no key) gets the default");
            Check(!AppSettings.Parse("{\"desktopSharpenFullscreen\":false}").DesktopSharpenFullscreen, "turning it off is remembered");
            Check(!FullscreenRule.PausesFor("Polaris-Win64-Shipping", "UnrealWindow"), "a game keeps its sharpening");
            Check(!FullscreenRule.PausesFor("vlc", "Qt5QWindowIcon") && !FullscreenRule.PausesFor("mpv", "mpv"), "local video players keep it (no DRM)");
            Check(FullscreenRule.PausesFor("chrome", "Chrome_WidgetWin_1") && FullscreenRule.PausesFor("MSEDGE", "x")
                  && FullscreenRule.PausesFor("firefox", "MozillaWindowClass"), "browsers pause (DRM video would go black)");
            Check(FullscreenRule.PausesFor("ApplicationFrameHost", "") && FullscreenRule.PausesFor("", "ApplicationFrameWindow"),
                  "Store apps pause (Netflix, Disney+)");
            Check(!FullscreenRule.PausesFor("", "") && !FullscreenRule.PausesFor(null, "UnrealWindow"), "an unreadable process is treated as a game");
            Check(!FullscreenRule.PausesFor("chromapanel", "") && !FullscreenRule.PausesFor("ChromeDriverHelper", ""), "only whole names match");

            // ---- startup entry ----
            Check(AppSettings.RunTarget("\"C:\\A B\\ChromaPanel.exe\" --tray") == "C:\\A B\\ChromaPanel.exe", "quoted Run value: path without args");
            Check(AppSettings.RunTarget("C:\\Tools\\ChromaPanel.exe --tray") == "C:\\Tools\\ChromaPanel.exe", "unquoted Run value: path without args");
            Check(AppSettings.RunTarget("") == "" && AppSettings.RunTarget(null) == "", "no Run value, no target");
            var v100 = new Version(1, 0, 0, 0);
            var v110 = new Version(1, 1, 0, 0);
            var v120 = new Version(1, 2, 0, 0);
            string me = "C:\\New\\ChromaPanel.exe", stale = "C:\\Downloads\\ChromaPanel.exe";
            Check(AppSettings.ShouldRepoint(stale, true, v100, me, v110), "an older copy in the startup entry is replaced");
            Check(AppSettings.ShouldRepoint(stale, false, null, me, v110), "a deleted copy in the startup entry is replaced");
            Check(!AppSettings.ShouldRepoint(stale, true, v120, me, v110), "a newer copy in the startup entry is kept");
            Check(!AppSettings.ShouldRepoint(stale, true, v110, me, v110), "an equal copy elsewhere is kept");
            Check(!AppSettings.ShouldRepoint("c:\\new\\chromapanel.exe", true, v100, me, v110), "the entry already naming this copy is left alone");
            Check(!AppSettings.ShouldRepoint("C:\\Other\\tool.exe", true, null, me, v110), "a non-ChromaPanel target is left alone");
            Check(AppSettings.CurrentVersion.Revision == 0 && AppSettings.CurrentVersion > v100,
                  "this build's version has four parts, so 1.1.1 does not sort below 1.1.1.0");

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : (fails + " TEST(S) FAILED"));
            Environment.Exit(fails == 0 ? 0 : 1);
        }
    }
}
