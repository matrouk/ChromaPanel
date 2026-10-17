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
        DarkCheck _chkAll, _chkHdr, _chkApplyLaunch, _chkReapply, _chkTray, _chkStartup;
        FlatButton _btnNew, _btnRename, _btnDelete, _btnDefault, _btnReset, _btnDiag, _btnApply;
        Panel _swatch;
        CurvePreview _curve;
        SliderRow _rowB, _rowC, _rowG, _rowV, _rowH;
        NotifyIcon _tray;
        Timer _saveTimer, _displayTimer, _savedFlash, _sharpTimer;

        // desktop sharpening (capture -> shader -> overlay)
        DarkCheck _chkDesk, _chkDeskFull;
        DarkCombo _cboDeskFilter, _cboDeskTarget;
        SliderRow _rowDesk;
        Label _lblDeskTip, _lblDeskNote;
        readonly List<string> _deskTargetKeys = new List<string>();
        readonly DesktopSharpen _desk = new DesktopSharpen();
        Timer _deskStatusTimer;
        ToolStripMenuItem _trayDesk;

        // sharpening (NVIDIA Image Scaling) - lives in the driver, not in our settings
        DarkCheck _chkNis;
        SliderRow _rowSharp;
        Label _lblSharpNote;

        // blackout
        HotkeyBox _hkBlack;
        DarkCombo _cboBlackTarget;
        DarkCheck _chkBlackCapture;
        FlatButton _btnBlackout;
        Label _lblBlackHint;
        readonly List<string> _blackTargetKeys = new List<string>();
        Blackout _blackout;
        const int HOTKEY_BLACKOUT = 0xB1AC;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainForm(AppSettings s, bool startHidden)
        {
            _s = s;
            _startHidden = startHidden;
            BuildUi();
            HookEvents();
            LoadDisplays(true);
            RefreshProfiles(_s.DefaultProfile);
            LoadSlidersFromState();
            SelectProfileForCurrentMonitor();
            UpdateDefaultLabel();
            LoadSharpening();
            UpdateDesktopSharpen();

            if (_s.ApplyOnLaunch) ApplyAll("Restored saved settings.");
            else SetStatus("Ready. Nothing applied yet.", Theme.Muted);
        }

        /// <summary>Something the launch itself did, shown in place of the usual startup status.</summary>
        public void Notice(string text)
        {
            SetStatus(text, Theme.Ok);
        }

        // ==============================================================
        // UI construction
        // ==============================================================
        void BuildUi()
        {
            Text = "ChromaPanel";
            ClientSize = new Size(1220, 808);
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
            sub.SetBounds(904, 18, 300, 18);
            sub.BackColor = Color.Transparent;
            Controls.Add(sub);

            // ---------------- display ----------------
            var cardD = NewCard("Display", 16, 52, 104);

            _cboMon = new DarkCombo();
            _cboMon.SetBounds(14, 30, 560, 24);
            cardD.Controls.Add(_cboMon);

            _lblMonInfo = Muted(14, 60, 560, 16, 8.5f);
            cardD.Controls.Add(_lblMonInfo);

            _chkAll = new DarkCheck();
            _chkAll.Text = "Mirror every change to all displays";
            _chkAll.SetBounds(14, 78, 330, 20);
            _chkAll.Checked = _s.LinkAllMonitors;
            cardD.Controls.Add(_chkAll);

            _chkHdr = new DarkCheck();
            _chkHdr.Text = "HDR";
            _chkHdr.SetBounds(360, 78, 214, 20);
            cardD.Controls.Add(_chkHdr);

            // ---------------- preset ----------------
            var cardP = NewCard("Preset", 16, 168, 96);

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
            var cardC = NewCard("Colour adjustment", 16, 276, 356);

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

            // ---------------- sharpening (right column) ----------------
            var cardN = NewCard("Sharpening", 616, 52, 294);

            // desktop
            _chkDesk = NewCheck(cardN, "Sharpen the desktop", 14, 30, 172);
            _chkDesk.Checked = _s.DesktopSharpenEnabled;

            // Order matches the SharpenFilter enum.
            _cboDeskFilter = new DarkCombo();
            _cboDeskFilter.SetBounds(190, 27, 250, 24);
            _cboDeskFilter.Items.AddRange(new object[]
            {
                "AMD CAS  \u00B7  everyday & text",
                "AMD FSR 1 RCAS  \u00B7  games",
                "Luma sharpen  \u00B7  text, no colour fringes",
                "AdaptiveSharpen  \u00B7  TV shows & video",
                "Anime4K Restore  \u00B7  anime"
            });
            _cboDeskFilter.SelectedIndex = (int)_s.DesktopFilter;
            cardN.Controls.Add(_cboDeskFilter);

            _cboDeskTarget = new DarkCombo();
            _cboDeskTarget.SetBounds(446, 27, 128, 24);
            _cboDeskTarget.DropDownWidth = 280;
            cardN.Controls.Add(_cboDeskTarget);

            _rowDesk = NewRow(cardN, "Desktop strength", 58, 0, 100, _s.DesktopSharpenStrength, 1.0, "%", "0");
            _rowDesk.Bar.Fill = Color.FromArgb(56, 189, 168);
            _chkDeskFull = NewCheck(cardN, "Sharpen fullscreen games too  (adds about a frame of lag; browsers still pause)", 14, 106, 560);
            _chkDeskFull.Checked = _s.DesktopSharpenFullscreen;
            _lblDeskTip = Muted(14, 130, 560, 16, 8f);
            _lblDeskTip.AutoEllipsis = true;
            cardN.Controls.Add(_lblDeskTip);
            _lblDeskNote = Muted(14, 146, 560, 16, 8f);
            _lblDeskNote.AutoEllipsis = true;
            cardN.Controls.Add(_lblDeskNote);

            var sep = new Panel();
            sep.SetBounds(14, 169, 560, 1);
            sep.BackColor = Theme.CardEdge;
            cardN.Controls.Add(sep);

            // games
            _chkNis = NewCheck(cardN, "Sharpen games with NVIDIA Image Scaling (NIS)", 14, 176, 400);
            _rowSharp = NewRow(cardN, "Game strength", 202, 0, 100, Sharpening.DefaultPercent, 1.0, "%", "0");
            _rowSharp.Bar.Fill = Color.FromArgb(56, 189, 168);
            _lblSharpNote = Muted(14, 250, 560, 40, 8f);
            cardN.Controls.Add(_lblSharpNote);

            // ---------------- blackout ----------------
            var cardB = NewCard("Blackout", 616, 358, 150);

            var lblKey = new Label();
            lblKey.Text = "Shortcut";
            lblKey.SetBounds(14, 33, 66, 20);
            lblKey.ForeColor = Theme.Text;
            lblKey.BackColor = Color.Transparent;
            cardB.Controls.Add(lblKey);

            _hkBlack = new HotkeyBox();
            _hkBlack.SetBounds(82, 30, 172, 24);
            _hkBlack.Value = Hotkey.Parse(_s.BlackoutHotkey);
            cardB.Controls.Add(_hkBlack);

            var lblOn = new Label();
            lblOn.Text = "Displays";
            lblOn.SetBounds(272, 33, 64, 20);
            lblOn.ForeColor = Theme.Text;
            lblOn.BackColor = Color.Transparent;
            cardB.Controls.Add(lblOn);

            _cboBlackTarget = new DarkCombo();
            _cboBlackTarget.SetBounds(338, 29, 236, 24);
            cardB.Controls.Add(_cboBlackTarget);

            _chkBlackCapture = NewCheck(cardB, "Keep OBS / screen capture recording what's underneath", 14, 62, 560);
            _chkBlackCapture.Checked = _s.BlackoutHideFromCapture;

            _btnBlackout = NewButton(cardB, "Black out now", 14, 94, 130);
            _lblBlackHint = Muted(156, 92, 418, 50, 8f);
            _lblBlackHint.Text = "Pure black, so OLED pixels switch off. A key, click, scroll or a deliberate mouse move brings it back. Nothing underneath loses focus.";
            cardB.Controls.Add(_lblBlackHint);

            // ---------------- preview + status ----------------
            var cardS = NewCard("Response", 16, 644, 1188, 104);

            _curve = new CurvePreview();
            _curve.SetBounds(14, 28, 180, 62);
            cardS.Controls.Add(_curve);

            _lblStatus = new Label();
            _lblStatus.SetBounds(206, 28, 968, 20);
            _lblStatus.ForeColor = Theme.Muted;
            _lblStatus.BackColor = Color.Transparent;
            _lblStatus.Font = Theme.UI(9f);
            _lblStatus.AutoEllipsis = true;   // never cut a message off silently
            cardS.Controls.Add(_lblStatus);

            _lblHint = Muted(206, 50, 968, 40, 8f);
            cardS.Controls.Add(_lblHint);

            // ---------------- options ----------------
            var cardO = NewCard("Options", 616, 520, 112);

            _chkApplyLaunch = NewCheck(cardO, "Apply saved settings at launch", 14, 26, 270);
            _chkReapply = NewCheck(cardO, "Re-apply after display or power changes", 14, 50, 270);
            _chkTray = NewCheck(cardO, "Close to tray instead of exiting", 296, 26, 278);
            _chkStartup = NewCheck(cardO, "Start with Windows", 296, 50, 278);

            _chkApplyLaunch.Checked = _s.ApplyOnLaunch;
            _chkReapply.Checked = _s.ReapplyOnDisplayChange;
            _chkTray.Checked = _s.MinimiseToTray;
            _chkStartup.Checked = AppSettings.GetStartWithWindows();

            // ---------------- bottom bar ----------------
            _btnReset = NewButton(this, "Reset to neutral", 16, 760, 140);
            _btnDiag = NewButton(this, "Diagnostics", 164, 760, 110);

            _lblSaved = new Label();
            _lblSaved.SetBounds(866, 768, 220, 18);
            _lblSaved.TextAlign = ContentAlignment.MiddleRight;
            _lblSaved.ForeColor = Theme.Faint;
            _lblSaved.Font = Theme.UI(8f);
            _lblSaved.BackColor = Color.Transparent;
            _lblSaved.Text = "Changes save automatically";
            Controls.Add(_lblSaved);

            _btnApply = NewButton(this, "Apply now", 1094, 760, 110);
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
            menu.Items.Add("Black out screens", null, delegate { ToggleBlackout(); });
            _trayDesk = new ToolStripMenuItem("Sharpen the desktop");
            _trayDesk.Checked = _s.DesktopSharpenEnabled;
            _trayDesk.Click += delegate { _chkDesk.Checked = !_chkDesk.Checked; };
            menu.Items.Add(_trayDesk);
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

            // Driver-settings writes are a session + save each time, so do not
            // fire one per pixel of slider drag.
            _sharpTimer = new Timer();
            _sharpTimer.Interval = 350;
            _sharpTimer.Tick += delegate { _sharpTimer.Stop(); ApplySharpening(); };

            _deskStatusTimer = new Timer();
            _deskStatusTimer.Interval = 500;
            _deskStatusTimer.Tick += delegate { UpdateDeskNote(); };
            _deskStatusTimer.Start();

            _savedFlash = new Timer();
            _savedFlash.Interval = 1400;
            _savedFlash.Tick += delegate
            {
                _savedFlash.Stop();
                _lblSaved.ForeColor = Theme.Faint;
                _lblSaved.Text = "Changes save automatically";
            };
        }

        Card NewCard(string heading, int x, int y, int h) { return NewCard(heading, x, y, 588, h); }

        Card NewCard(string heading, int x, int y, int w, int h)
        {
            var c = new Card();
            c.Heading = heading;
            c.SetBounds(x, y, w, h);
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
            RegisterBlackoutHotkey(false);
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
            // A newer version is taking over: save and exit as if Exit were chosen from the tray.
            else if (m.Msg == SingleInstance.WM_QUIT_FOR_NEWER && m.Msg != 0) { _reallyClose = true; Close(); return; }
            else if (m.Msg == Hotkey.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_BLACKOUT) ToggleBlackout();
            base.WndProc(ref m);
        }

        // ==============================================================
        // Events
        // ==============================================================
        void HookEvents()
        {
            // Each slider writes only its own property. A shared handler that
            // committed all three is what let contrast clobber brightness.
            _rowB.Bar.ValueChanged += delegate { EditChannel(Adjustable.Brightness, _rowB.Bar.Value); };
            _rowC.Bar.ValueChanged += delegate { EditChannel(Adjustable.Contrast, _rowC.Bar.Value); };
            _rowG.Bar.ValueChanged += delegate { EditChannel(Adjustable.Gamma, _rowG.Bar.Value / 100.0); };
            _rowV.Bar.ValueChanged += delegate
            {
                if (_suspend) return;
                CurrentSet.Vibrance = _rowV.Bar.Value;
                CommitEdit(); ApplyCurrent(); QueueSave();
            };
            _rowH.Bar.ValueChanged += delegate
            {
                if (_suspend) return;
                CurrentSet.Hue = _rowH.Bar.Value;
                CommitEdit(); ApplyCurrent(); QueueSave();
            };

            _cboMon.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                LoadSlidersFromState();
                // Follow the display: show the preset that display is on, not
                // whatever the previous display happened to be using.
                SelectProfileForCurrentMonitor();
                MonitorInfo m = Current;
                if (m != null) { _s.LastMonitorKey = m.Key; QueueSave(); }
            };

            _chkHdr.CheckedChanged += delegate
            {
                if (_suspend) return;
                ToggleHdr(_chkHdr.Checked);
            };

            _cboCh.SelectedIndexChanged += delegate { if (!_suspend) LoadSlidersFromState(); };

            _cboProf.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                string name = SelectedProfileName;
                Profile p = _s.Find(name);
                if (p == null) return;
                _s.LastProfile = name;
                // Remember this choice against the display(s) it was made for.
                foreach (MonitorInfo t in Targets) _s.MonitorProfile[t.Key] = name;
                ApplySetToTargets(p.Set);
                LoadSlidersFromState();
                ApplyCurrent();
                SetStatus("Applied preset '" + name + "'.", Theme.Ok);
                QueueSave();
            };

            _chkAll.CheckedChanged += delegate
            {
                _s.LinkAllMonitors = _chkAll.Checked;
                if (_chkAll.Checked) { CommitEdit(); ApplyCurrent(); }
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

            // desktop sharpening
            _chkDesk.CheckedChanged += delegate
            {
                _s.DesktopSharpenEnabled = _chkDesk.Checked;
                if (_trayDesk != null) _trayDesk.Checked = _chkDesk.Checked;
                UpdateDesktopSharpen();
                QueueSave();
            };
            _cboDeskFilter.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                _s.DesktopFilter = (SharpenFilter)Math.Max(0, _cboDeskFilter.SelectedIndex);
                _desk.SetParams(_s.DesktopFilter, _s.DesktopSharpenStrength, _s.DesktopSharpenFullscreen);
                UpdateDeskNote();
                QueueSave();
            };
            _chkDeskFull.CheckedChanged += delegate
            {
                _s.DesktopSharpenFullscreen = _chkDeskFull.Checked;
                _desk.SetParams(_s.DesktopFilter, _s.DesktopSharpenStrength, _s.DesktopSharpenFullscreen);   // live
                QueueSave();
            };
            _cboDeskTarget.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                int i = _cboDeskTarget.SelectedIndex;
                _s.DesktopSharpenTarget = (i >= 0 && i < _deskTargetKeys.Count) ? _deskTargetKeys[i] : "";
                UpdateDesktopSharpen();
                QueueSave();
            };
            _rowDesk.Bar.ValueChanged += delegate
            {
                if (_suspend) return;
                _rowDesk.UpdateReadout();
                _s.DesktopSharpenStrength = _rowDesk.Bar.Value;
                _desk.SetParams(_s.DesktopFilter, _s.DesktopSharpenStrength, _s.DesktopSharpenFullscreen);   // live, no restart
                QueueSave();
            };

            // sharpening
            _chkNis.CheckedChanged += delegate { if (!_suspend) { UpdateSharpNote(); QueueSharpen(); } };
            _rowSharp.Bar.ValueChanged += delegate
            {
                if (_suspend) return;
                _rowSharp.UpdateReadout();
                if (_chkNis.Checked) QueueSharpen();
            };

            // blackout
            _btnBlackout.Click += delegate { ToggleBlackout(); };
            // Recording a new shortcut must not fire the old one.
            _hkBlack.GotFocus += delegate { Hotkey.Unregister(Handle, HOTKEY_BLACKOUT); };
            _hkBlack.LostFocus += delegate { RegisterBlackoutHotkey(false); };
            _hkBlack.HotkeyChanged += delegate
            {
                _s.BlackoutHotkey = _hkBlack.Value.ToString();
                RegisterBlackoutHotkey(true);
                QueueSave();
            };
            _cboBlackTarget.SelectedIndexChanged += delegate
            {
                if (_suspend) return;
                int i = _cboBlackTarget.SelectedIndex;
                _s.BlackoutTarget = (i >= 0 && i < _blackTargetKeys.Count) ? _blackTargetKeys[i] : "";
                QueueSave();
            };
            _chkBlackCapture.CheckedChanged += delegate
            {
                _s.BlackoutHideFromCapture = _chkBlackCapture.Checked;
                QueueSave();
            };

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
                if (_blackout != null) _blackout.Stop();
                _desk.Stop();
                try { Hotkey.Unregister(Handle, HOTKEY_BLACKOUT); } catch { }
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
            SelectProfileForCurrentMonitor();
            UpdateDesktopSharpen();   // displays moved, resized or changed HDR state
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

            RefreshBlackoutTargets();
            RefreshDeskTargets();
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
                SetHint("Hue could not be read for this display, so that slider is disabled. Diagnostics shows the NVAPI status.");
                return;
            }
            SetHint("");
        }

        /// <summary>Drives the Windows HDR toggle for the selected display.</summary>
        void ToggleHdr(bool on)
        {
            MonitorInfo m = Current;
            if (m == null) return;
            if (!m.HasTarget)
            {
                SetStatus("Cannot reach this display's HDR setting.", Theme.Err);
                SyncHdrCheck();
                return;
            }

            string err;
            if (!Ccd.SetHdr(m.AdapterLow, m.AdapterHigh, m.TargetId, on, out err))
            {
                SetStatus("Could not turn HDR " + (on ? "on" : "off") + ": " + err, Theme.Err);
                SyncHdrCheck();
                return;
            }

            SetStatus("HDR turned " + (on ? "on" : "off") + " for " + m.BaseName + ".", Theme.Ok);
            // Windows reconfigures the display; the resulting
            // DisplaySettingsChanged re-enumerates and re-applies for us.
            _displayTimer.Stop();
            _displayTimer.Start();
        }

        /// <summary>Reflects the real HDR state without firing the handler.</summary>
        void SyncHdrCheck()
        {
            MonitorInfo m = Current;
            bool wasSuspended = _suspend;
            _suspend = true;
            if (m == null)
            {
                _chkHdr.Enabled = false;
                _chkHdr.Checked = false;
            }
            else
            {
                _chkHdr.Enabled = m.HdrSupported && m.HasTarget;
                _chkHdr.Checked = m.HdrEnabled;
            }
            _chkHdr.Invalidate();
            _suspend = wasSuspended;
        }

        // ==============================================================
        // Sharpening (NVIDIA Image Scaling)
        // ==============================================================
        void LoadSharpening()
        {
            Sharpening.State st = Sharpening.Read();
            bool usable = st.Available && Sharpening.WritesVerified;
            _suspend = true;
            _chkNis.Enabled = usable;
            _chkNis.Checked = st.Enabled;
            _rowSharp.Bar.SetQuiet(st.Percent);
            _rowSharp.SetEnabled(usable);
            _suspend = false;
            _rowSharp.UpdateReadout();
            UpdateSharpNote();
            if (!st.Available) _lblSharpNote.Text = "Unavailable: " + st.Error;
            else if (!Sharpening.WritesVerified) _lblSharpNote.Text = Sharpening.PendingNote;
        }

        void UpdateSharpNote()
        {
            _lblSharpNote.Text = _chkNis.Checked
                ? "On for every game, on every display. Games below native resolution are upscaled with NIS; at native they are sharpened only. Restart a game to see a change. The Windows desktop is not affected."
                : "Off (driver default). Turning it on is the same as NVIDIA Control Panel > Manage 3D settings > Image Scaling, and applies to games only.";
        }

        void QueueSharpen()
        {
            _sharpTimer.Stop();
            _sharpTimer.Start();
        }

        void ApplySharpening()
        {
            string err;
            bool on = _chkNis.Checked;
            int pct = _rowSharp.Bar.Value;
            if (!Sharpening.Write(on, pct, out err))
            {
                SetStatus("Could not change sharpening: " + err, Theme.Err);
                LoadSharpening();   // show what the driver actually has
                return;
            }
            SetStatus(on ? "Game sharpening " + pct + "% - restart a game to see it." : "Game sharpening off.", Theme.Ok);
        }

        // ==============================================================
        // Desktop sharpening
        // ==============================================================
        void RefreshDeskTargets()
        {
            _suspend = true;
            _cboDeskTarget.Items.Clear();
            _deskTargetKeys.Clear();
            _cboDeskTarget.Items.Add("All displays");
            _deskTargetKeys.Add("");
            int sel = 0;
            foreach (MonitorInfo m in _mons)
            {
                _cboDeskTarget.Items.Add(m.ShortLabel);
                _deskTargetKeys.Add(m.Key);
                if (string.Equals(m.Key, _s.DesktopSharpenTarget, StringComparison.OrdinalIgnoreCase))
                    sel = _deskTargetKeys.Count - 1;
            }
            _cboDeskTarget.SelectedIndex = sel;
            _suspend = false;
        }

        /// <summary>Starts, restarts or stops the overlays to match the settings.</summary>
        void UpdateDesktopSharpen()
        {
            if (!_s.DesktopSharpenEnabled) { _desk.Stop(); UpdateDeskNote(); return; }
            List<MonitorInfo> targets = DesktopSharpen.TargetsFor(_mons, _s.DesktopSharpenTarget);
            if (targets.Count == 0)
            {
                _desk.Stop();
                _lblDeskNote.Text = "The chosen display is not connected.";
                return;
            }
            _desk.Start(targets, _s.DesktopFilter, _s.DesktopSharpenStrength, _s.DesktopSharpenFullscreen);
            UpdateDeskNote();
        }

        /// <summary>What the selected filter is good at - shown under the picker.</summary>
        static string FilterTip(SharpenFilter f)
        {
            switch (f)
            {
                case SharpenFilter.Rcas: return "FSR 1's sharpener: crisper than CAS, never overshoots its neighbours. Good for games.";
                case SharpenFilter.Luma: return "Sharpens brightness only, so text gains no coloured fringes. The gentlest option.";
                case SharpenFilter.Adaptive: return "Made for video that has already been resized, with anti-ringing. Best for TV and films.";
                case SharpenFilter.Anime4K: return "Neural network that rebuilds line art. Superb on anime, but makes text look heavy.";
                default: return "Adaptive: sharpens soft areas more than sharp ones. The safe all-rounder, kind to text.";
            }
        }

        void UpdateDeskNote()
        {
            if (_lblDeskNote == null) return;
            string second = _desk.Running
                ? _desk.Describe()
                : "Off. While on, OBS still records the plain desktop, and windowed DRM video goes black.";
            _lblDeskTip.Text = FilterTip(_s.DesktopFilter);
            _lblDeskNote.Text = second;
        }

        // ==============================================================
        // Blackout
        // ==============================================================
        void RefreshBlackoutTargets()
        {
            _suspend = true;
            _cboBlackTarget.Items.Clear();
            _blackTargetKeys.Clear();
            _cboBlackTarget.Items.Add("All displays");
            _blackTargetKeys.Add("");
            int sel = 0;
            foreach (MonitorInfo m in _mons)
            {
                _cboBlackTarget.Items.Add(m.ShortLabel);
                _blackTargetKeys.Add(m.Key);
                if (string.Equals(m.Key, _s.BlackoutTarget, StringComparison.OrdinalIgnoreCase))
                    sel = _blackTargetKeys.Count - 1;
            }
            _cboBlackTarget.SelectedIndex = sel;
            _suspend = false;
        }

        /// <param name="report">Say so in the status line - only when the user just changed it.</param>
        void RegisterBlackoutHotkey(bool report)
        {
            if (!IsHandleCreated) return;
            Hotkey.Unregister(Handle, HOTKEY_BLACKOUT);
            Hotkey hk = Hotkey.Parse(_s.BlackoutHotkey);
            if (hk.IsEmpty)
            {
                if (report) SetStatus("Blackout shortcut cleared.", Theme.Muted);
                return;
            }
            if (hk.Register(Handle, HOTKEY_BLACKOUT))
            {
                if (report) SetStatus("Blackout shortcut is now " + hk + ".", Theme.Ok);
            }
            else
            {
                SetStatus(hk + " is in use by another app - pick another.", Theme.Warn);
            }
        }

        void ToggleBlackout()
        {
            if (_blackout == null)
            {
                _blackout = new Blackout(this);
                _blackout.Ended += delegate { SetStatus("Blackout ended.", Theme.Muted); };
            }
            if (_blackout.Active) { _blackout.Stop(); return; }

            List<Rectangle> areas = Blackout.AreasFor(_mons, _s.BlackoutTarget);
            if (areas.Count == 0) { SetStatus("No display to black out.", Theme.Err); return; }
            _blackout.Start(areas, _s.BlackoutHideFromCapture);
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
            SyncHdrCheck();
            UpdateHint();
            _curve.Show(set);

            UpdateMixedHint(set);
        }

        /// <summary>One slider moved: write that property to the selected channels only.</summary>
        void EditChannel(Adjustable p, double value)
        {
            if (_suspend) return;
            CurrentSet.Set(p, ActiveChannels, value);
            CommitEdit();
            ApplyCurrent();
            QueueSave();
        }

        /// <summary>Mirror, sync the preset, and refresh the read-outs after any edit.</summary>
        void CommitEdit()
        {
            ColourSet set = CurrentSet;
            if (_chkAll.Checked) MirrorToAll(set);

            // The selected preset tracks your edits - there is no Save button.
            Profile p = _s.Find(SelectedProfileName);
            if (p != null) p.Set = set.Clone();

            UpdateReadouts();
            _curve.Show(set);
            UpdateMixedHint(set);
        }

        /// <summary>Names the properties that currently differ between channels.</summary>
        void UpdateMixedHint(ColourSet set)
        {
            if (_cboCh.SelectedIndex != 0) { _lblMixed.Text = ""; return; }

            var differing = new List<string>();
            if (!set.Uniform(Adjustable.Brightness)) differing.Add("brightness");
            if (!set.Uniform(Adjustable.Contrast)) differing.Add("contrast");
            if (!set.Uniform(Adjustable.Gamma)) differing.Add("gamma");

            _lblMixed.Text = differing.Count == 0
                ? ""
                : "per-channel " + string.Join("/", differing.ToArray()) + " - shows red";
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

        /// <summary>
        /// Points the preset box at whatever preset the current display is on,
        /// without applying anything - the display's own values are already
        /// loaded, so re-applying would be pointless work and a visible flicker.
        /// </summary>
        void SelectProfileForCurrentMonitor()
        {
            MonitorInfo m = Current;
            if (m == null) return;
            string want = _s.ProfileForMonitor(m.Key);
            if (want == null) return;

            _suspend = true;
            for (int i = 0; i < _profileOrder.Count; i++)
            {
                if (string.Equals(_profileOrder[i], want, StringComparison.OrdinalIgnoreCase))
                {
                    _cboProf.SelectedIndex = i;
                    break;
                }
            }
            _suspend = false;
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
            _s.Profiles.Add(new Profile(n, CurrentSet.Clone()));
            _s.LastProfile = n;
            foreach (MonitorInfo t in Targets) _s.MonitorProfile[t.Key] = n;
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
            var rebound = new List<string>();
            foreach (KeyValuePair<string, string> kv in _s.MonitorProfile)
                if (string.Equals(kv.Value, old, StringComparison.OrdinalIgnoreCase)) rebound.Add(kv.Key);
            foreach (string k in rebound) _s.MonitorProfile[k] = n;
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
            var orphaned = new List<string>();
            foreach (KeyValuePair<string, string> kv in _s.MonitorProfile)
                if (string.Equals(kv.Value, n, StringComparison.OrdinalIgnoreCase)) orphaned.Add(kv.Key);
            foreach (string k in orphaned) _s.MonitorProfile.Remove(k);
            RefreshProfiles(_s.DefaultProfile);
            SelectProfileForCurrentMonitor();
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
            CommitEdit();
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
