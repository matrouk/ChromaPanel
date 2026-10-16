using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChromaPanel
{
    /// <summary>
    /// Covers chosen displays with pure black - on an OLED that switches the
    /// pixels off, which is the point. Exists because OBS keeps the display
    /// awake while it runs, so Windows' own screen-off and screensaver never
    /// trigger.
    ///
    /// The windows never take focus, so a game or OBS keeps running untouched
    /// underneath. Input is watched with low-level hooks (installed only while
    /// blacked out) rather than focus, so any key, click, wheel or a deliberate
    /// mouse move ends it.
    /// </summary>
    internal sealed class Blackout : IDisposable
    {
        public event EventHandler Ended;

        readonly List<BlackWindow> _windows = new List<BlackWindow>();
        IntPtr _kbHook = IntPtr.Zero, _mouseHook = IntPtr.Zero;
        HookProc _kbProc, _mouseProc;     // held so the GC cannot collect them mid-hook
        int _armAt;                       // Environment.TickCount after which input counts
        bool _haveOrigin;
        Point _origin;
        Control _invoker;

        /// <summary>Ignore input for this long, so the shortcut's own key-repeat or the tray click cannot end it instantly.</summary>
        public const int GraceMs = 700;
        /// <summary>Mouse travel that counts as "I'm back". Small enough to be deliberate, big enough to ignore a bumped desk.</summary>
        public const int WakeDistance = 60;

        public bool Active { get { return _windows.Count > 0; } }

        /// <param name="invoker">Any control on the UI thread, used to marshal the stop out of the hook.</param>
        public Blackout(Control invoker) { _invoker = invoker; }

        public void Start(IEnumerable<Rectangle> areas, bool hideFromCapture)
        {
            if (Active) return;
            foreach (Rectangle r in areas)
            {
                var w = new BlackWindow(r, hideFromCapture);
                w.Show();
                _windows.Add(w);
            }
            if (_windows.Count == 0) return;

            _armAt = Environment.TickCount + GraceMs;
            _haveOrigin = false;
            InstallHooks();
        }

        public void Stop()
        {
            if (!Active) return;
            RemoveHooks();
            foreach (BlackWindow w in _windows)
            {
                try { w.Close(); w.Dispose(); } catch { }
            }
            _windows.Clear();
            if (Ended != null) Ended(this, EventArgs.Empty);
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// Bounds to cover: the chosen display, or every display when none was
        /// chosen or the chosen one is no longer connected.
        /// </summary>
        public static List<Rectangle> AreasFor(List<MonitorInfo> mons, string target)
        {
            var l = new List<Rectangle>();
            foreach (MonitorInfo m in mons)
                if (m.Bounds.Width > 0 && (string.IsNullOrEmpty(target) ||
                    string.Equals(m.Key, target, StringComparison.OrdinalIgnoreCase)))
                    l.Add(m.Bounds);
            if (l.Count == 0 && !string.IsNullOrEmpty(target))
                foreach (MonitorInfo m in mons) if (m.Bounds.Width > 0) l.Add(m.Bounds);
            return l;
        }

        // ------------------------------------------------------------------
        // Low-level input hooks
        // ------------------------------------------------------------------
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204,
                  WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A, WM_XBUTTONDOWN = 0x020B, WM_MOUSEHWHEEL = 0x020E;

        void InstallHooks()
        {
            _kbProc = KeyboardProc;
            _mouseProc = MouseProc;
            IntPtr mod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        }

        void RemoveHooks()
        {
            if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }

        bool Armed { get { return unchecked(Environment.TickCount - _armAt) >= 0; } }

        // Hooks must return fast or Windows silently drops them, so the actual
        // teardown is posted back to the UI thread.
        void RequestStop()
        {
            try { _invoker.BeginInvoke((MethodInvoker)Stop); } catch { }
        }

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Active && Armed)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    RequestStop();
                    return new IntPtr(1);   // swallow the waking key, as a screensaver does
                }
            }
            return CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Active && Armed)
            {
                int msg = wParam.ToInt32();
                // MSLLHOOKSTRUCT starts with the cursor position.
                var pt = new Point(Marshal.ReadInt32(lParam, 0), Marshal.ReadInt32(lParam, 4));
                if (msg == WM_MOUSEMOVE)
                {
                    if (!_haveOrigin) { _origin = pt; _haveOrigin = true; }
                    else
                    {
                        int dx = pt.X - _origin.X, dy = pt.Y - _origin.Y;
                        if (dx * dx + dy * dy > WakeDistance * WakeDistance) RequestStop();
                    }
                }
                else if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN ||
                         msg == WM_XBUTTONDOWN || msg == WM_MOUSEWHEEL || msg == WM_MOUSEHWHEEL)
                {
                    RequestStop();
                    return new IntPtr(1);   // do not click whatever is underneath
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ------------------------------------------------------------------
        // The black window itself
        // ------------------------------------------------------------------
        sealed class BlackWindow : Form
        {
            [DllImport("user32.dll")]
            static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
            [DllImport("user32.dll")]
            static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andPlane, byte[] xorPlane);

            const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
            // Visible on the monitor, absent from every capture. OBS display
            // capture then records whatever is underneath, not a black frame.
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

            readonly bool _hideFromCapture;
            static Cursor _blank;

            public BlackWindow(Rectangle bounds, bool hideFromCapture)
            {
                _hideFromCapture = hideFromCapture;
                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
                BackColor = Color.Black;     // #000000 - OLED pixels fully off
                ShowInTaskbar = false;
                TopMost = true;
                Cursor = BlankCursor();
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                if (_hideFromCapture)
                {
                    try { SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); } catch { }
                }
            }

            // Painting nothing but black, and no erase flicker.
            protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Color.Black); }

            static Cursor BlankCursor()
            {
                if (_blank != null) return _blank;
                try
                {
                    // AND mask all ones + XOR mask all zeros = fully transparent.
                    var and = new byte[32 * 32 / 8];
                    var xor = new byte[32 * 32 / 8];
                    for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
                    IntPtr h = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
                    _blank = h != IntPtr.Zero ? new Cursor(h) : Cursors.Default;
                }
                catch { _blank = Cursors.Default; }
                return _blank;
            }
        }
    }
}
