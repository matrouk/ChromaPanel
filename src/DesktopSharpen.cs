using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ChromaPanel
{
    /// <summary>
    /// Sharpens what is on screen, the way ShaderGlass's "desktop glass" does:
    /// each display is captured with DXGI desktop duplication, run through
    /// the sharpening shader and drawn into a click-through overlay that is
    /// itself excluded from capture (so it never re-sharpens its own output).
    ///
    /// Costs that come with the technique, not this implementation:
    ///  - about one frame of latency on whatever is under it (the hardware
    ///    mouse cursor is unaffected);
    ///  - DRM video is blanked in capture, so windowed Netflix and the like
    ///    show black under the overlay. It cannot be detected: DXGI reports
    ///    ProtectedContentMaskedOut for ANY capture-excluded window, the
    ///    overlay itself included, so the flag is true on every frame
    ///    (measured: 25/25 with an excluded window, 0/25 without);
    ///  - fullscreen apps get the display to themselves, so games and
    ///    fullscreen video are untouched.
    /// </summary>
    internal sealed class DesktopSharpen : IDisposable
    {
        readonly List<Worker> _workers = new List<Worker>();
        volatile int _filter;
        volatile int _strengthMilli;

        public bool Running { get { return _workers.Count > 0; } }
        internal SharpenFilter Filter { get { return (SharpenFilter)_filter; } }
        internal float Strength { get { return _strengthMilli / 1000f; } }

        public void Start(List<MonitorInfo> targets, SharpenFilter filter, int percent)
        {
            Stop();
            SetParams(filter, percent);
            foreach (MonitorInfo m in targets)
            {
                var w = new Worker(this, m.GdiName, m.BaseName, m.HdrEnabled);
                _workers.Add(w);
                w.Start();
            }
        }

        /// <summary>Live change; the render threads pick it up on their next frame.</summary>
        public void SetParams(SharpenFilter filter, int percent)
        {
            _filter = (int)filter;
            _strengthMilli = Math.Max(0, Math.Min(100, percent)) * 10;
            foreach (Worker w in _workers) w.ParamsChanged = true;
        }

        public void Stop()
        {
            foreach (Worker w in _workers) w.RequestStop();
            foreach (Worker w in _workers) w.Join(3000);
            _workers.Clear();
        }

        public void Dispose() { Stop(); }

        /// <summary>Frames presented across all displays since Start.</summary>
        public int FramesPresented
        {
            get { int n = 0; foreach (Worker w in _workers) n += Thread.VolatileRead(ref w.Frames); return n; }
        }

        /// <summary>One line per display, for the status label.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (Worker w in _workers)
            {
                if (sb.Length > 0) sb.Append("   \u00B7   ");
                sb.Append(w.Label).Append(": ").Append(w.State);
            }
            return sb.ToString();
        }

        /// <summary>"" = every display; otherwise just the one whose key matches (none if it is gone).</summary>
        public static List<MonitorInfo> TargetsFor(List<MonitorInfo> mons, string target)
        {
            var l = new List<MonitorInfo>();
            foreach (MonitorInfo m in mons)
                if (string.IsNullOrEmpty(target) || string.Equals(m.Key, target, StringComparison.OrdinalIgnoreCase))
                    l.Add(m);
            return l;
        }

        // ==================================================================
        sealed class Worker
        {
            readonly DesktopSharpen _owner;
            readonly string _gdi;
            readonly bool _hdr;
            public readonly string Label;
            Thread _thread;
            volatile bool _stop;
            public volatile bool ParamsChanged = true;
            public volatile string State = "starting";
            public int Frames;   // presented, for diagnostics and the integration test

            public Worker(DesktopSharpen owner, string gdi, string label, bool hdr)
            {
                _owner = owner; _gdi = gdi; Label = label; _hdr = hdr;
            }

            public void Start()
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "ChromaPanel sharpen " + _gdi };
                _thread.Start();
            }

            public void RequestStop() { _stop = true; }
            public void Join(int ms) { if (_thread != null) _thread.Join(ms); }
            public bool Stopping { get { return _stop; } }

            void Run()
            {
                OverlayWindow.UsePhysicalPixelsOnThisThread();
                if (_hdr)
                {
                    // In HDR the desktop is scRGB floating point; this path is 8-bit SDR.
                    State = "off - HDR is on";
                    return;
                }
                while (!_stop)
                {
                    Session s = null;
                    try
                    {
                        s = new Session(this, _gdi);
                        s.Loop();
                    }
                    catch (Exception ex) { State = "error - " + ex.Message; }
                    finally { if (s != null) s.Dispose(); }

                    // Lost the output (mode change, UAC prompt, driver reset): retry shortly.
                    for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
                }
                State = "stopped";
            }

            internal SharpenFilter Filter { get { return _owner.Filter; } }
            internal float Strength { get { return _owner.Strength; } }
        }

        // ==================================================================
        /// <summary>Everything bound to one display for as long as its output stays valid.</summary>
        sealed class Session : IDisposable
        {
            readonly Worker _w;
            IntPtr _adapter, _output, _device, _ctxPtr, _dup, _swap, _backbuffer, _rtv, _copy, _copySrv;
            Dx.Context _ctx;
            Sharpener _sharpener;
            OverlayWindow _overlay;
            Rectangle _bounds;
            int _width, _height;

            public Session(Worker w, string gdi)
            {
                _w = w;
                Dx.OUTPUT_DESC od;
                if (!Dx.FindOutput(gdi, out _adapter, out _output, out od))
                    throw new InvalidOperationException("display not found");
                if (od.Rotation != 1 && od.Rotation != 0)
                    throw new InvalidOperationException("rotated displays are not supported");

                _bounds = Rectangle.FromLTRB(od.Left, od.Top, od.Right, od.Bottom);
                _width = _bounds.Width;
                _height = _bounds.Height;

                _device = Dx.CreateDevice(_adapter, out _ctxPtr);
                _ctx = new Dx.Context(_ctxPtr);
                _dup = Dx.DuplicateOutput(_output, _device);

                _overlay = new OverlayWindow(_bounds, true);
                _swap = Dx.CreateSwapChain(_device, _overlay.Handle, (uint)_width, (uint)_height);
                _backbuffer = Dx.SwapChainBuffer(_swap);
                _rtv = Dx.CreateRTV(_device, _backbuffer);

                // Our own copy of the latest desktop image. The duplicated
                // texture is only valid until ReleaseFrame, and a strength
                // change has to re-render without waiting for the desktop to move.
                var d = new Dx.TEXTURE2D_DESC
                {
                    Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1,
                    Format = Dx.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
                    Usage = Dx.USAGE_DEFAULT, BindFlags = Dx.BIND_SHADER_RESOURCE
                };
                _copy = Dx.CreateTexture2D(_device, ref d, IntPtr.Zero);
                _copySrv = Dx.CreateSRV(_device, _copy);
                _sharpener = new Sharpener(_device, _ctx);
            }

            public void Loop()
            {
                var acquire = Dx.AcquireNextFrameFn(_dup);
                var release = Dx.ReleaseFrameFn(_dup);
                var present = Dx.PresentFn(_swap);

                bool haveFrame = false, paused = true, needRender = true;
                int lastCheck = Environment.TickCount - 1000;
                _w.State = "starting";

                while (!_w.Stopping)
                {
                    OverlayWindow.Pump();

                    Dx.OUTDUPL_FRAME_INFO fi;
                    IntPtr res;
                    int hr = acquire(_dup, 16, out fi, out res);
                    if (hr == Dx.DXGI_ERROR_ACCESS_LOST) { _overlay.Hide(); _w.State = "reconnecting"; return; }
                    if (hr != Dx.DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        Dx.Check(hr, "AcquireNextFrame");
                        try
                        {
                            // LastPresentTime 0 = only the pointer moved; no new image.
                            if (fi.LastPresentTime != 0)
                            {
                                IntPtr tex = Dx.QueryInterface(res, Dx.IID_ID3D11Texture2D);
                                _ctx.CopyResource(_ctxPtr, _copy, tex);
                                Dx.Release(ref tex);
                                haveFrame = true;
                                needRender = true;
                            }
                            // fi.ProtectedContentMaskedOut is deliberately ignored - see the class comment.
                        }
                        finally { Dx.Release(ref res); release(_dup); }
                    }

                    int now = Environment.TickCount;
                    if (unchecked(now - lastCheck) >= 250)
                    {
                        lastCheck = now;
                        string why = null;
                        if (_w.Strength <= 0f) why = "strength 0";
                        else if (FullscreenAppOn(_bounds)) why = "fullscreen app";

                        if (why != null)
                        {
                            if (!paused) { _overlay.Hide(); paused = true; }
                            _w.State = "paused (" + why + ")";
                        }
                        else if (paused && haveFrame)
                        {
                            paused = false;
                            needRender = true;
                            _w.State = "sharpening";
                        }
                    }

                    if (_w.ParamsChanged) { _w.ParamsChanged = false; needRender = true; }

                    if (!paused && haveFrame && needRender)
                    {
                        needRender = false;
                        _sharpener.Render(_copySrv, _rtv, _width, _height, _w.Filter, _w.Strength);
                        present(_swap, 1, 0);
                        Interlocked.Increment(ref _w.Frames);
                        // Shown only once a real frame is in it - never a blank overlay.
                        if (!_overlay.Visible)
                        {
                            _overlay.Show();
                            _sharpener.Render(_copySrv, _rtv, _width, _height, _w.Filter, _w.Strength);
                            present(_swap, 1, 0);
                        }
                    }
                }
                _overlay.Hide();
            }

            public void Dispose()
            {
                if (_overlay != null) { _overlay.Dispose(); _overlay = null; }
                if (_sharpener != null) { _sharpener.Dispose(); _sharpener = null; }
                Dx.Release(ref _copySrv);
                Dx.Release(ref _copy);
                Dx.Release(ref _rtv);
                Dx.Release(ref _backbuffer);
                Dx.Release(ref _swap);
                Dx.Release(ref _dup);
                Dx.Release(ref _ctxPtr);
                Dx.Release(ref _device);
                Dx.Release(ref _output);
                Dx.Release(ref _adapter);
            }
        }

        // ==================================================================
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        const uint GA_ROOT = 2;

        /// <summary>
        /// Is a fullscreen app showing on this display? Looks at whatever is on
        /// top at the display's centre - not just the focused window - so a
        /// fullscreen video on a second monitor counts too. (The overlay is
        /// click-through, so WindowFromPoint looks straight through it.)
        ///
        /// A maximised window is never fullscreen. That matters: apps with
        /// custom-drawn title bars (Discord, VS Code, Spotify...) have no
        /// standard caption, and on a display without a taskbar a maximised
        /// one covers the whole screen. Games, F11 browsers and video players
        /// are not in the maximised state - Chromium even un-maximises first.
        /// </summary>
        static bool FullscreenAppOn(Rectangle display)
        {
            var centre = new POINT { X = display.Left + display.Width / 2, Y = display.Top + display.Height / 2 };
            IntPtr h = GetAncestor(WindowFromPoint(centre), GA_ROOT);
            if (h == IntPtr.Zero) return false;

            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;

            if (IsZoomed(h)) return false;

            RECT r;
            if (!GetWindowRect(h, out r)) return false;
            return r.Left <= display.Left && r.Top <= display.Top && r.Right >= display.Right && r.Bottom >= display.Bottom;
        }
    }
}
