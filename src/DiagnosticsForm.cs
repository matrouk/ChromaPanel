using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ChromaPanel
{
    /// <summary>Read-only view of Report.Build, with a copy button.</summary>
    internal class DiagnosticsForm : Form
    {
        readonly TextBox _box;

        public DiagnosticsForm(List<MonitorInfo> mons)
        {
            Text = "ChromaPanel diagnostics";
            ClientSize = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI(9f);
            MinimizeBox = false;
            MaximizeBox = false;

            _box = new TextBox();
            _box.Multiline = true;
            _box.ReadOnly = true;
            _box.ScrollBars = ScrollBars.Both;
            _box.WordWrap = false;
            _box.BackColor = Color.FromArgb(22, 22, 27);
            _box.ForeColor = Theme.Text;
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.Font = Theme.Mono(9f);
            _box.SetBounds(12, 12, 736, 496);
            // TextBox needs CRLF or every line runs together.
            _box.Text = Report.Build(mons).Replace("\r\n", "\n").Replace("\n", "\r\n");
            _box.Select(0, 0);
            Controls.Add(_box);

            var copy = new FlatButton();
            copy.Text = "Copy";
            copy.SetBounds(12, 518, 100, 30);
            copy.Click += delegate
            {
                try { Clipboard.SetText(_box.Text); copy.Text = "Copied"; }
                catch { copy.Text = "Copy failed"; }
            };
            Controls.Add(copy);

            var close = new FlatButton();
            close.Text = "Close";
            close.Primary = true;
            close.SetBounds(648, 518, 100, 30);
            close.DialogResult = DialogResult.OK;
            Controls.Add(close);
            AcceptButton = close;
        }
    }
}
