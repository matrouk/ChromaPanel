using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ChromaPanel
{
    /// <summary>A global shortcut such as Ctrl+Alt+B, stored as text in settings.</summary>
    public struct Hotkey
    {
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;
        const uint MOD_NOREPEAT = 0x4000;

        public uint Modifiers;   // MOD_* flags
        public Keys Key;         // the non-modifier key; Keys.None means "no shortcut"

        public bool IsEmpty { get { return Key == Keys.None; } }

        public static readonly Hotkey None = new Hotkey();

        /// <summary>
        /// A lone key is only allowed if it cannot be typed by accident:
        /// F-keys, Pause, Scroll Lock. Anything else needs a modifier.
        /// </summary>
        public bool IsUsable
        {
            get
            {
                if (IsEmpty || IsModifierKey(Key)) return false;
                if (Modifiers != 0) return true;
                return (Key >= Keys.F1 && Key <= Keys.F24) || Key == Keys.Pause || Key == Keys.Scroll;
            }
        }

        public static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.LControlKey || k == Keys.RControlKey
                || k == Keys.ShiftKey || k == Keys.LShiftKey || k == Keys.RShiftKey
                || k == Keys.Menu || k == Keys.LMenu || k == Keys.RMenu
                || k == Keys.LWin || k == Keys.RWin;
        }

        public static Hotkey From(Keys modifierState, Keys key)
        {
            var h = new Hotkey { Key = key };
            if ((modifierState & Keys.Control) != 0) h.Modifiers |= MOD_CONTROL;
            if ((modifierState & Keys.Alt) != 0) h.Modifiers |= MOD_ALT;
            if ((modifierState & Keys.Shift) != 0) h.Modifiers |= MOD_SHIFT;
            return h;
        }

        public override string ToString()
        {
            if (IsEmpty) return "";
            var parts = new List<string>();
            if ((Modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Modifiers & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts.ToArray());
        }

        static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            switch (k)
            {
                case Keys.Scroll: return "ScrollLock";
                case Keys.Pause: return "Pause";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.Oem6: return "]";
                case Keys.Oem5: return "\\";
                case Keys.Oem1: return ";";
                case Keys.Oem7: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                default: return k.ToString();
            }
        }

        /// <summary>Inverse of ToString. Unknown or empty text gives Hotkey.None.</summary>
        public static Hotkey Parse(string text)
        {
            var h = new Hotkey();
            if (string.IsNullOrEmpty(text)) return h;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                // "Ctrl++" style: an empty part right after a '+' means the key is '+' itself.
                if (p.Length == 0) { if (i == parts.Length - 1 && i > 0) p = "="; else continue; }
                string u = p.ToUpperInvariant();
                if (u == "CTRL" || u == "CONTROL") { h.Modifiers |= MOD_CONTROL; continue; }
                if (u == "ALT") { h.Modifiers |= MOD_ALT; continue; }
                if (u == "SHIFT") { h.Modifiers |= MOD_SHIFT; continue; }
                if (u == "WIN" || u == "WINDOWS") { h.Modifiers |= MOD_WIN; continue; }
                h.Key = ParseKey(p);
            }
            if (h.Key == Keys.None) return None;
            return h;
        }

        static Keys ParseKey(string p)
        {
            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if (c >= 'A' && c <= 'Z') return (Keys)c;
                if (c >= '0' && c <= '9') return Keys.D0 + (c - '0');
                switch (c)
                {
                    case '`': return Keys.Oemtilde;
                    case '-': return Keys.OemMinus;
                    case '=': return Keys.Oemplus;
                    case '[': return Keys.OemOpenBrackets;
                    case ']': return Keys.Oem6;
                    case '\\': return Keys.Oem5;
                    case ';': return Keys.Oem1;
                    case '\'': return Keys.Oem7;
                    case ',': return Keys.Oemcomma;
                    case '.': return Keys.OemPeriod;
                    case '/': return Keys.OemQuestion;
                }
            }
            if (string.Equals(p, "ScrollLock", StringComparison.OrdinalIgnoreCase)) return Keys.Scroll;
            try { return (Keys)Enum.Parse(typeof(Keys), p, true); }
            catch { return Keys.None; }
        }

        // ------------------------------------------------------------------
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;

        /// <summary>Registers system-wide. Fails if another app already owns the combination.</summary>
        public bool Register(IntPtr hwnd, int id)
        {
            if (!IsUsable) return false;
            return RegisterHotKey(hwnd, id, Modifiers | MOD_NOREPEAT, (uint)Key);
        }

        public static void Unregister(IntPtr hwnd, int id) { UnregisterHotKey(hwnd, id); }
    }
}
