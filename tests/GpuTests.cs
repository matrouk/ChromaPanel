using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ChromaPanel
{
    /// <summary>
    /// Runs every desktop filter through Direct3D 11 and checks the output.
    ///
    ///  - CAS, RCAS, Luma: compared byte for byte with a CPU version of the
    ///    same maths.
    ///  - Anime4K: compared with a CPU network built by parsing the ORIGINAL
    ///    GLSL (third_party/anime4k), independently of tools/gen_anime4k.py,
    ///    so a translation mistake in the generator fails here.
    ///  - AdaptiveSharpen: behaviour checks (edges sharpen, flat areas stay,
    ///    overshoot stays bounded) plus GPU-versus-WARP agreement.
    ///
    /// Uses the GPU if there is one, otherwise Microsoft's WARP software
    /// rasteriser, so it also runs on a CI machine.
    ///
    ///   GpuTests.exe [--warp] [path\to\Anime4K_Restore_CNN_M.glsl]
    /// </summary>
    internal static class GpuTests
    {
        static int fails;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (!ok) fails++;
        }

        const int W = 64, H = 48;

        sealed class Gpu
        {
            public IntPtr Dev, CtxPtr;
            public Dx.Context Ctx;
            public Sharpener Sh;
            public string Name;
        }

        static int Main(string[] args)
        {
            bool forceWarp = Array.IndexOf(args, "--warp") >= 0;
            string glsl = null;
            foreach (string a in args) if (a.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase)) glsl = a;
            if (glsl == null)
            {
                string exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                glsl = Path.Combine(exeDir, @"..\third_party\anime4k\Anime4K_Restore_CNN_M.glsl");
            }

            Gpu main = Open(forceWarp);
            Gpu warp = main.Name.StartsWith("WARP") ? null : Open(true);
            Console.WriteLine("device: " + main.Name + (warp != null ? "   (+ WARP for cross-checks)" : ""));

            byte[] img = MakeImage();

            // ---------------- CAS / RCAS ----------------
            foreach (SharpenFilter f in new[] { SharpenFilter.Cas, SharpenFilter.Rcas })
            {
                Check(MaxDiff(Run(main, img, f, 0f), img) == 0, f + " at 0 is an exact pass-through");
                foreach (float s in new[] { 0.5f, 1.0f })
                {
                    int d = MaxDiff(Run(main, img, f, s), Cpu(img, f, s));
                    Check(d <= 2, f + " at " + s.ToString("0.0", CultureInfo.InvariantCulture) + " matches the CPU reference (max diff " + d + "/255)");
                }
                CommonBehaviour(main, img, f, 1f);
            }

            // ---------------- Luma ----------------
            Check(MaxDiff(Run(main, img, SharpenFilter.Luma, 0f), img) == 0, "Luma at 0 is an exact pass-through");
            foreach (float s in new[] { 0.5f, 1.0f })
            {
                int d = MaxDiff(Run(main, img, SharpenFilter.Luma, s), Cpu(img, SharpenFilter.Luma, s));
                Check(d <= 2, "Luma at " + s.ToString("0.0", CultureInfo.InvariantCulture) + " matches the CPU reference (max diff " + d + "/255)");
            }
            Check(SameShiftOnAllChannels(img, Run(main, img, SharpenFilter.Luma, 1f)),
                  "Luma moves R, G and B by the same amount (no colour fringes)");
            CommonBehaviour(main, img, SharpenFilter.Luma, 1f);

            // ---------------- AdaptiveSharpen ----------------
            {
                byte[] a0 = Run(main, img, SharpenFilter.Adaptive, 0f);
                int d0 = MaxDiff(a0, img);
                // Not an exact pass-through by design: sharpdiff carries a fixed
                // (d0 - neg_laplace) * 0.01 floor, worth up to ~2.5/255 on a hard edge
                // plus the saturation compensation. The app never shows it - the
                // overlay switches off entirely at 0%.
                Check(d0 <= 3, "Adaptive at 0 stays within its built-in 0.01 floor (max " + d0 + "/255)");
                CommonBehaviour(main, img, SharpenFilter.Adaptive, 0.5f);
                int over = EdgeOvershoot(img, Run(main, img, SharpenFilter.Adaptive, 1f));
                Check(over <= 8, "Adaptive anti-ringing keeps overshoot on a hard edge small (" + over + "/255)");
            }

            // ---------------- Anime4K ----------------
            if (File.Exists(glsl))
            {
                var net = A4KNet.Parse(File.ReadAllText(glsl));
                Check(net.Passes.Count == Anime4K.Entries.Length,
                      "generated shader has the same " + net.Passes.Count + " passes as the GLSL");
                Check(MaxDiff(Run(main, img, SharpenFilter.Anime4K, 0f), img) == 0, "Anime4K at 0 is an exact pass-through");
                foreach (float s in new[] { 0.5f, 1.0f })
                {
                    int d = MaxDiff(Run(main, img, SharpenFilter.Anime4K, s), net.Run(img, W, H, s * 2f));
                    Check(d <= 2, "Anime4K at " + s.ToString("0.0", CultureInfo.InvariantCulture) + " matches a CPU network parsed from the original GLSL (max diff " + d + "/255)");
                }
                Check(EdgeEnergy(Run(main, img, SharpenFilter.Anime4K, 1f)) != EdgeEnergy(img), "Anime4K changes the picture");
            }
            else
            {
                Console.WriteLine("  FAIL  Anime4K GLSL not found at " + glsl);
                fails++;
            }

            // ---------------- hardware vs WARP ----------------
            if (warp != null)
            {
                foreach (SharpenFilter f in Enum.GetValues(typeof(SharpenFilter)))
                {
                    int d = MaxDiff(Run(main, img, f, 0.75f), Run(warp, img, f, 0.75f));
                    Check(d <= 2, f + ": GPU and WARP agree (max diff " + d + "/255)");
                }
            }

            // Timing only means something on a real GPU. CI runners have none, and
            // there even a 'hardware' device can be the software Basic Render Driver.
            bool ci = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
            if (!main.Name.StartsWith("WARP") && !ci) Perf(main);

            Close(main);
            if (warp != null) Close(warp);
            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "ALL GPU TESTS PASSED" : fails + " GPU TEST(S) FAILED");
            return fails == 0 ? 0 : 1;
        }

        static void CommonBehaviour(Gpu g, byte[] img, SharpenFilter f, float s)
        {
            byte[] o = Run(g, img, f, s);
            Check(EdgeEnergy(o) > EdgeEnergy(img), f + " increases edge contrast");
            Check(RegionUnchanged(img, o, 48, 2, 60, 9), f + " leaves a flat black area untouched");
            Check(RegionUnchanged(img, o, 48, 14, 60, 21), f + " leaves a flat white area untouched");
        }

        static Gpu Open(bool warp)
        {
            var g = new Gpu();
            if (warp) { g.Dev = Dx.CreateWarpDevice(out g.CtxPtr); g.Name = "WARP (software)"; }
            else
            {
                try { g.Dev = Dx.CreateDevice(IntPtr.Zero, out g.CtxPtr); g.Name = "hardware GPU"; }
                catch (DxException) { g.Dev = Dx.CreateWarpDevice(out g.CtxPtr); g.Name = "WARP (software, no GPU found)"; }
            }
            g.Ctx = new Dx.Context(g.CtxPtr);
            g.Sh = new Sharpener(g.Dev, g.Ctx);
            return g;
        }

        static void Close(Gpu g)
        {
            g.Sh.Dispose();
            Dx.Release(ref g.CtxPtr);
            Dx.Release(ref g.Dev);
        }

        // ------------------------------------------------------------------
        // Test image (BGRA): noise, a hard edge, a soft gradient, flat black,
        // flat white and saturated primaries - the cases a sharpener gets wrong.
        static byte[] MakeImage()
        {
            var rnd = new Random(12345);
            var b = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int r, g, bl;
                    if (x < 22) { r = rnd.Next(256); g = rnd.Next(256); bl = rnd.Next(256); }
                    else if (x < 44)
                    {
                        if (y < 24) { int v = x < 33 ? 30 : 220; r = g = bl = v; }               // hard edge
                        else { int v = (x - 22) * 255 / 21; r = v; g = 255 - v; bl = 128; }    // gradient
                    }
                    else
                    {
                        if (y < 12) r = g = bl = 0;                       // black
                        else if (y < 24) r = g = bl = 255;                // white
                        else if (y < 32) { r = 255; g = 0; bl = 0; }
                        else if (y < 40) { r = 0; g = 255; bl = 0; }
                        else { r = 0; g = 0; bl = 255; }
                    }
                    int o = (y * W + x) * 4;
                    b[o] = (byte)bl; b[o + 1] = (byte)g; b[o + 2] = (byte)r; b[o + 3] = 255;
                }
            return b;
        }

        // ------------------------------------------------------------------
        static byte[] Run(Gpu g, byte[] img, SharpenFilter f, float s)
        {
            var d = new Dx.TEXTURE2D_DESC
            {
                Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Dx.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1, Usage = Dx.USAGE_DEFAULT, BindFlags = Dx.BIND_SHADER_RESOURCE
            };
            IntPtr src = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            d.BindFlags = Dx.BIND_RENDER_TARGET;
            IntPtr dst = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            d.BindFlags = 0; d.Usage = Dx.USAGE_STAGING; d.CPUAccessFlags = Dx.CPU_ACCESS_READ;
            IntPtr staging = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            IntPtr srv = Dx.CreateSRV(g.Dev, src), rtv = Dx.CreateRTV(g.Dev, dst);

            GCHandle pin = GCHandle.Alloc(img, GCHandleType.Pinned);
            try { g.Ctx.UpdateSubresource(g.CtxPtr, src, 0, IntPtr.Zero, pin.AddrOfPinnedObject(), W * 4, 0); }
            finally { pin.Free(); }

            g.Sh.Render(srv, rtv, W, H, f, s);
            g.Ctx.CopyResource(g.CtxPtr, staging, dst);

            var outp = new byte[W * H * 4];
            Dx.MAPPED_SUBRESOURCE m;
            Dx.Check(g.Ctx.Map(g.CtxPtr, staging, 0, Dx.MAP_READ, 0, out m), "Map");
            for (int y = 0; y < H; y++)
                Marshal.Copy(new IntPtr(m.pData.ToInt64() + (long)y * m.RowPitch), outp, y * W * 4, W * 4);
            g.Ctx.Unmap(g.CtxPtr, staging, 0);

            Dx.Release(ref rtv); Dx.Release(ref srv); Dx.Release(ref staging); Dx.Release(ref dst); Dx.Release(ref src);
            return outp;
        }

        // ------------------------------------------------------------------
        // CPU references. Pixels are read as R, G, B (the image is stored BGRA).
        static float[] Rgb(byte[] img, int x, int y)
        {
            x = Math.Max(0, Math.Min(W - 1, x));
            y = Math.Max(0, Math.Min(H - 1, y));
            int o = (y * W + x) * 4;
            return new[] { img[o + 2] / 255f, img[o + 1] / 255f, img[o] / 255f };
        }

        static float Sat(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        static byte ToByte(float v) { return (byte)Math.Floor(Sat(v) * 255f + 0.5f); }

        static byte[] Pack(float[][] rgb)
        {
            var o = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                o[i * 4] = ToByte(rgb[i][2]); o[i * 4 + 1] = ToByte(rgb[i][1]); o[i * 4 + 2] = ToByte(rgb[i][0]); o[i * 4 + 3] = 255;
            }
            return o;
        }

        static byte[] Cpu(byte[] img, SharpenFilter f, float s)
        {
            var px = new float[W * H][];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = f == SharpenFilter.Cas ? CasCpu(img, x, y, s)
                                  : f == SharpenFilter.Rcas ? RcasCpu(img, x, y, s)
                                  : LumaCpu(img, x, y, s);
            return Pack(px);
        }

        static float[] CasCpu(byte[] img, int x, int y, float s)
        {
            float[] A = Rgb(img, x - 1, y - 1), B = Rgb(img, x, y - 1), C = Rgb(img, x + 1, y - 1);
            float[] D = Rgb(img, x - 1, y), E = Rgb(img, x, y), F = Rgb(img, x + 1, y);
            float[] G = Rgb(img, x - 1, y + 1), Hh = Rgb(img, x, y + 1), I = Rgb(img, x + 1, y + 1);
            var r = new float[3];
            for (int c = 0; c < 3; c++)
            {
                float a = A[c], b = B[c], cc = C[c], d = D[c], e = E[c], f = F[c], g = G[c], hh = Hh[c], i = I[c];
                float mn = Math.Min(Math.Min(Math.Min(d, e), Math.Min(f, b)), hh);
                float mx = Math.Max(Math.Max(Math.Max(d, e), Math.Max(f, b)), hh);
                mn += Math.Min(Math.Min(Math.Min(mn, a), Math.Min(cc, g)), i);
                mx += Math.Max(Math.Max(Math.Max(mx, a), Math.Max(cc, g)), i);
                float amp = (float)Math.Sqrt(Sat(Math.Min(mn, 2f - mx) / Math.Max(mx, 1e-5f)));
                float wt = amp * (-0.2f * s);
                r[c] = Sat((b * wt + d * wt + f * wt + hh * wt + e) / (1f + 4f * wt));
            }
            return r;
        }

        static float[] RcasCpu(byte[] img, int x, int y, float s)
        {
            float[] b = Rgb(img, x, y - 1), d = Rgb(img, x - 1, y), e = Rgb(img, x, y), f = Rgb(img, x + 1, y), hh = Rgb(img, x, y + 1);
            var l = new float[3];
            for (int c = 0; c < 3; c++)
            {
                float mn4 = Math.Min(Math.Min(b[c], d[c]), Math.Min(f[c], hh[c]));
                float mx4 = Math.Max(Math.Max(b[c], d[c]), Math.Max(f[c], hh[c]));
                float hitMin = Math.Min(mn4, e[c]) / Math.Max(4f * mx4, 1e-5f);
                float hitMax = (1f - Math.Max(mx4, e[c])) / Math.Min(4f * mn4 - 4f, -1e-5f);
                l[c] = Math.Max(-hitMin, hitMax);
            }
            float lobe = Math.Max(-(0.25f - 1f / 16f), Math.Min(Math.Max(l[0], Math.Max(l[1], l[2])), 0f)) * s;
            var r = new float[3];
            for (int c = 0; c < 3; c++)
                r[c] = Sat((lobe * (b[c] + d[c] + f[c] + hh[c]) + e[c]) / (4f * lobe + 1f));
            return r;
        }

        static float Luma(float[] c) { return c[0] * 0.2126f + c[1] * 0.7152f + c[2] * 0.0722f; }

        static float[] LumaCpu(byte[] img, int x, int y, float s)
        {
            float[] c = Rgb(img, x, y);
            float l = Luma(c);
            float blur = 4f * l
                + 2f * (Luma(Rgb(img, x, y - 1)) + Luma(Rgb(img, x - 1, y)) + Luma(Rgb(img, x + 1, y)) + Luma(Rgb(img, x, y + 1)))
                + (Luma(Rgb(img, x - 1, y - 1)) + Luma(Rgb(img, x + 1, y - 1)) + Luma(Rgb(img, x - 1, y + 1)) + Luma(Rgb(img, x + 1, y + 1)));
            blur /= 16f;
            float detail = Math.Max(-0.05f, Math.Min(0.05f, (l - blur) * (s * 3f)));
            return new[] { Sat(c[0] + detail), Sat(c[1] + detail), Sat(c[2] + detail) };
        }

        // ------------------------------------------------------------------
        /// <summary>Anime4K network rebuilt straight from the mpv GLSL.</summary>
        sealed class A4KNet
        {
            public sealed class Term { public string Tex; public int Sign; public bool Relu; public int Dx, Dy; public float[] M; }
            public sealed class Pass { public string Save; public List<Term> Terms = new List<Term>(); public float[] Bias; public bool Residual; }
            public readonly List<Pass> Passes = new List<Pass>();

            public static A4KNet Parse(string src)
            {
                var net = new A4KNet();
                foreach (string p in Regex.Split(src, @"(?m)^//!DESC").Skip1())
                {
                    var pass = new Pass { Save = Regex.Match(p, @"//!SAVE (\w+)").Groups[1].Value };
                    // Macros: name -> (texture, sign, relu, fixed offset?)
                    var macros = new Dictionary<string, Term>();
                    foreach (Match m in Regex.Matches(p, @"(?m)^#define (\w+)(\([^)]*\))? (.*)$"))
                    {
                        string body = m.Groups[3].Value;
                        var t = new Term();
                        t.Relu = body.StartsWith("(max(");
                        t.Sign = body.Contains("max(-(") ? -1 : 1;
                        Match tex = Regex.Match(body, @"(\w+?)_tex(Off)?\(");
                        t.Tex = tex.Groups[1].Value;
                        macros[m.Groups[1].Value] = t;
                    }
                    foreach (Match m in Regex.Matches(p, @"mat4\(([^)]*)\)\s*\*\s*(go_\d+|g_\d+)(\(([^)]*)\))?"))
                    {
                        Term proto = macros[m.Groups[2].Value];
                        var t = new Term { Tex = proto.Tex, Sign = proto.Sign, Relu = proto.Relu, M = Floats(m.Groups[1].Value) };
                        if (m.Groups[4].Success)
                        {
                            float[] off = Floats(m.Groups[4].Value);
                            t.Dx = (int)off[0]; t.Dy = (int)off[1];
                        }
                        pass.Terms.Add(t);
                    }
                    pass.Bias = Floats(Regex.Match(p, @"result \+= vec4\(([^)]*)\);").Groups[1].Value);
                    pass.Residual = p.Contains("return result + MAIN_tex(MAIN_pos);");
                    net.Passes.Add(pass);
                }
                return net;
            }

            static float[] Floats(string csv)
            {
                string[] parts = csv.Split(',');
                var f = new float[parts.Length];
                for (int i = 0; i < parts.Length; i++) f[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
                return f;
            }

            /// <summary>Runs the network; intermediates are rounded to half precision like the GPU's RGBA16F.</summary>
            public byte[] Run(byte[] img, int w, int h, float residual)
            {
                var tex = new Dictionary<string, float[][]>();
                var main = new float[w * h][];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float[] c = Rgb(img, x, y);
                        main[y * w + x] = new[] { c[0], c[1], c[2], 1f };
                    }
                tex["MAIN"] = main;

                float[][] final = null;
                foreach (Pass pass in Passes)
                {
                    var outp = new float[w * h][];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            float[] r = { pass.Bias[0], pass.Bias[1], pass.Bias[2], pass.Bias[3] };
                            foreach (Term t in pass.Terms)
                            {
                                int sx = Math.Max(0, Math.Min(w - 1, x + t.Dx)), sy = Math.Max(0, Math.Min(h - 1, y + t.Dy));
                                float[] v = tex[t.Tex][sy * w + sx];
                                var a = new float[4];
                                for (int i = 0; i < 4; i++) a[i] = t.Relu ? Math.Max(t.Sign * v[i], 0f) : v[i];
                                // GLSL mat4 * vec4: column i of the mat4 is M[4i..4i+3].
                                for (int j = 0; j < 4; j++)
                                    r[j] += a[0] * t.M[j] + a[1] * t.M[4 + j] + a[2] * t.M[8 + j] + a[3] * t.M[12 + j];
                            }
                            if (pass.Residual)
                            {
                                float[] m0 = main[y * w + x];
                                outp[y * w + x] = new[] { m0[0] + r[0] * residual, m0[1] + r[1] * residual, m0[2] + r[2] * residual };
                            }
                            else outp[y * w + x] = new[] { Half(r[0]), Half(r[1]), Half(r[2]), Half(r[3]) };
                        }
                    if (pass.Save == "MAIN") final = outp; else tex[pass.Save] = outp;
                }
                return Pack(final);
            }

            /// <summary>float32 -> float16 -> float32, round to nearest even.</summary>
            static float Half(float f)
            {
                uint x = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
                uint sign = (x >> 16) & 0x8000;
                int exp = (int)((x >> 23) & 0xFF) - 127 + 15;
                uint mant = x & 0x7FFFFF;
                uint hbits;
                if (exp <= 0)
                {
                    if (exp < -10) hbits = sign;
                    else
                    {
                        mant |= 0x800000;
                        int shift = 14 - exp;
                        uint hm = mant >> shift, rem = mant & ((1u << shift) - 1), half = 1u << (shift - 1);
                        if (rem > half || (rem == half && (hm & 1) != 0)) hm++;
                        hbits = sign | hm;
                    }
                }
                else if (exp >= 31) hbits = sign | 0x7C00;
                else
                {
                    uint hm = mant >> 13, rem = mant & 0x1FFF;
                    hbits = sign | ((uint)exp << 10) | hm;
                    if (rem > 0x1000 || (rem == 0x1000 && (hm & 1) != 0)) hbits++;
                }
                // back to float32
                uint s = (hbits & 0x8000) << 16;
                int e = (int)((hbits >> 10) & 0x1F);
                uint m = hbits & 0x3FF;
                uint fb;
                if (e == 0)
                {
                    if (m == 0) fb = s;
                    else { e = 1; while ((m & 0x400) == 0) { m <<= 1; e--; } m &= 0x3FF; fb = s | ((uint)(e - 15 + 127) << 23) | (m << 13); }
                }
                else if (e == 31) fb = s | 0x7F800000 | (m << 13);
                else fb = s | ((uint)(e - 15 + 127) << 23) | (m << 13);
                return BitConverter.ToSingle(BitConverter.GetBytes(fb), 0);
            }
        }

        // ------------------------------------------------------------------
        static int MaxDiff(byte[] a, byte[] b)
        {
            int m = 0;
            for (int i = 0; i < a.Length; i++) if ((i & 3) != 3) m = Math.Max(m, Math.Abs(a[i] - b[i]));
            return m;
        }

        // Sum of horizontal green differences across the hard-edge rows.
        static long EdgeEnergy(byte[] img)
        {
            long sum = 0;
            for (int y = 2; y < 22; y++)
                for (int x = 24; x < 42; x++)
                {
                    int i = (y * W + x) * 4, j = i + 4;
                    sum += Math.Abs(img[i + 1] - img[j + 1]);
                }
            return sum;
        }

        /// <summary>How far past the 30..220 edge values the output strays, in 1/255 steps.</summary>
        static int EdgeOvershoot(byte[] src, byte[] o)
        {
            int worst = 0;
            for (int y = 2; y < 22; y++)
                for (int x = 26; x < 40; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int v = o[(y * W + x) * 4 + c];
                        worst = Math.Max(worst, Math.Max(30 - v, v - 220));
                    }
            return worst;
        }

        static bool SameShiftOnAllChannels(byte[] a, byte[] b)
        {
            for (int i = 0; i < W * H; i++)
            {
                int o = i * 4;
                bool clipped = false;
                for (int c = 0; c < 3; c++) if (b[o + c] == 0 || b[o + c] == 255) clipped = true;
                if (clipped) continue;
                int d0 = b[o] - a[o], d1 = b[o + 1] - a[o + 1], d2 = b[o + 2] - a[o + 2];
                if (Math.Abs(d0 - d1) > 1 || Math.Abs(d1 - d2) > 1) return false;
            }
            return true;
        }

        static bool RegionUnchanged(byte[] a, byte[] b, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        int i = (y * W + x) * 4 + c;
                        if (a[i] != b[i]) return false;
                    }
            return true;
        }

        // ------------------------------------------------------------------
        // What a full 2560x1440 frame costs on this GPU, per filter.
        static void Perf(Gpu g)
        {
            const int PW = 2560, PH = 1440, N = 60;
            var d = new Dx.TEXTURE2D_DESC
            {
                Width = PW, Height = PH, MipLevels = 1, ArraySize = 1, Format = Dx.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1, Usage = Dx.USAGE_DEFAULT, BindFlags = Dx.BIND_SHADER_RESOURCE
            };
            IntPtr src = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            d.BindFlags = Dx.BIND_RENDER_TARGET;
            IntPtr dst = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            d.BindFlags = 0; d.Usage = Dx.USAGE_STAGING; d.CPUAccessFlags = Dx.CPU_ACCESS_READ;
            IntPtr staging = Dx.CreateTexture2D(g.Dev, ref d, IntPtr.Zero);
            IntPtr srv = Dx.CreateSRV(g.Dev, src), rtv = Dx.CreateRTV(g.Dev, dst);

            foreach (SharpenFilter f in Enum.GetValues(typeof(SharpenFilter)))
            {
                g.Sh.Render(srv, rtv, PW, PH, f, 0.5f);
                Sync(g, staging, dst);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) g.Sh.Render(srv, rtv, PW, PH, f, 0.5f + (i & 1) * 0.01f);
                Sync(g, staging, dst);
                sw.Stop();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  perf  {0,-9} 2560x1440: {1:0.000} ms per frame (GPU)", f, sw.Elapsed.TotalMilliseconds / N));
            }
            Dx.Release(ref rtv); Dx.Release(ref srv); Dx.Release(ref staging); Dx.Release(ref dst); Dx.Release(ref src);
        }

        // Copy the result back and map it: Map cannot return until the GPU has
        // finished every queued draw that wrote it.
        static void Sync(Gpu g, IntPtr staging, IntPtr dst)
        {
            g.Ctx.CopyResource(g.CtxPtr, staging, dst);
            Dx.MAPPED_SUBRESOURCE m;
            g.Ctx.Map(g.CtxPtr, staging, 0, Dx.MAP_READ, 0, out m);
            g.Ctx.Unmap(g.CtxPtr, staging, 0);
        }
    }

    internal static class SplitExt
    {
        /// <summary>Everything after the first element (C# 5 has no LINQ-free Skip on arrays).</summary>
        public static IEnumerable<string> Skip1(this string[] a)
        {
            for (int i = 1; i < a.Length; i++) yield return a[i];
        }
    }
}
