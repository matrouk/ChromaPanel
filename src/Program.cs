using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ChromaPanel
{
    internal static class Program
    {
        public const string Version = AppSettings.Version;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string msg);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly int WM_SHOWME = RegisterWindowMessage("ChromaPanel_ShowMe_9F2C");

        [STAThread]
        static int Main(string[] args)
        {
            var opt = Options.Parse(args);

            if (opt.Help)
            {
                MessageBox.Show(HelpText(), "ChromaPanel " + Version, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Crash(e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Crash(e.Exception);
            };

            AppSettings settings = AppSettings.Load();

            // ---- headless verbs -------------------------------------
            if (opt.Apply || opt.Reset)
            {
                RunHeadless(settings, opt);
                return 0;
            }
            if (opt.Blackout)
            {
                RunBlackout(settings);
                return 0;
            }

            // ---- single instance ------------------------------------
            bool isFirst;
            using (var mutex = new Mutex(true, SingleInstance.MutexName, out isFirst))
            {
                var notes = new List<string>();
                if (!isFirst)
                {
                    System.Version replaced;
                    if (!SingleInstance.ReplaceOlder(out replaced) || !Acquire(mutex))
                    {
                        // Ask the running copy to show itself, then step aside.
                        PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                        return 0;
                    }
                    notes.Add("Closed an older ChromaPanel " + AppSettings.Short(replaced) + " that was running.");
                    settings = AppSettings.Load();   // whatever the old copy saved last
                }

                string healed = AppSettings.HealStartupEntry();
                if (healed != null) notes.Add(healed);

                using (var form = new MainForm(settings, opt.Tray))
                {
                    if (notes.Count > 0) form.Notice(string.Join(" ", notes.ToArray()));
                    Application.Run(form);
                }
            }
            return 0;
        }

        /// <summary>Waits for the replaced copy's mutex; a killed owner leaves it abandoned, which is ours.</summary>
        static bool Acquire(Mutex m)
        {
            try { return m.WaitOne(5000); }
            catch (AbandonedMutexException) { return true; }
        }

        static void RunHeadless(AppSettings settings, Options opt)
        {
            List<MonitorInfo> mons = Displays.Enumerate();

            ColourSet source = null;
            if (opt.Reset)
            {
                source = new ColourSet();
            }
            else
            {
                string name = string.IsNullOrEmpty(opt.ProfileName) ? settings.DefaultProfile : opt.ProfileName;
                Profile p = settings.Find(name);
                if (p == null)
                {
                    MessageBox.Show("No preset named '" + name + "'.", "ChromaPanel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                source = p.Set;
            }

            foreach (MonitorInfo m in mons)
            {
                ColourSet set;
                if (opt.Apply && string.IsNullOrEmpty(opt.ProfileName) && opt.UseSaved)
                    set = settings.ForMonitor(m.Key);
                else
                    set = source;

                bool clamped;
                Gdi.Apply(m.GdiName, Ramp.Build(set), out clamped);
                if (NvApi.Available)
                {
                    NvApi.SetVibrance(m.GdiName, set.Vibrance);
                    NvApi.SetHue(m.GdiName, set.Hue);
                }
                settings.Monitors[m.Key] = set.Clone();
            }
            settings.Save();
        }

        /// <summary>Stand-alone blackout for Stream Deck / OBS hotkeys; exits when woken.</summary>
        static void RunBlackout(AppSettings settings)
        {
            List<Rectangle> areas = Blackout.AreasFor(Displays.Enumerate(), settings.BlackoutTarget);
            if (areas.Count == 0) return;
            using (var host = new Form())
            {
                IntPtr forceHandle = host.Handle;   // a UI-thread handle for the hooks to post back to
                var b = new Blackout(host);
                b.Ended += delegate { Application.ExitThread(); };
                b.Start(areas, settings.BlackoutHideFromCapture);
                Application.Run();
            }
        }

        static void Crash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                string path = Path.Combine(AppSettings.Dir, "crash.log");
                var sb = new StringBuilder();
                sb.AppendLine("---- " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture) + " ChromaPanel " + Version);
                sb.AppendLine(ex == null ? "(no exception object)" : ex.ToString());
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                MessageBox.Show("ChromaPanel hit an error and wrote details to:\r\n" + path +
                                "\r\n\r\n" + (ex == null ? "" : ex.Message),
                                "ChromaPanel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        static string HelpText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ChromaPanel " + Version);
            sb.AppendLine();
            sb.AppendLine("ChromaPanel.exe                 open the window");
            sb.AppendLine("ChromaPanel.exe --tray          start hidden in the notification area");
            sb.AppendLine("ChromaPanel.exe --apply         apply the saved per-display settings");
            sb.AppendLine("ChromaPanel.exe --apply NAME    apply preset NAME to every display");
            sb.AppendLine("ChromaPanel.exe --reset         return every display to neutral");
            sb.AppendLine("ChromaPanel.exe --blackout      black out the chosen display(s) until a key or click");
            sb.AppendLine("ChromaPanel.exe --help          this message");
            sb.AppendLine();
            sb.AppendLine("Settings: " + AppSettings.FilePath);
            return sb.ToString();
        }

        class Options
        {
            public bool Tray, Apply, Reset, Help, UseSaved, Blackout;
            public string ProfileName = "";

            public static Options Parse(string[] args)
            {
                var o = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                    switch (a)
                    {
                        case "tray":
                        case "t":
                        case "minimized":
                        case "minimised":
                            o.Tray = true;
                            break;
                        case "apply":
                            o.Apply = true;
                            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                                o.ProfileName = args[++i];
                            else
                                o.UseSaved = true;
                            break;
                        case "reset":
                            o.Reset = true;
                            break;
                        case "blackout":
                        case "black":
                            o.Blackout = true;
                            break;
                        case "help":
                        case "h":
                        case "?":
                            o.Help = true;
                            break;
                    }
                }
                return o;
            }
        }
    }
}
