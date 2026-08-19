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

            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : (fails + " TEST(S) FAILED"));
            Environment.Exit(fails == 0 ? 0 : 1);
        }
    }
}
