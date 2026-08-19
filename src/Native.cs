using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ChromaPanel
{
    /// <summary>Everything we know about one physical panel.</summary>
    public class MonitorInfo
    {
        public string GdiName = "";          // \\.\DISPLAY1  - what GDI/NVAPI want
        public string DevicePath = "";       // \\?\DISPLAY#ABC1234#5&..&UID4353#{guid}
        public string AdapterString = "";    // "NVIDIA GeForce RTX ..."
        public string PnpModel = "";         // EnumDisplayDevices monitor string ("Generic PnP Monitor")
        public string CcdFriendly = "";      // CCD monitorFriendlyDeviceName ("TD2455")
        public string EdidName = "";         // EDID 0xFC descriptor
        public string EdidSerial = "";       // EDID 0xFF descriptor, else numeric serial
        public string Vendor = "";           // "ViewSonic" resolved from the 3-letter PnP code
        public string VendorCode = "";       // "VSC"
        public string Connector = "";        // "DisplayPort" / "HDMI" / ...
        public uint ConnectorRaw = 0xFFFFFFFF;      // outputTechnology from the target-name query
        public uint ConnectorRawPath = 0xFFFFFFFF;  // outputTechnology from the path record
        public uint ConnectorInstance = 0;
        public bool Primary;
        public bool HdrSupported;
        public bool HdrEnabled;          // true HDR only, never wide colour gamut
        public bool WideColorEnabled;
        public int ColorMode = -1;       // Ccd.MODE_SDR / MODE_WCG / MODE_HDR
        public int BitsPerColor;
        public bool ColorInfo2Ok;        // the modern API answered
        public Rectangle Bounds;
        public string CcdNote = "";          // why CCD data is missing, if it is
        public bool StaleMonitorRecord;      // adapter had no ACTIVE monitor record

        /// <summary>
        /// Stable id used as the settings key. Derived from the monitor's
        /// device instance (DISPLAY#ABC1234#5&amp;..&amp;UID4353) so it survives
        /// \\.\DISPLAYn renumbering and stays distinct between two panels of
        /// the same model.
        /// </summary>
        public string Key
        {
            get
            {
                string p = DevicePath;
                if (!string.IsNullOrEmpty(p))
                {
                    if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
                    int guid = p.IndexOf("#{", StringComparison.Ordinal);
                    if (guid > 0) p = p.Substring(0, guid);
                    return p;
                }
                if (!string.IsNullOrEmpty(EdidSerial)) return VendorCode + "-" + EdidSerial;
                return GdiName;
            }
        }

        /// <summary>
        /// Best human name for the panel, ignoring uniqueness.
        ///
        /// CCD first: it is what Windows Settings shows and it reads the live
        /// connection. EnumDisplayDevices' string is last because it can name a
        /// monitor that was unplugged months ago.
        /// </summary>
        public string BaseName
        {
            get
            {
                string n = FirstSpecific(CcdFriendly, EdidName, PnpModel);
                if (string.IsNullOrEmpty(n)) n = First(CcdFriendly, EdidName, PnpModel, GdiName);

                // "27GN950" -> "LG 27GN950"
                if (!string.IsNullOrEmpty(Vendor) &&
                    n.IndexOf(Vendor, StringComparison.OrdinalIgnoreCase) < 0 &&
                    !IsGeneric(n))
                {
                    n = Vendor + " " + n;
                }
                return n;
            }
        }

        static string FirstSpecific(params string[] vals)
        {
            foreach (string v in vals) if (!string.IsNullOrEmpty(v) && !IsGeneric(v)) return v;
            return "";
        }

        // Filled in by Displays.Enumerate once every monitor is known, so the
        // text is guaranteed unique across the list.
        public string Label = "";
        public string ShortLabel = "";

        public int Index;   // 1-based, matches \\.\DISPLAYn where possible

        static bool IsGeneric(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            string t = s.Trim();
            return t.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase)
                || t.Equals("Default Monitor", StringComparison.OrdinalIgnoreCase)
                || t.Equals("PnP-Monitor (Standard)", StringComparison.OrdinalIgnoreCase);
        }

        static string First(params string[] vals)
        {
            foreach (string v in vals) if (!string.IsNullOrEmpty(v)) return v;
            return "";
        }

        public string Geometry
        {
            get
            {
                if (Bounds.Width == 0) return "";
                return string.Format(CultureInfo.InvariantCulture, "{0}x{1}", Bounds.Width, Bounds.Height);
            }
        }

        public string ConnectorLabel
        {
            get
            {
                if (string.IsNullOrEmpty(Connector)) return "";
                if (ConnectorInstance > 0)
                    return Connector + " #" + (ConnectorInstance + 1).ToString(CultureInfo.InvariantCulture);
                return Connector;
            }
        }
    }

    // =====================================================================
    // GDI gamma ramp
    // =====================================================================
    internal static class Gdi
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateDC(string driver, string device, string port, IntPtr pd);
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        /// <summary>
        /// Writes the ramp, reads it back and reports whether Windows clamped it.
        /// A per-display DC from CreateDC is mandatory: GetDC(NULL) silently
        /// fails on multi-monitor setups.
        /// </summary>
        public static bool Apply(string gdiName, ushort[] ramp, out bool clamped)
        {
            clamped = false;
            IntPtr hdc = CreateDC(gdiName, gdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try
            {
                if (!SetDeviceGammaRamp(hdc, ramp)) return false;
                var back = new ushort[768];
                if (GetDeviceGammaRamp(hdc, back))
                {
                    for (int i = 0; i < 768; i += 37)
                    {
                        int diff = back[i] - ramp[i];
                        if (diff < 0) diff = -diff;
                        if (diff > 600) { clamped = true; break; }
                    }
                }
                return true;
            }
            finally { DeleteDC(hdc); }
        }
    }

    // =====================================================================
    // EnumDisplayDevices
    // =====================================================================
    internal static class DisplayDevices
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;   // adapter records
        const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;        // adapter records
        const int DISPLAY_DEVICE_ACTIVE = 0x1;                // monitor records
        const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

        public static List<MonitorInfo> Enumerate()
        {
            var list = new List<MonitorInfo>();
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                var m = new MonitorInfo();
                m.GdiName = dd.DeviceName;
                m.AdapterString = dd.DeviceString;
                m.Primary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

                // Find the ACTIVE monitor on this adapter.
                //
                // Monitor index 0 is NOT the attached panel. Windows keeps a
                // record for every monitor that was ever plugged into the
                // adapter, stale ones first, flagged inactive. Hard-coding
                // index 0 handed back the same long-replaced monitor for every
                // display - same name, same device path - which is why two
                // different panels showed one name and shared one settings
                // entry. Take the first record with DISPLAY_DEVICE_ACTIVE set.
                uint monIndex = 0;
                bool haveAny = false, haveActive = false;
                for (uint j = 0; ; j++)
                {
                    var cand = new DISPLAY_DEVICE();
                    cand.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (!EnumDisplayDevices(dd.DeviceName, j, ref cand, 0)) break;
                    if (!haveAny) { monIndex = j; haveAny = true; }
                    if ((cand.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0)
                    {
                        monIndex = j;
                        haveActive = true;
                        m.PnpModel = (cand.DeviceString ?? "").Trim();
                        break;
                    }
                    if (j == 0) m.PnpModel = (cand.DeviceString ?? "").Trim();
                }
                if (!haveActive && haveAny) m.StaleMonitorRecord = true;

                // Interface-name form: \\?\DISPLAY#ABC1234#5&..&UID4353#{guid},
                // byte-for-byte what CCD reports as monitorDevicePath.
                if (haveAny)
                {
                    var mon2 = new DISPLAY_DEVICE();
                    mon2.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                    if (EnumDisplayDevices(dd.DeviceName, monIndex, ref mon2, EDD_GET_DEVICE_INTERFACE_NAME))
                        m.DevicePath = (mon2.DeviceID ?? "").Trim();
                }

                list.Add(m);
            }
            return list;
        }
    }

    // =====================================================================
    // CCD - real monitor names, connector type, HDR state
    // =====================================================================
    internal static class Ccd
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }

        // 20 bytes, not 16. The trailing statusFlags is easy to miss because
        // MSDN documents it after an anonymous union; leaving it out makes
        // PATH_INFO 68 bytes instead of 72, so QueryDisplayConfig writes four
        // bytes past every element (heap corruption) and every path after the
        // first is read at the wrong offset - which is what smeared one
        // monitor's name and connector across all of them.
        [StructLayout(LayoutKind.Sequential)]
        struct PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RATIONAL { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

        // Deliberately no ByValArray byte[] here: a null array field inside a
        // marshalled struct array made Windows write past the buffer and killed
        // the process with STATUS_HEAP_CORRUPTION. Plain uints, fixed size.
        [StructLayout(LayoutKind.Sequential)]
        struct MODE_INFO
        {
            public uint infoType; public uint id; public LUID adapterId;
            public uint b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10, b11;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SOURCE_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        // Legacy (type 9). Its "advancedColorEnabled" bit does NOT mean HDR:
        // it is set for wide-colour-gamut mode too, so on Windows 11 it reports
        // a plain WCG display as HDR. Only used when INFO_2 is unavailable.
        [StructLayout(LayoutKind.Sequential)]
        struct ADVANCED_COLOR_INFO
        {
            public DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        // Windows 11 24H2+ (type 15). activeColorMode finally distinguishes
        // SDR / WCG / HDR, which is the only way to answer "is HDR actually on".
        [StructLayout(LayoutKind.Sequential)]
        struct ADVANCED_COLOR_INFO_2
        {
            public DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode;
        }

        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);
        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPath, IntPtr paths, ref uint numMode, IntPtr modes, IntPtr current);
        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);
        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO r);
        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO_2 r);

        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        const uint GET_SOURCE_NAME = 1;
        const uint GET_TARGET_NAME = 2;
        const uint GET_ADVANCED_COLOR_INFO = 9;
        const uint GET_ADVANCED_COLOR_INFO_2 = 15;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        // activeColorMode
        public const int MODE_UNKNOWN = -1;
        public const int MODE_SDR = 0;
        public const int MODE_WCG = 1;
        public const int MODE_HDR = 2;

        public static string ModeName(int m)
        {
            switch (m)
            {
                case MODE_SDR: return "SDR";
                case MODE_WCG: return "Wide colour gamut";
                case MODE_HDR: return "HDR";
                default: return "unknown";
            }
        }

        public class Entry
        {
            public string GdiName = "";
            public string DevicePath = "";
            public string Friendly = "";
            public string Connector = "";
            public uint ConnectorRaw = 0xFFFFFFFF;
            public uint ConnectorRawPath = 0xFFFFFFFF;
            public uint ConnectorInstance;
            public bool HdrSupported;
            public bool HdrEnabled;
            public bool WideColorEnabled;
            public int ColorMode = MODE_UNKNOWN;
            public int BitsPerColor;
            public bool ColorInfo2Ok;
            public bool TargetNameOk;
        }

        public static string LastError = "";

        public static string TechName(uint t)
        {
            switch (t)
            {
                case 0: return "VGA";
                case 1: return "S-Video";
                case 2: return "Composite";
                case 3: return "Component";
                case 4: return "DVI";
                case 5: return "HDMI";
                case 6: return "LVDS";
                case 8: return "D-Jpn";
                case 9: return "SDI";
                case 10: return "DisplayPort";
                case 11: return "DisplayPort (embedded)";
                case 12: return "UDI";
                case 13: return "UDI (embedded)";
                case 14: return "SDTV dongle";
                case 15: return "Miracast";
                case 16: return "Indirect wired";
                case 17: return "Indirect virtual";
                case 0x80000000: return "Internal";
                case 0xFFFFFFFF: return "Other";
                default: return "Type " + t.ToString(CultureInfo.InvariantCulture);
            }
        }

        public static List<Entry> Query()
        {
            var result = new List<Entry>();
            IntPtr pPaths = IntPtr.Zero, pModes = IntPtr.Zero;
            try
            {
                int pathSize = Marshal.SizeOf(typeof(PATH_INFO));
                int modeSize = Marshal.SizeOf(typeof(MODE_INFO));

                // A wrong size here corrupts the process heap, so refuse to
                // call. 72 is the only correct value - do not widen this.
                if (pathSize != 72)
                { LastError = "unexpected PATH_INFO size " + pathSize + " (expected 72)"; return result; }
                if (modeSize != 64)
                { LastError = "unexpected MODE_INFO size " + modeSize; return result; }

                uint nPath, nMode;
                int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out nPath, out nMode);
                if (rc != 0) { LastError = "GetDisplayConfigBufferSizes rc=" + rc; return result; }
                if (nPath == 0) { LastError = "no active paths"; return result; }

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    pPaths = Marshal.AllocHGlobal((int)nPath * pathSize);
                    pModes = Marshal.AllocHGlobal((int)Math.Max(1, nMode) * modeSize);

                    uint gotPath = nPath, gotMode = nMode;
                    rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref gotPath, pPaths, ref gotMode, pModes, IntPtr.Zero);
                    if (rc == 0) { nPath = gotPath; nMode = gotMode; break; }

                    Marshal.FreeHGlobal(pPaths); pPaths = IntPtr.Zero;
                    Marshal.FreeHGlobal(pModes); pModes = IntPtr.Zero;

                    // The layout can change between the size query and the read.
                    if (rc != ERROR_INSUFFICIENT_BUFFER)
                    { LastError = "QueryDisplayConfig rc=" + rc; return result; }
                    rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out nPath, out nMode);
                    if (rc != 0) { LastError = "GetDisplayConfigBufferSizes rc=" + rc; return result; }
                }
                if (pPaths == IntPtr.Zero) { LastError = "QueryDisplayConfig kept resizing"; return result; }

                for (int i = 0; i < nPath; i++)
                {
                    IntPtr slot = new IntPtr(pPaths.ToInt64() + (long)i * pathSize);
                    PATH_INFO path = (PATH_INFO)Marshal.PtrToStructure(slot, typeof(PATH_INFO));

                    var e = new Entry();
                    e.ConnectorRawPath = path.targetInfo.outputTechnology;

                    var src = new SOURCE_DEVICE_NAME();
                    src.header.type = GET_SOURCE_NAME;
                    src.header.size = (uint)Marshal.SizeOf(typeof(SOURCE_DEVICE_NAME));
                    src.header.adapterId = path.sourceInfo.adapterId;
                    src.header.id = path.sourceInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref src) == 0)
                        e.GdiName = (src.viewGdiDeviceName ?? "").Trim();

                    var tgt = new TARGET_DEVICE_NAME();
                    tgt.header.type = GET_TARGET_NAME;
                    tgt.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
                    tgt.header.adapterId = path.targetInfo.adapterId;
                    tgt.header.id = path.targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref tgt) == 0)
                    {
                        e.TargetNameOk = true;
                        e.Friendly = (tgt.monitorFriendlyDeviceName ?? "").Trim();
                        e.DevicePath = (tgt.monitorDevicePath ?? "").Trim();
                        e.ConnectorRaw = tgt.outputTechnology;
                        e.ConnectorInstance = tgt.connectorInstance;
                        e.Connector = TechName(tgt.outputTechnology);
                    }
                    else
                    {
                        e.Connector = TechName(path.targetInfo.outputTechnology);
                        e.ConnectorRaw = path.targetInfo.outputTechnology;
                    }

                    // Prefer INFO_2: it separates HDR from wide colour gamut.
                    var adv2 = new ADVANCED_COLOR_INFO_2();
                    adv2.header.type = GET_ADVANCED_COLOR_INFO_2;
                    adv2.header.size = (uint)Marshal.SizeOf(typeof(ADVANCED_COLOR_INFO_2));
                    adv2.header.adapterId = path.targetInfo.adapterId;
                    adv2.header.id = path.targetInfo.id;
                    if (DisplayConfigGetDeviceInfo(ref adv2) == 0)
                    {
                        e.ColorInfo2Ok = true;
                        e.HdrSupported = (adv2.value & 0x10) != 0;   // highDynamicRangeSupported
                        e.HdrEnabled = (adv2.value & 0x20) != 0;     // highDynamicRangeUserEnabled
                        e.WideColorEnabled = (adv2.value & 0x80) != 0; // wideColorUserEnabled
                        e.BitsPerColor = (int)adv2.bitsPerColorChannel;
                        e.ColorMode = (int)adv2.activeColorMode;
                        // activeColorMode is authoritative if it disagrees.
                        if (e.ColorMode == MODE_HDR) e.HdrEnabled = true;
                        else if (e.ColorMode == MODE_SDR || e.ColorMode == MODE_WCG) e.HdrEnabled = false;
                    }
                    else
                    {
                        var adv = new ADVANCED_COLOR_INFO();
                        adv.header.type = GET_ADVANCED_COLOR_INFO;
                        adv.header.size = (uint)Marshal.SizeOf(typeof(ADVANCED_COLOR_INFO));
                        adv.header.adapterId = path.targetInfo.adapterId;
                        adv.header.id = path.targetInfo.id;
                        if (DisplayConfigGetDeviceInfo(ref adv) == 0)
                        {
                            bool advancedActive = (adv.value & 0x2) != 0;
                            bool wideEnforced = (adv.value & 0x4) != 0;
                            e.HdrSupported = (adv.value & 0x1) != 0;
                            // advancedColorEnabled alone is not HDR - it is also
                            // set for WCG. wideColorEnforced tells them apart.
                            e.HdrEnabled = advancedActive && !wideEnforced;
                            e.WideColorEnabled = advancedActive && wideEnforced;
                            e.BitsPerColor = (int)adv.bitsPerColorChannel;
                            e.ColorMode = e.HdrEnabled ? MODE_HDR : (e.WideColorEnabled ? MODE_WCG : MODE_SDR);
                        }
                    }

                    // Keep every path, even ones without a source name - the
                    // device-path join can still place them.
                    if (!string.IsNullOrEmpty(e.GdiName) || !string.IsNullOrEmpty(e.DevicePath))
                        result.Add(e);
                }
            }
            catch (Exception ex) { LastError = ex.Message; }
            finally
            {
                if (pPaths != IntPtr.Zero) Marshal.FreeHGlobal(pPaths);
                if (pModes != IntPtr.Zero) Marshal.FreeHGlobal(pModes);
            }
            return result;
        }
    }

    // =====================================================================
    // EDID - the only source that reliably distinguishes two identical panels
    // =====================================================================
    internal static class Edid
    {
        /// <summary>Reads the raw EDID blob for a \\?\DISPLAY#... interface path.</summary>
        public static byte[] Read(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            try
            {
                // \\?\DISPLAY#ABC1234#5&1a2b3c4&0&UID4353#{guid}
                string p = devicePath;
                if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
                string[] parts = p.Split('#');
                if (parts.Length < 3) return null;

                string key = @"SYSTEM\CurrentControlSet\Enum\" + parts[0] + @"\" + parts[1] + @"\" + parts[2] + @"\Device Parameters";
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(key))
                {
                    if (rk == null) return null;
                    return rk.GetValue("EDID") as byte[];
                }
            }
            catch { return null; }
        }

        public static string VendorCode(byte[] e)
        {
            if (e == null || e.Length < 10) return "";
            int m = (e[8] << 8) | e[9];
            char c1 = (char)('A' - 1 + ((m >> 10) & 0x1F));
            char c2 = (char)('A' - 1 + ((m >> 5) & 0x1F));
            char c3 = (char)('A' - 1 + (m & 0x1F));
            if (!char.IsLetter(c1) || !char.IsLetter(c2) || !char.IsLetter(c3)) return "";
            return new string(new[] { c1, c2, c3 });
        }

        /// <summary>Descriptor 0xFC - the model name the manufacturer burned in.</summary>
        public static string Name(byte[] e) { return Descriptor(e, 0xFC); }

        /// <summary>Descriptor 0xFF, falling back to the numeric serial in bytes 12-15.</summary>
        public static string Serial(byte[] e)
        {
            string s = Descriptor(e, 0xFF);
            if (!string.IsNullOrEmpty(s)) return s;
            if (e == null || e.Length < 16) return "";
            uint n = (uint)(e[12] | (e[13] << 8) | (e[14] << 16) | (e[15] << 24));
            return n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture);
        }

        static string Descriptor(byte[] e, byte tag)
        {
            if (e == null || e.Length < 128) return "";
            for (int off = 54; off + 18 <= 126; off += 18)
            {
                if (e[off] != 0 || e[off + 1] != 0 || e[off + 2] != 0) continue;
                if (e[off + 3] != tag) continue;
                var sb = new StringBuilder();
                for (int i = off + 5; i < off + 18; i++)
                {
                    byte b = e[i];
                    if (b == 0x0A) break;
                    if (b < 32 || b > 126) continue;
                    sb.Append((char)b);
                }
                return sb.ToString().Trim();
            }
            return "";
        }

        static readonly Dictionary<string, string> Vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "VSC", "ViewSonic" }, { "DEL", "Dell" },   { "SAM", "Samsung" }, { "GSM", "LG" },
            { "AUS", "ASUS" },      { "ACI", "ASUS" },   { "ACR", "Acer" },    { "BNQ", "BenQ" },
            { "HWP", "HP" },        { "HPN", "HP" },     { "MSI", "MSI" },     { "AOC", "AOC" },
            { "PHL", "Philips" },   { "GBT", "Gigabyte" },{ "LEN", "Lenovo" }, { "NEC", "NEC" },
            { "SNY", "Sony" },      { "APP", "Apple" },  { "EIZ", "EIZO" },    { "ENC", "EIZO" },
            { "IVM", "iiyama" },    { "CMN", "Chi Mei" },{ "AUO", "AU Optronics" }, { "BOE", "BOE" },
            { "SHP", "Sharp" },     { "LGD", "LG Display" }, { "HSD", "HannStar" }, { "CPT", "CPT" },
            { "ALC", "Alienware" }, { "PIO", "Pioneer" },{ "TCL", "TCL" },     { "HAT", "Haier" },
            { "RTK", "Realtek" },   { "VZO", "Vizio" },  { "ONN", "onn" },     { "KTC", "KTC" },
        };

        public static string VendorName(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string v;
            return Vendors.TryGetValue(code, out v) ? v : code;
        }
    }

    // =====================================================================
    // NVAPI - digital vibrance and hue
    // =====================================================================
    internal static class NvApi
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QI64(uint id);
        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QI32(uint id);
        static IntPtr Q(uint id) { return (IntPtr.Size == 8) ? QI64(id) : QI32(id); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_Init();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int D_AssocHandle([MarshalAs(UnmanagedType.LPStr)] string name, ref IntPtr h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_OutputId(IntPtr h, ref uint outputId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_GetLevel(IntPtr h, uint outputId, ref NV_LEVEL info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_SetDvc(IntPtr h, uint outputId, ref NV_LEVEL info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int D_SetHue(IntPtr h, uint outputId, int angle);

        // NV_DISPLAY_DVC_INFO_EX and NV_DISPLAY_HUE_INFO share this shape.
        [StructLayout(LayoutKind.Sequential)]
        public struct NV_LEVEL
        {
            public uint version;
            public int currentLevel, minLevel, maxLevel, defaultLevel;
        }

        const uint ID_Init = 0x0150E828;
        const uint ID_Assoc = 0x35C29134;
        const uint ID_OutputId = 0xD995937E;
        const uint ID_GetDVCEx = 0x0E45002D;
        const uint ID_SetDVCEx = 0x4A82C2B1;
        const uint ID_GetHUE = 0x95B64341;
        const uint ID_SetHUE = 0xF5A0F22C;

        static readonly uint VER_LEVEL = (uint)(Marshal.SizeOf(typeof(NV_LEVEL)) | (1 << 16));

        static bool _init, _tried;
        public static string LastError = "";

        public static bool Available { get { return Init(); } }

        public static bool Init()
        {
            if (_init) return true;
            if (_tried) return false;
            _tried = true;
            try
            {
                IntPtr p = Q(ID_Init);
                if (p == IntPtr.Zero) { LastError = "NvAPI_Initialize not exported"; return false; }
                var fn = (D_Init)Marshal.GetDelegateForFunctionPointer(p, typeof(D_Init));
                int st = fn();
                if (st != 0) { LastError = "NvAPI_Initialize status " + st; return false; }
                _init = true;
                return true;
            }
            catch (DllNotFoundException) { LastError = "nvapi64.dll not found (no NVIDIA driver?)"; return false; }
            catch (Exception ex) { LastError = ex.Message; return false; }
        }

        static bool Resolve(string gdiName, out IntPtr h, out uint outputId)
        {
            h = IntPtr.Zero; outputId = 0;
            if (!Init()) return false;
            IntPtr pa = Q(ID_Assoc);
            if (pa == IntPtr.Zero) { LastError = "GetAssociatedNvidiaDisplayHandle missing"; return false; }
            var fa = (D_AssocHandle)Marshal.GetDelegateForFunctionPointer(pa, typeof(D_AssocHandle));
            if (fa(gdiName, ref h) != 0 || h == IntPtr.Zero)
            { LastError = "no NVIDIA handle for " + gdiName; return false; }

            // outputId 0 means "every output on this GPU" - that is what made
            // vibrance change both monitors at once. Always resolve the real one.
            IntPtr po = Q(ID_OutputId);
            if (po != IntPtr.Zero)
            {
                var fo = (D_OutputId)Marshal.GetDelegateForFunctionPointer(po, typeof(D_OutputId));
                uint oid = 0;
                if (fo(h, ref oid) == 0) outputId = oid;
            }
            return true;
        }

        public static string Describe(string gdiName)
        {
            IntPtr h; uint oid;
            if (!Resolve(gdiName, out h, out oid)) return "no NVIDIA handle (" + LastError + ")";
            return "handle 0x" + h.ToString("X") + "  outputId 0x" + oid.ToString("X");
        }

        static bool GetLevel(uint fnId, string gdiName, out NV_LEVEL info)
        {
            info = new NV_LEVEL();
            IntPtr h; uint oid;
            if (!Resolve(gdiName, out h, out oid)) return false;
            IntPtr p = Q(fnId);
            if (p == IntPtr.Zero) { LastError = "function 0x" + fnId.ToString("X") + " missing"; return false; }
            var fn = (D_GetLevel)Marshal.GetDelegateForFunctionPointer(p, typeof(D_GetLevel));
            info.version = VER_LEVEL;
            int st = fn(h, oid, ref info);
            if (st != 0) { LastError = "status " + st + " from 0x" + fnId.ToString("X"); return false; }
            return true;
        }

        public static bool GetVibrance(string gdiName, out NV_LEVEL info) { return GetLevel(ID_GetDVCEx, gdiName, out info); }
        public static bool GetHue(string gdiName, out NV_LEVEL info) { return GetLevel(ID_GetHUE, gdiName, out info); }

        public static bool SetVibrance(string gdiName, int level)
        {
            IntPtr h; uint oid;
            if (!Resolve(gdiName, out h, out oid)) return false;
            IntPtr p = Q(ID_SetDVCEx);
            if (p == IntPtr.Zero) { LastError = "SetDVCLevelEx missing"; return false; }
            var fn = (D_SetDvc)Marshal.GetDelegateForFunctionPointer(p, typeof(D_SetDvc));
            var info = new NV_LEVEL();
            info.version = VER_LEVEL;
            info.currentLevel = level;
            int st = fn(h, oid, ref info);
            if (st != 0) { LastError = "SetDVCLevelEx status " + st; return false; }
            return true;
        }

        public static bool SetHue(string gdiName, int angle)
        {
            IntPtr h; uint oid;
            if (!Resolve(gdiName, out h, out oid)) return false;
            IntPtr p = Q(ID_SetHUE);
            if (p == IntPtr.Zero) { LastError = "SetHUEAngle missing"; return false; }
            var fn = (D_SetHue)Marshal.GetDelegateForFunctionPointer(p, typeof(D_SetHue));
            int st = fn(h, oid, ((angle % 360) + 360) % 360);
            if (st != 0) { LastError = "SetHUEAngle status " + st; return false; }
            return true;
        }
    }

    // =====================================================================
    // Assembling the monitor list
    // =====================================================================
    public static class Displays
    {
        public static string CcdError = "";
        public static bool CcdUsed;

        public static List<MonitorInfo> Enumerate()
        {
            List<MonitorInfo> mons = DisplayDevices.Enumerate();

            // --- CCD ---------------------------------------------------
            List<Ccd.Entry> ccd = new List<Ccd.Entry>();
            CcdUsed = false;
            try
            {
                ccd = Ccd.Query();
                CcdError = Ccd.LastError;
                CcdUsed = ccd.Count > 0;
            }
            catch (Exception ex) { CcdError = ex.Message; }

            // Join on the device interface path first. Two monitors of the same
            // model produce identical friendly names, so a name-based or
            // source-name-based join is what smeared one panel's connector
            // across both rows.
            var byPath = new Dictionary<string, Ccd.Entry>(StringComparer.OrdinalIgnoreCase);
            var byGdi = new Dictionary<string, Ccd.Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (Ccd.Entry e in ccd)
            {
                if (!string.IsNullOrEmpty(e.DevicePath) && !byPath.ContainsKey(e.DevicePath)) byPath[e.DevicePath] = e;
                if (!string.IsNullOrEmpty(e.GdiName) && !byGdi.ContainsKey(e.GdiName)) byGdi[e.GdiName] = e;
            }
            var claimed = new HashSet<Ccd.Entry>();

            foreach (MonitorInfo m in mons)
            {
                Ccd.Entry e = null;
                if (!string.IsNullOrEmpty(m.DevicePath)) byPath.TryGetValue(m.DevicePath, out e);
                if (e == null || claimed.Contains(e))
                {
                    Ccd.Entry g;
                    if (byGdi.TryGetValue(m.GdiName, out g) && !claimed.Contains(g)) e = g;
                    else if (e != null && claimed.Contains(e)) e = null;
                }
                if (e != null && claimed.Contains(e)) e = null;

                if (e != null)
                {
                    claimed.Add(e);
                    m.CcdFriendly = e.Friendly;
                    m.Connector = e.Connector;
                    m.ConnectorRaw = e.ConnectorRaw;
                    m.ConnectorRawPath = e.ConnectorRawPath;
                    m.ConnectorInstance = e.ConnectorInstance;
                    m.HdrSupported = e.HdrSupported;
                    m.HdrEnabled = e.HdrEnabled;
                    m.WideColorEnabled = e.WideColorEnabled;
                    m.ColorMode = e.ColorMode;
                    m.BitsPerColor = e.BitsPerColor;
                    m.ColorInfo2Ok = e.ColorInfo2Ok;
                    if (string.IsNullOrEmpty(m.DevicePath)) m.DevicePath = e.DevicePath;
                    if (!e.TargetNameOk) m.CcdNote = "target-name query failed; connector read from the path record";
                }
                else
                {
                    m.CcdNote = string.IsNullOrEmpty(CcdError) ? "no matching CCD path" : CcdError;
                }

                // --- EDID ---------------------------------------------
                byte[] blob = Edid.Read(m.DevicePath);
                if (blob != null)
                {
                    m.EdidName = Edid.Name(blob);
                    m.EdidSerial = Edid.Serial(blob);
                    m.VendorCode = Edid.VendorCode(blob);
                    m.Vendor = Edid.VendorName(m.VendorCode);
                }

                // --- geometry -----------------------------------------
                foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
                {
                    if (string.Equals(s.DeviceName, m.GdiName, StringComparison.OrdinalIgnoreCase))
                    { m.Bounds = s.Bounds; break; }
                }
            }

            // Order left-to-right so the list matches the physical desk layout.
            mons.Sort(delegate (MonitorInfo a, MonitorInfo b)
            {
                int c = a.Bounds.X.CompareTo(b.Bounds.X);
                if (c != 0) return c;
                return string.Compare(a.GdiName, b.GdiName, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < mons.Count; i++) mons[i].Index = i + 1;
            BuildLabels(mons);
            return mons;
        }

        /// <summary>
        /// Names every monitor so that no two rows read the same. Identical
        /// panels get separated by connector, then connector instance, then
        /// EDID serial, then the GDI device number.
        /// </summary>
        static void BuildLabels(List<MonitorInfo> mons)
        {
            var baseCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MonitorInfo m in mons)
            {
                string b = m.BaseName;
                baseCount[b] = baseCount.ContainsKey(b) ? baseCount[b] + 1 : 1;
            }

            // How many share the same name AND the same connector label?
            var withConn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (MonitorInfo m in mons)
            {
                string k = m.BaseName + "|" + m.ConnectorLabel;
                withConn[k] = withConn.ContainsKey(k) ? withConn[k] + 1 : 1;
            }

            foreach (MonitorInfo m in mons)
            {
                var sb = new StringBuilder();
                sb.Append(m.Index.ToString(CultureInfo.InvariantCulture)).Append(".  ").Append(m.BaseName);

                bool nameCollides = baseCount[m.BaseName] > 1;
                string conn = m.ConnectorLabel;

                if (!string.IsNullOrEmpty(conn)) sb.Append("  -  ").Append(conn);

                // Still ambiguous: two identical panels on the same connector type.
                if (nameCollides && withConn[m.BaseName + "|" + conn] > 1)
                {
                    if (!string.IsNullOrEmpty(m.EdidSerial)) sb.Append("  (s/n ").Append(Tail(m.EdidSerial, 8)).Append(')');
                    else sb.Append("  (").Append(m.GdiName.Replace(@"\\.\", "")).Append(')');
                }

                if (!string.IsNullOrEmpty(m.Geometry)) sb.Append("  -  ").Append(m.Geometry);
                if (m.HdrEnabled) sb.Append("  [HDR]");
                if (m.Primary) sb.Append("  *");

                m.Label = sb.ToString();

                var s2 = new StringBuilder();
                s2.Append(m.Index.ToString(CultureInfo.InvariantCulture)).Append(".  ").Append(m.BaseName);
                if (!string.IsNullOrEmpty(conn)) s2.Append(" (").Append(conn).Append(')');
                m.ShortLabel = s2.ToString();
            }
        }

        static string Tail(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(s.Length - n);
        }
    }
}
