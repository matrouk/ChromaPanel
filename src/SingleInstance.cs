using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChromaPanel
{
    /// <summary>
    /// What a launch does when another ChromaPanel already holds the single-instance mutex.
    /// Normally it steps aside and shows the running window. When the running copy is older --
    /// an old download still registered to start with Windows, say -- stepping aside would leave
    /// the old version in charge, so the old copy is closed and this one carries on instead.
    /// </summary>
    internal static class SingleInstance
    {
        public const string MutexName = @"Local\ChromaPanel_SingleInstance";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string msg);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        /// <summary>Asks a running copy to save and exit. Understood from 1.1.1 on.</summary>
        public static readonly int WM_QUIT_FOR_NEWER = RegisterWindowMessage("ChromaPanel_QuitForNewer_9F2C");
        static readonly Version FirstWithQuitMessage = new Version(1, 1, 1, 0);

        /// <summary>
        /// Closes every running ChromaPanel older than this one. Returns false without touching
        /// anything when a running copy is the same version or newer, or none could be identified,
        /// and the caller then defers to the running copy as before. <paramref name="replaced"/> is
        /// the oldest version closed.
        /// </summary>
        public static bool ReplaceOlder(out Version replaced)
        {
            replaced = null;
            Version self = AppSettings.CurrentVersion;
            var older = new List<KeyValuePair<Process, Version>>();
            Process[] all = Process.GetProcesses();
            try
            {
                using (Process me = Process.GetCurrentProcess())
                {
                    foreach (Process p in all)
                    {
                        if (p.Id == me.Id || p.SessionId != me.SessionId) continue;
                        // Only open processes that could be us; the file version decides. A renamed
                        // copy is missed, which just means the old behaviour (show it).
                        if (p.ProcessName.IndexOf("chroma", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string path;
                        try { path = p.MainModule.FileName; } catch { continue; }
                        Version v = AppSettings.ExeVersion(path);     // null for Razer Chroma and friends
                        if (v == null) continue;
                        if (v >= self) return false;
                        older.Add(new KeyValuePair<Process, Version>(p, v));
                    }
                }
                if (older.Count == 0) return false;

                bool allGone = true;
                foreach (KeyValuePair<Process, Version> o in older)
                {
                    Process p = o.Key;
                    // Older builds have no quit message, and closing them normally would save a
                    // settings file without the keys they do not know. End them outright instead.
                    if (o.Value >= FirstWithQuitMessage) PostToWindowsOf(p.Id, WM_QUIT_FOR_NEWER);
                    if (!p.WaitForExit(o.Value >= FirstWithQuitMessage ? 5000 : 0))
                    {
                        try { p.Kill(); } catch { }
                        if (!p.WaitForExit(3000)) allGone = false;
                    }
                    if (replaced == null || o.Value < replaced) replaced = o.Value;
                }
                return allGone;
            }
            finally
            {
                foreach (Process p in all) p.Dispose();
            }
        }

        static void PostToWindowsOf(int pid, int msg)
        {
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                int owner;
                GetWindowThreadProcessId(h, out owner);
                if (owner == pid) PostMessage(h, msg, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
    }
}
