using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChromaPanel
{
    internal class MainForm : Form
    {
        // ---- state ----------------------------------------------------
        AppSettings _s;
        List<MonitorInfo> _mons = new List<MonitorInfo>();
        readonly List<string> _profileOrder = new List<string>();
        readonly Dictionary<string, NvApi.NV_LEVEL> _vibRange = new Dictionary<string, NvApi.NV_LEVEL>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, NvApi.NV_LEVEL> _hueRange = new Dictionary<string, NvApi.NV_LEVEL>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _noVib = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _noHue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool _suspend;          // slider/combo events are programmatic
        bool _reallyClose;
        bool _startHidden;

        // ---- controls -------------------------------------------------
        DarkCombo _cboMon, _cboProf, _cboCh;
        Label _lblMonInfo, _lblDefault, _lblStatus, _lblHint, _lblSaved, _lblMixed;
        DarkCheck _chkAll, _chkApplyLaunch, _chkReapply, _chkTray, _chkStartup;
        FlatButton _btnNew, _btnRename, _btnDelete, _btnDefault, _btnReset, _btnDiag, _btnApply;
        Panel _swatch;
        CurvePreview _curve;
        SliderRow _rowB, _rowC, _rowG, _rowV, _rowH;
        NotifyIcon _tray;
        Timer _saveTimer, _displayTimer, _savedFlash;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainForm(AppSettings s, bool startHidden)
        {
            _s = s;
            _startHidden = startHidden;
            BuildUi();
            HookEvents();
            LoadDisplays(true);
            RefreshProfiles(_s.LastProfile.Length > 0 ? _s.LastProfile : _s.DefaultProfile);
            LoadSlidersFromState();
            UpdateDefaultLabel();

            if (_s.ApplyOnLaunch) ApplyAll("Restored saved settings.");
            else SetStatus("Ready. Nothing applied yet.", Theme.Muted);
        }

        // ==============================================================
        // UI construction
        // ==============================================================
        void BuildUi()
        {
            Text = "ChromaPanel";
            ClientSize = new Size(620, 876);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI(9f);
            Icon = MakeIcon();
            KeyPreview = true;

            var title = new Label();
            title.Text = "ChromaPanel";
            title.Font = Theme.UIBold(14f);
            title.ForeColor = Theme.Text;
            title.SetBounds(16, 12, 300, 28);
            title.BackColor = Color.Transparent;
            Controls.Add(title);

            var sub = new Label();
            sub.Text = "Per-display colour control";
            sub.Font = Theme.UI(8.5f);
            sub.ForeColor = Theme.Faint;
            sub.TextAlign = ContentAlignment.MiddleRight;
            sub.SetBounds(304, 18, 300, 18);
            sub.BackColor = Color.Transparent;
            Controls.Add(sub);

            // ---------------- display ----------------
            var cardD = NewCard("Display", 52, 104);

            _cboMon = new DarkCombo();
            _cboMon.SetBounds(14, 30, 560, 24);
            cardD.Controls.Add(_cboMon);

            _lblMonInfo = Muted(14, 60, 560, 16, 8.5f);
            cardD.Controls.Add(_lblMonInfo);

            _chkAll = new DarkCheck();
            _chkAll.Text = "Mirror every change to all displays";
            _chkAll.SetBounds(14, 78, 340, 20);
            _chkAll.Checked = _s.LinkAllMonitors;
            cardD.Controls.Add(_chkAll);

            // ---------------- preset ----------------
            var cardP = NewCard("Preset", 168, 96);

            _cboProf = new DarkCombo();
            _cboProf.SetBounds(14, 28, 240, 24);
            cardP.Controls.Add(_cboProf);

            _btnNew = NewButton(cardP, "New", 262, 27, 58);
            _btnRename = NewButton(cardP, "Rename", 324, 27, 70);
            _btnDelete = NewButton(cardP, "Delete", 398, 27, 62);
            _btnDefault = NewButton(cardP, "Set default", 464, 27, 110);

            _lblDefault = Muted(14, 62, 560, 18, 8.5f);
            cardP.Controls.Add(_lblDefault);

            // ---------------- colour ----------------
            var cardC = NewCard("Colour adjustment", 276, 332);

            var lblCh = new Label();
            lblCh.Text = "Channel";
            lblCh.SetBounds(14, 32, 60, 20);
            lblCh.ForeColor = Theme.Text;
            lblCh.BackColor = Color.Transparent;
            cardC.Controls.Add(lblCh);

            _cboCh = new DarkCombo();
            _cboCh.SetBounds(78, 29, 150, 24);
            _cboCh.Items.AddRange(new object[] { "All channels", "Red", "Green", "Blue" });
            _cboCh.SelectedIndex = 0;
            cardC.Controls.Add(_cboCh);

            _swatch = new Panel();
            _swatch.SetBounds(238, 31, 20, 20);
            _swatch.BackColor = Theme.Faint;
            cardC.Controls.Add(_swatch);

            _lblMixed = Muted(268, 32, 306, 18, 8.5f);
            cardC.Controls.Add(_lblMixed);

            _rowB = NewRow(cardC, "Brightness", 62, 0, 100, 50, 1.0, "%", "0");
            _rowC = NewRow(cardC, "Contrast", 114, 0, 100, 50, 1.0, "%", "0");
            _rowG = NewRow(cardC, "Gamma", 166, 30, 280, 100, 100.0, "", "0.00");
            _rowV = NewRow(cardC, "Digital vibrance", 218, 0, 100, 50, 1.0, "%", "0");
            _rowH = NewRow(cardC, "Hue", 270, 0, 359, 0, 1.0, "\u00B0", "0");

            _rowG.Bar.Neutral = 100;
            _rowH.Bar.Neutral = 0;
            _rowV.Bar.Fill = Color.FromArgb(196, 112, 240);
            _rowH.Bar.Fill = Color.FromArgb(240, 160, 80);

            // ---------------- preview + status ----------------
            var cardS = NewCard("Response", 620, 104);

            _curve = new CurvePreview();
            _curve.SetBounds(14, 28, 180, 62);
            cardS.Controls.Add(_curve);

            _lblStatus = new Label();
            _lblStatus.SetBounds(206, 28, 368, 20);
            _lblStatus.ForeColor = Theme.Muted;
            _lblStatus.BackColor = Color.Transparent;
            _lblStatus.Font = Theme.UI(9f);
            cardS.Controls.Add(_lblStatus);

            _lblHint = Muted(206, 50, 368, 40, 8f);
            cardS.Controls.Add(_lblHint);

            // ---------------- options ----------------
            var cardO = NewCard("Options", 736, 78);

            _chkApplyLaunch = NewCheck(cardO, "Apply saved settings at launch", 14, 26, 270);
            _chkReapply = NewCheck(cardO, "Re-apply after display or power changes", 14, 50, 270);
            _chkTray = NewCheck(cardO, "Close to tray instead of exiting", 296, 26, 278);
            _chkStartup = NewCheck(cardO, "Start with Windows", 296, 50, 278);

            _chkApplyLaunch.Checked = _s.ApplyOnLaunch;
            _chkReapply.Checked = _s.ReapplyOnDisplayChange;
            _chkTray.Checked = _s.MinimiseToTray;
            _chkStartup.Checked = AppSettings.GetStartWithWindows();

            // ---------------- bottom bar ----------------
            _btnReset = NewButton(this, "Reset to neutral", 16, 828, 140);
            _btnDiag = NewButton(this, "Diagnostics", 164, 828, 110);

            _lblSaved = new Label();
            _lblSaved.SetBounds(282, 836, 200, 18);
            _lblSaved.TextAlign = ContentAlignment.MiddleRight;
            _lblSaved.ForeColor = Theme.Faint;
            _lblSaved.Font = Theme.UI(8f);
            _lblSaved.BackColor = Color.Transparent;
            _lblSaved.Text = "Changes save automatically";
            Controls.Add(_lblSaved);

            _btnApply = NewButton(this, "Apply now", 494, 828, 110);
            _btnApply.Primary = true;

            // ---------------- tray ----------------
            _tray = new NotifyIcon();
            _tray.Icon = Icon;
            _tray.Text = "ChromaPanel";
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open ChromaPanel", null, delegate { ShowFromTray(); });
            menu.Items.Add("Apply default preset", null, delegate { ApplyDefaultPreset(); });
            menu.Items.Add("Reset all displays", null, delegate { ResetAll(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { _reallyClose = true; Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };

            // ---------------- timers ----------------
            _saveTimer = new Timer();
            _saveTimer.Interval = 700;
            _saveTimer.Tick += delegate { _saveTimer.Stop(); SaveNow(); };

            _displayTimer = new Timer();
            _displayTimer.Interval = 1200;
            _displayTimer.Tick += delegate { _displayTimer.Stop(); OnDisplaysChanged(); };

            _savedFlash = new Timer();
            _savedFlash.Interval = 1400;
            _savedFlash.Tick += delegate
            {
                _savedFlash.Stop();
                _lblSaved.ForeColor = Theme.Faint;
                _lblSaved.Text = "Changes save automatically";
            };
        }

        Card NewCard(string heading, int y, int h)
        {
            var c = new Card();
            c.Heading = heading;
            c.SetBounds(16, y, 588, h);
            Controls.Add(c);
            return c;
        }

        static Label Muted(int x, int y, int w, int h, float size)
        {
            var l = new Label();
            l.SetBounds(x, y, w, h);
            l.ForeColor = Theme.Muted;
            l.Font = Theme.UI(size);
            l.BackColor = Color.Transparent;
            return l;
        }

        static FlatButton NewButton(Control parent, string text, int x, int y, int w)
        {
            var b = new FlatButton();
            b.Text = text;
            b.SetBounds(x, y, w, 30);
            parent.Controls.Add(b);
            return b;
        }

        static DarkCheck NewCheck(Control parent, string text, int x, int y, int w)
        {
            var c = new DarkCheck();
            c.Text = text;
            c.SetBounds(x, y, w, 20);
            parent.Controls.Add(c);
            return c;
        }

        static SliderRow NewRow(Control parent, string caption, int y, int min, int max, int val, double div, string suffix, string fmt)
        {
            var r = new SliderRow();
            r.Divisor = div;
            r.Suffix = suffix;
            r.Format = fmt;
            r.Attach(parent, 14, y, 560);
            r.Caption.Text = caption;
            r.Bar.Minimum = min;
            r.Bar.Maximum = max;
            r.Bar.SetQuiet(val);
            r.UpdateReadout();
            return r;
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(239, 88, 88)), 2, 6, 18, 18);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(160, 74, 200, 105)), 12, 6, 18, 18);
                    g.FillEllipse(new SolidBrush(Color.FromArgb(150, 84, 132, 245)), 7, 12, 18, 18);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar. Attribute 20 on current Windows, 19 on older builds.
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_startHidden) { Hide(); _startHidden = false; }
        }

        protected override void WndProc(ref Message m)
        {
            // A second launch broadcasts this instead of opening a new window.
            if (m.Msg == Program.WM_SHOWME) ShowFromTray();
            base.WndProc(ref m);
        }

        // ==============================================================
        // Events
        // ==============================================================
        void HookEvents()
        {
            EventHandler onSlide = delegate { if (!_suspend) { PushSlidersToState(); ApplyCurrent(); QueueSave(); } };
            _rowB.Bar.ValueChanged += onSlide;
            _rowC.Bar.ValueChanged += onSlide;
            _rowG.Bar.ValueChanged += onSlide;
            _rowV.Bar.ValueChanged += onSlide;
            _rowH.Bar.ValueChanged += onSlide;

            _cboMon.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                LoadSlidersFromState();
                MonitorInfo m = Current;
                if (m != null) { _s.LastMonitorKey = m.Key; QueueSave(); }
            };

            _cboCh.SelectedIndexChanged += delegate { if (!_suspend) LoadSlidersFromState(); };

            _cboProf.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                string name = SelectedProfileName;
                Profile p = _s.Find(name);
                if (p == null) return;
                _s.LastProfile = name;
                ApplySetToTargets(p.Set);
                LoadSlidersFromState();
                ApplyCurrent();
                SetStatus("Applied preset '" + name + "'.", Theme.Ok);
                QueueSave();
            };

            _chkAll.CheckedChanged += delegate
            {
                _s.LinkAllMonitors = _chkAll.Checked;
                if (_chkAll.Checked) { PushSlidersToState(); ApplyCurrent(); }
                QueueSave();
            };

            _chkApplyLaunch.CheckedChanged += delegate { _s.ApplyOnLaunch = _chkApplyLaunch.Checked; QueueSave(); };
            _chkReapply.CheckedChanged += delegate { _s.ReapplyOnDisplayChange = _chkReapply.Checked; QueueSave(); };
            _chkTray.CheckedChanged += delegate { _s.MinimiseToTray = _chkTray.Checked; QueueSave(); };
            _chkStartup.CheckedChanged += delegate
            {
                string err = AppSettings.SetStartWithWindows(_chkStartup.Checked);
                if (err != null) SetStatus("Could not change startup entry: " + err, Theme.Err);
                else SetStatus(_chkStartup.Checked ? "ChromaPanel will start with Windows." : "Startup entry removed.", Theme.Muted);
            };

            _btnNew.Click += delegate { NewProfile(); };
            _btnRename.Click += delegate { RenameProfile(); };
            _btnDelete.Click += delegate { DeleteProfile(); };
            _btnDefault.Click += delegate { SetDefaultProfile(); };
            _btnReset.Click += delegate { ResetCurrent(); };
            _btnApply.Click += delegate { ApplyCurrent(); SetStatus("Re-applied.", Theme.Ok); };
            _btnDiag.Click += delegate { using (var d = new DiagnosticsForm(_mons)) d.ShowDialog(this); };

            SystemEvents.DisplaySettingsChanged += OnSystemDisplayChange;
            SystemEvents.PowerModeChanged += OnPowerChange;

            FormClosing += delegate (object sender, FormClosingEventArgs e)
            {
                if (!_reallyClose && _s.MinimiseToTray && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
                SaveNow();
                try
                {
                    SystemEvents.DisplaySettingsChanged -= OnSystemDisplayChange;
                    SystemEvents.PowerModeChanged -= OnPowerChange;
                }
                catch { }
                _tray.Visible = false;
            };
        }

        void OnSystemDisplayChange(object sender, EventArgs e)
        {
            _displayTimer.Stop();
            _displayTimer.Start();
        }

        void OnPowerChange(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _displayTimer.Stop();
                _displayTimer.Start();
            }
        }

        void OnDisplaysChanged()
        {
            LoadDisplays(false);
            LoadSlidersFromState();
            if (_s.ReapplyOnDisplayChange) ApplyAll("Displays changed - settings re-applied.");
            else SetStatus("Display configuration changed.", Theme.Muted);
        }

        void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        // ==============================================================
        // Displays
        // ==============================================================
        MonitorInfo Current
        {
            get
            {
                int i = _cboMon.SelectedIndex;
                if (i < 0 || i >= _mons.Count) return null;
                return _mons[i];
            }
        }

        List<MonitorInfo> Targets
        {
            get
            {
                if (_chkAll.Checked) return _mons;
                var l = new List<MonitorInfo>();
                MonitorInfo m = Current;
                if (m != null) l.Add(m);
                return l;
            }
        }

        ColourSet CurrentSet
        {
            get
            {
                MonitorInfo m = Current;
                return _s.ForMonitor(m == null ? "*" : m.Key);
            }
        }

        void LoadDisplays(bool first)
        {
            string wanted = null;
            MonitorInfo cur = Current;
            if (cur != null) wanted = cur.Key;
            else if (first) wanted = _s.LastMonitorKey;

            _mons = Displays.Enumerate();
            _vibRange.Clear();
            _hueRange.Clear();
            _noVib.Clear();
            _noHue.Clear();

            _suspend = true;
            _cboMon.Items.Clear();
            foreach (MonitorInfo m in _mons)
            {
                _cboMon.Items.Add(m.Label);
                _s.MonitorNames[m.Key] = m.ShortLabel;
            }
            if (_cboMon.Items.Count == 0) _cboMon.Items.Add("No displays found");

            int idx = 0;
            if (!string.IsNullOrEmpty(wanted))
            {
                for (int i = 0; i < _mons.Count; i++)
                    if (string.Equals(_mons[i].Key, wanted, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            }
            _cboMon.SelectedIndex = Math.Min(idx, _cboMon.Items.Count - 1);
            _suspend = false;

            UpdateMonitorInfo();
        }

        void UpdateMonitorInfo()
        {
            MonitorInfo m = Current;
            if (m == null) { _lblMonInfo.Text = ""; return; }

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.ConnectorLabel)) parts.Add(m.ConnectorLabel);
            if (!string.IsNullOrEmpty(m.Geometry)) parts.Add(m.Geometry);
            if (!string.IsNullOrEmpty(m.EdidSerial)) parts.Add("s/n " + m.EdidSerial);
            parts.Add(m.GdiName.Replace(@"\\.\", ""));
            if (m.ColorMode >= 0) parts.Add(Ccd.ModeName(m.ColorMode));
            else if (m.HdrEnabled) parts.Add("HDR on");
            if (m.BitsPerColor > 0) parts.Add(m.BitsPerColor.ToString(CultureInfo.InvariantCulture) + "-bit");

            _lblMonInfo.Text = string.Join("   \u00B7   ", parts.ToArray());
        }

        /// <summary>Explains whatever is currently limiting this display.</summary>
        void UpdateHint()
        {
            MonitorInfo m = Current;
            if (m == null) { SetHint(""); return; }

            // Only real HDR bypasses the gamma ramp. Wide colour gamut does not,
            // so do not warn about it.
            if (m.HdrEnabled)
            {
                SetHint("HDR is on for this display, and Windows ignores the gamma ramp while it is - brightness, contrast and gamma will do nothing until you turn HDR off. Vibrance still works.");
                return;
            }
            if (_noHue.Contains(m.Key))
            {
                SetHint("Hue is not exposed by this driver (NVAPI reports it unsupported), so that slider is disabled.");
                return;
            }
            SetHint("");
        }

        // ==============================================================
        // NVAPI ranges
        // ==============================================================
        NvApi.NV_LEVEL VibRange(MonitorInfo m)
        {
            NvApi.NV_LEVEL r;
            if (_vibRange.TryGetValue(m.Key, out r)) return r;
            if (!NvApi.Available || !NvApi.GetVibrance(m.GdiName, out r))
            {
                _noVib.Add(m.Key);
                r = new NvApi.NV_LEVEL { minLevel = 0, maxLevel = 100, defaultLevel = 50, currentLevel = 50 };
            }
            _vibRange[m.Key] = r;
            return r;
        }

        NvApi.NV_LEVEL HueRange(MonitorInfo m)
        {
            NvApi.NV_LEVEL r;
            if (_hueRange.TryGetValue(m.Key, out r)) return r;
            // NVAPI reports status -9 (NVAPI_NOT_SUPPORTED) for hue on current
            // driver branches, so treat a failed query as "no hue control"
            // rather than leaving a slider that silently does nothing.
            if (!NvApi.Available || !NvApi.GetHue(m.GdiName, out r) || r.maxLevel <= r.minLevel)
            {
                _noHue.Add(m.Key);
                r = new NvApi.NV_LEVEL { minLevel = 0, maxLevel = 359, defaultLevel = 0, currentLevel = 0 };
            }
            _hueRange[m.Key] = r;
            return r;
        }

        // ==============================================================
        // Sliders <-> state
        // ==============================================================
        char[] ActiveChannels
        {
            get
            {
                switch (_cboCh.SelectedIndex)
                {
                    case 1: return new[] { 'R' };
                    case 2: return new[] { 'G' };
                    case 3: return new[] { 'B' };
                    default: return new[] { 'R', 'G', 'B' };
                }
            }
        }

        void LoadSlidersFromState()
        {
            MonitorInfo m = Current;
            ColourSet set = CurrentSet;
            char[] chs = ActiveChannels;
            Adjust first = set[chs[0]];

            _suspend = true;
            _rowB.Bar.SetQuiet((int)Math.Round(first.Brightness));
            _rowC.Bar.SetQuiet((int)Math.Round(first.Contrast));
            _rowG.Bar.SetQuiet((int)Math.Round(first.Gamma * 100.0));

            if (m != null)
            {
                NvApi.NV_LEVEL v = VibRange(m);
                _rowV.Bar.Minimum = v.minLevel;
                _rowV.Bar.Maximum = Math.Max(v.minLevel + 1, v.maxLevel);
                _rowV.Bar.Neutral = v.defaultLevel;
                _rowV.SetEnabled(!_noVib.Contains(m.Key));
                _rowV.Bar.SetQuiet(set.Vibrance);

                NvApi.NV_LEVEL h = HueRange(m);
                _rowH.Bar.Minimum = h.minLevel;
                _rowH.Bar.Maximum = Math.Max(h.minLevel + 1, h.maxLevel);
                _rowH.Bar.Neutral = h.defaultLevel;
                _rowH.SetEnabled(!_noHue.Contains(m.Key));
                _rowH.Bar.SetQuiet(set.Hue);
            }
            _suspend = false;

            UpdateReadouts();
            UpdateSwatch();
            UpdateMonitorInfo();
            UpdateHint();
            _curve.Show(set);

            _lblMixed.Text = (_cboCh.SelectedIndex == 0 && !set.ChannelsMatch)
                ? "channels differ - moving a slider levels all three"
                : "";
        }

        void PushSlidersToState()
        {
            ColourSet set = CurrentSet;
            foreach (char c in ActiveChannels)
            {
                Adjust a = set[c];
                a.Brightness = _rowB.Bar.Value;
                a.Contrast = _rowC.Bar.Value;
                a.Gamma = _rowG.Bar.Value / 100.0;
            }
            set.Vibrance = _rowV.Bar.Value;
            set.Hue = _rowH.Bar.Value;

            if (_chkAll.Checked) MirrorToAll(set);

            // The selected preset tracks your edits - there is no Save button.
            Profile p = _s.Find(SelectedProfileName);
            if (p != null) p.Set = set.Clone();

            UpdateReadouts();
            _curve.Show(set);
            _lblMixed.Text = (_cboCh.SelectedIndex == 0 && !set.ChannelsMatch)
                ? "channels differ - moving a slider levels all three"
                : "";
        }

        void MirrorToAll(ColourSet set)
        {
            foreach (MonitorInfo m in _mons)
            {
                ColourSet t = _s.ForMonitor(m.Key);
                if (!ReferenceEquals(t, set))
                {
                    ColourSet c = set.Clone();
                    t.R = c.R; t.G = c.G; t.B = c.B;
                    t.Vibrance = c.Vibrance; t.Hue = c.Hue;
                }
            }
        }

        void ApplySetToTargets(ColourSet source)
        {
            foreach (MonitorInfo m in Targets)
            {
                ColourSet t = _s.ForMonitor(m.Key);
                ColourSet c = source.Clone();
                t.R = c.R; t.G = c.G; t.B = c.B;
                t.Vibrance = c.Vibrance; t.Hue = c.Hue;
            }
        }

        void UpdateReadouts()
        {
            _rowB.UpdateReadout();
            _rowC.UpdateReadout();
            _rowG.UpdateReadout();
            _rowV.UpdateReadout();
            _rowH.UpdateReadout();
        }

        void UpdateSwatch()
        {
            Color c;
            switch (_cboCh.SelectedIndex)
            {
                case 1: c = Theme.Red; break;
                case 2: c = Theme.Green; break;
                case 3: c = Theme.Blue; break;
                default: c = Theme.Faint; break;
            }
            _swatch.BackColor = c;
            _rowB.Bar.Fill = c == Theme.Faint ? Theme.Accent : c;
            _rowC.Bar.Fill = _rowB.Bar.Fill;
            _rowG.Bar.Fill = _rowB.Bar.Fill;
            _rowB.Bar.Invalidate(); _rowC.Bar.Invalidate(); _rowG.Bar.Invalidate();
        }

        // ==============================================================
        // Applying
        // ==============================================================
        void ApplyCurrent()
        {
            var problems = new List<string>();
            bool clampedAny = false;
            foreach (MonitorInfo m in Targets)
                ApplyTo(m, _s.ForMonitor(m.Key), problems, ref clampedAny);
            Report(problems, clampedAny, "Applied.");
        }

        void ApplyAll(string okText)
        {
            var problems = new List<string>();
            bool clampedAny = false;
            foreach (MonitorInfo m in _mons)
                ApplyTo(m, _s.ForMonitor(m.Key), problems, ref clampedAny);
            Report(problems, clampedAny, okText);
        }

        void ApplyTo(MonitorInfo m, ColourSet set, List<string> problems, ref bool clampedAny)
        {
            // Populate the capability caches for monitors the user has not
            // selected yet, so the checks below mean something.
            VibRange(m);
            HueRange(m);

            bool clamped;
            if (!Gdi.Apply(m.GdiName, Ramp.Build(set), out clamped))
                problems.Add("gamma failed on " + m.ShortLabel);
            else if (clamped) clampedAny = true;

            if (NvApi.Available)
            {
                if (!_noVib.Contains(m.Key) && !NvApi.SetVibrance(m.GdiName, set.Vibrance))
                    problems.Add("vibrance: " + NvApi.LastError);
                if (!_noHue.Contains(m.Key) && !NvApi.SetHue(m.GdiName, set.Hue))
                    _noHue.Add(m.Key);   // driver lacks hue control; stop trying
            }
        }

        void Report(List<string> problems, bool clamped, string okText)
        {
            if (problems.Count > 0)
            {
                SetStatus(string.Join("   ", problems.ToArray()), Theme.Err);
            }
            else if (clamped)
            {
                SetStatus("Applied, but Windows clamped part of the curve.", Theme.Warn);
                SetHint("To unlock the full range, run as admin then reboot:  reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f");
            }
            else
            {
                SetStatus(okText, Theme.Ok);
            }
        }

        void SetStatus(string text, Color c)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = c;
        }

        void SetHint(string text) { _lblHint.Text = text; }

        // ==============================================================
        // Presets
        // ==============================================================
        string SelectedProfileName
        {
            get
            {
                int i = _cboProf.SelectedIndex;
                if (i < 0 || i >= _profileOrder.Count) return null;
                return _profileOrder[i];
            }
        }

        void RefreshProfiles(string select)
        {
            _suspend = true;
            _cboProf.Items.Clear();
            _profileOrder.Clear();
            int sel = 0;
            for (int i = 0; i < _s.Profiles.Count; i++)
            {
                Profile p = _s.Profiles[i];
                _profileOrder.Add(p.Name);
                bool isDefault = string.Equals(p.Name, _s.DefaultProfile, StringComparison.OrdinalIgnoreCase);
                _cboProf.Items.Add(isDefault ? p.Name + "   (default)" : p.Name);
                if (select != null && string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase)) sel = i;
            }
            if (_cboProf.Items.Count > 0) _cboProf.SelectedIndex = Math.Min(sel, _cboProf.Items.Count - 1);
            _suspend = false;
            UpdateDefaultLabel();
        }

        void UpdateDefaultLabel()
        {
            string d = string.IsNullOrEmpty(_s.DefaultProfile) ? "(none)" : _s.DefaultProfile;
            _lblDefault.Text = "Default preset:  " + d + "      \u00B7      selected at startup, and used by the tray menu";
        }

        void NewProfile()
        {
            string n = Prompt.Ask(this, "New preset", "");
            if (n == null) return;
            if (_s.Find(n) != null)
            {
                MessageBox.Show(this, "A preset named '" + n + "' already exists.", "ChromaPanel");
                return;
            }
            PushSlidersToState();
            _s.Profiles.Add(new Profile(n, CurrentSet.Clone()));
            _s.LastProfile = n;
            RefreshProfiles(n);
            SetStatus("Created preset '" + n + "'.", Theme.Ok);
            QueueSave();
        }

        void RenameProfile()
        {
            string old = SelectedProfileName;
            if (old == null) return;
            string n = Prompt.Ask(this, "Rename preset", old);
            if (n == null || n == old) return;
            if (_s.Find(n) != null)
            {
                MessageBox.Show(this, "A preset named '" + n + "' already exists.", "ChromaPanel");
                return;
            }
            Profile p = _s.Find(old);
            if (p == null) return;
            p.Name = n;
            if (string.Equals(_s.DefaultProfile, old, StringComparison.OrdinalIgnoreCase)) _s.DefaultProfile = n;
            if (string.Equals(_s.LastProfile, old, StringComparison.OrdinalIgnoreCase)) _s.LastProfile = n;
            RefreshProfiles(n);
            QueueSave();
        }

        void DeleteProfile()
        {
            string n = SelectedProfileName;
            if (n == null) return;
            if (_s.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "Keep at least one preset.", "ChromaPanel");
                return;
            }
            if (MessageBox.Show(this, "Delete preset '" + n + "'?", "ChromaPanel", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Profile p = _s.Find(n);
            if (p != null) _s.Profiles.Remove(p);
            if (string.Equals(_s.DefaultProfile, n, StringComparison.OrdinalIgnoreCase))
                _s.DefaultProfile = _s.Profiles[0].Name;
            RefreshProfiles(_s.DefaultProfile);
            SetStatus("Deleted '" + n + "'.", Theme.Muted);
            QueueSave();
        }

        void SetDefaultProfile()
        {
            string n = SelectedProfileName;
            if (n == null) return;
            _s.DefaultProfile = n;
            RefreshProfiles(n);
            SetStatus("'" + n + "' is now the default preset.", Theme.Ok);
            QueueSave();
        }

        void ApplyDefaultPreset()
        {
            Profile p = _s.Find(_s.DefaultProfile);
            if (p == null) return;
            foreach (MonitorInfo m in _mons)
            {
                ColourSet t = _s.ForMonitor(m.Key);
                ColourSet c = p.Set.Clone();
                t.R = c.R; t.G = c.G; t.B = c.B;
                t.Vibrance = c.Vibrance; t.Hue = c.Hue;
            }
            LoadSlidersFromState();
            ApplyAll("Applied default preset '" + p.Name + "'.");
            QueueSave();
        }

        // ==============================================================
        // Reset
        // ==============================================================
        void ResetCurrent()
        {
            foreach (MonitorInfo m in Targets) Neutralise(m);
            LoadSlidersFromState();
            PushSlidersToState();
            ApplyCurrent();
            SetStatus("Reset to neutral.", Theme.Muted);
            QueueSave();
        }

        void ResetAll()
        {
            foreach (MonitorInfo m in _mons) Neutralise(m);
            LoadSlidersFromState();
            ApplyAll("All displays reset to neutral.");
            QueueSave();
        }

        void Neutralise(MonitorInfo m)
        {
            var fresh = new ColourSet();
            fresh.Vibrance = VibRange(m).defaultLevel;
            fresh.Hue = HueRange(m).defaultLevel;
            _s.Monitors[m.Key] = fresh;
        }

        // ==============================================================
        // Saving - automatic, debounced
        // ==============================================================
        void QueueSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        void SaveNow()
        {
            MonitorInfo m = Current;
            if (m != null) _s.LastMonitorKey = m.Key;
            string name = SelectedProfileName;
            if (name != null) _s.LastProfile = name;

            string err = _s.Save();
            if (err != null)
            {
                _lblSaved.Text = "Could not save: " + err;
                _lblSaved.ForeColor = Theme.Err;
                return;
            }
            _lblSaved.Text = "Saved";
            _lblSaved.ForeColor = Theme.Ok;
            _savedFlash.Stop();
            _savedFlash.Start();
        }
    }
}
