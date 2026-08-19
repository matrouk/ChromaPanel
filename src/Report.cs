using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ChromaPanel
{
    /// <summary>
    /// Dumps every naming source side by side. When a monitor is labelled
    /// wrongly, this is what says which layer disagreed. Shared by the
    /// Diagnostics window and the dump-displays console tool.
    /// </summary>
    internal static class Report
    {
        public static string Build(List<MonitorInfo> mons)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ChromaPanel " + AppSettings.Version);
            sb.AppendLine("Windows " + Environment.OSVersion.Version + "   " + (IntPtr.Size == 8 ? "x64" : "x86"));
            sb.AppendLine("Settings file: " + AppSettings.FilePath);
            sb.AppendLine();
            sb.AppendLine("NVAPI available : " + NvApi.Available);
            if (!NvApi.Available) sb.AppendLine("NVAPI error     : " + NvApi.LastError);
            sb.AppendLine("CCD used        : " + Displays.CcdUsed);
            if (!string.IsNullOrEmpty(Displays.CcdError)) sb.AppendLine("CCD note        : " + Displays.CcdError);
            sb.AppendLine();
            sb.AppendLine("Displays found  : " + mons.Count);
            sb.AppendLine(new string('-', 78));

            foreach (MonitorInfo m in mons)
            {
                sb.AppendLine();
                sb.AppendLine("[" + m.Index.ToString(CultureInfo.InvariantCulture) + "] " + m.Label);
                sb.AppendLine("  gdi name        : " + m.GdiName + (m.Primary ? "   (primary)" : ""));
                sb.AppendLine("  adapter         : " + m.AdapterString);
                sb.AppendLine("  device path     : " + Blank(m.DevicePath));
                sb.AppendLine("  settings key    : " + m.Key);
                sb.AppendLine("  -- names ------------------------------------------------");
                sb.AppendLine("  EnumDisplayDev  : " + Blank(m.PnpModel) + (m.StaleMonitorRecord ? "   [no ACTIVE record - value may be a monitor that is no longer plugged in]" : ""));
                sb.AppendLine("  CCD friendly    : " + Blank(m.CcdFriendly));
                sb.AppendLine("  EDID name       : " + Blank(m.EdidName));
                sb.AppendLine("  EDID serial     : " + Blank(m.EdidSerial));
                sb.AppendLine("  EDID vendor     : " + Blank(m.VendorCode) + (string.IsNullOrEmpty(m.Vendor) ? "" : "  (" + m.Vendor + ")"));
                sb.AppendLine("  chosen name     : " + m.BaseName);
                sb.AppendLine("  -- connector --------------------------------------------");
                sb.AppendLine("  connector       : " + Blank(m.Connector));
                sb.AppendLine("  outputTech      : " + Raw(m.ConnectorRaw) + "   (from the target-name query)");
                sb.AppendLine("  outputTech path : " + Raw(m.ConnectorRawPath) + "   (from the path record)");
                sb.AppendLine("  connectorInst   : " + m.ConnectorInstance.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(m.CcdNote)) sb.AppendLine("  ccd note        : " + m.CcdNote);
                sb.AppendLine("  -- state ------------------------------------------------");
                sb.AppendLine("  geometry        : " + Blank(m.Geometry) + " at " + m.Bounds.X + "," + m.Bounds.Y);
                sb.AppendLine("  hdr             : supported=" + m.HdrSupported + "  enabled=" + m.HdrEnabled);
                sb.AppendLine("  nvapi           : " + NvApi.Describe(m.GdiName));

                NvApi.NV_LEVEL v;
                if (NvApi.Available && NvApi.GetVibrance(m.GdiName, out v))
                    sb.AppendLine("  vibrance        : cur=" + v.currentLevel + " min=" + v.minLevel + " max=" + v.maxLevel + " def=" + v.defaultLevel);
                else
                    sb.AppendLine("  vibrance        : unavailable (" + NvApi.LastError + ")");

                NvApi.NV_LEVEL h;
                if (NvApi.Available && NvApi.GetHue(m.GdiName, out h))
                    sb.AppendLine("  hue             : cur=" + h.currentLevel + " min=" + h.minLevel + " max=" + h.maxLevel + " def=" + h.defaultLevel);
                else
                    sb.AppendLine("  hue             : unavailable (" + NvApi.LastError + ")");
            }

            sb.AppendLine();
            sb.AppendLine(new string('-', 78));
            sb.AppendLine("outputTechnology codes: 0=VGA 4=DVI 5=HDMI 10=DisplayPort 11=DP embedded");
            sb.AppendLine("                        0x80000000=Internal 0xFFFFFFFF=Other");
            return sb.ToString();
        }

        static string Blank(string s) { return string.IsNullOrEmpty(s) ? "(empty)" : s; }

        static string Raw(uint v)
        {
            if (v == 0xFFFFFFFF) return "0xFFFFFFFF (not reported)";
            return v.ToString(CultureInfo.InvariantCulture) + " = " + Ccd.TechName(v);
        }
    }
}
