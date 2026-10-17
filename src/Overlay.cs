using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ChromaPanel
{
    /// <summary>
    /// A bare Win32 window that sits on top of one display and is invisible to
    /// everything except the eye: clicks pass through it, it never takes focus,
    /// and it is excluded from screen capture - which is what stops desktop
    /// duplication from capturing its own output and sharpening it again every
    /// frame. Created on the thread that will pump its messages.
    /// </summary>
    internal sealed class OverlayWindow : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public Rectangle Bounds { get; private set; }
        bool _visible;

        delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                   WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        const uint LWA_ALPHA = 0x2;
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        const uint SWP_NOACTIVATE = 0x10, SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
        static readonly IntPtr HTTRANSPARENT = new IntPtr(-1), MA_NOACTIVATE = new IntPtr(3);

        const string ClassName = "ChromaPanelSharpenOverlay";
        static readonly object _lock = new object();
        static WndProc _proc;      // held for the life of the process: Windows keeps calling it
        static bool _registered;

        static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            // Layered + transparent already makes it click-through; these make
            // sure nothing can ever hit-test or activate it anyway.
            if (msg == WM_NCHITTEST) return HTTRANSPARENT;
            if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
            return DefWindowProc(hwnd, msg, w, l);
        }

        static void EnsureClass()
        {
            lock (_lock)
            {
                if (_registered) return;
                _proc = Proc;
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = ClassName
                };
                if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* already exists */)
                    throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
                _registered = true;
            }
        }

        /// <summary>
        /// Per-monitor DPI awareness for this thread only, so the overlay is
        /// laid out in real pixels and DWM never bitmap-stretches it - a
        /// stretched overlay would blur exactly what it is meant to sharpen.
        /// </summary>
        public static void UsePhysicalPixelsOnThisThread()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }   // PER_MONITOR_AWARE_V2
        }

        public OverlayWindow(Rectangle bounds, bool excludeFromCapture)
        {
            EnsureClass();
            Bounds = bounds;
            Handle = CreateWindowEx(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                ClassName, "", WS_POPUP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            // Fully opaque; the layered style is only there for click-through.
            SetLayeredWindowAttributes(Handle, 0, 255, LWA_ALPHA);
            if (excludeFromCapture) SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        }

        public bool Visible { get { return _visible; } }

        public void Show()
        {
            if (_visible || Handle == IntPtr.Zero) return;
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _visible = true;
        }

        /// <summary>
        /// Back to the top of the topmost band if anything has got above it. A fullscreen game is
        /// often topmost too, and Windows lifts it over the overlay whenever it is activated.
        /// </summary>
        public void KeepOnTop()
        {
            if (!_visible || Handle == IntPtr.Zero) return;
            if (GetWindow(Handle, 3 /* GW_HWNDPREV */) == IntPtr.Zero) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public void Hide()
        {
            if (!_visible || Handle == IntPtr.Zero) return;
            ShowWindow(Handle, SW_HIDE);
            _visible = false;
        }

        /// <summary>Drains this thread's message queue without blocking.</summary>
        public static void Pump()
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { DestroyWindow(Handle); Handle = IntPtr.Zero; }
            _visible = false;
        }
    }
}
