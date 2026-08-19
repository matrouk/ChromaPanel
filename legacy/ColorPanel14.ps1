<#
    Colour Panel - build 11

    * Per-channel Brightness / Contrast / Gamma (All / Red / Green / Blue)
    * Digital Vibrance via NVAPI, per physical output
    * Real per-monitor identification
    * Named profiles, with a Default profile created automatically
    * Nothing is applied until you actually change something

    Run:  powershell -ExecutionPolicy Bypass -File .\ColorPanel11.ps1
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class CP11
{
    // ================= GDI =================
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDC(string driver, string device, string port, IntPtr pd);
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    public static bool ApplyRamp(string deviceName, ushort[] ramp)
    {
        IntPtr hdc = CreateDC(deviceName, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(hdc, ramp); }
        finally { DeleteDC(hdc); }
    }

    // Write, read back, and report whether Windows clamped us.
    public static bool ApplyRampChecked(string deviceName, ushort[] ramp, out bool clamped)
    {
        clamped = false;
        IntPtr hdc = CreateDC(deviceName, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try {
            if (!SetDeviceGammaRamp(hdc, ramp)) return false;
            var back = new ushort[768];
            if (GetDeviceGammaRamp(hdc, back)) {
                for (int i = 0; i < 768; i += 37) {
                    int diff = back[i] - ramp[i];
                    if (diff < 0) diff = -diff;
                    if (diff > 600) { clamped = true; break; }
                }
            }
            return true;
        }
        finally { DeleteDC(hdc); }
    }

    // ================= Displays =================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    public class DisplayInfo {
        public string DeviceName;
        public string Model;
        public string MonitorId;   // EDID-ish instance path, unique per panel
        public bool   Primary;
    }

    public static List<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 0x1) == 0) continue;   // not attached

            string model = null, monId = null;
            var mon = new DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0)) {
                model = mon.DeviceString;
                monId = mon.DeviceID;
            }
            if (string.IsNullOrWhiteSpace(model)) model = dd.DeviceString;
            if (string.IsNullOrWhiteSpace(model)) model = dd.DeviceName;

            list.Add(new DisplayInfo {
                DeviceName = dd.DeviceName,
                Model      = model,
                MonitorId  = monId ?? "",
                Primary    = (dd.StateFlags & 0x4) != 0
            });
        }
        return list;
    }

    // ================= NVAPI =================
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
    delegate int D_GetDVC(IntPtr h, uint outputId, ref NV_DVC info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int D_SetDVC(IntPtr h, uint outputId, ref NV_DVC info);

    [StructLayout(LayoutKind.Sequential)]
    public struct NV_DVC {
        public uint version;
        public int currentLevel, minLevel, maxLevel, defaultLevel;
    }

    const uint ID_Init      = 0x0150E828;
    const uint ID_Assoc     = 0x35C29134;
    const uint ID_OutputId  = 0xD995937E;
    const uint ID_GetDVCEx  = 0x0E45002D;
    const uint ID_SetDVCEx  = 0x4A82C2B1;
    static uint VER_DVC = (uint)(Marshal.SizeOf(typeof(NV_DVC)) | (1 << 16));

    static bool _init;
    public static string LastError = "";

    public static bool Init() {
        if (_init) return true;
        try {
            IntPtr p = Q(ID_Init);
            if (p == IntPtr.Zero) { LastError = "NvAPI_Initialize missing"; return false; }
            var fn = (D_Init)Marshal.GetDelegateForFunctionPointer(p, typeof(D_Init));
            int st = fn();
            if (st != 0) { LastError = "NvAPI_Initialize status " + st; return false; }
            _init = true; return true;
        } catch (Exception ex) { LastError = ex.Message; return false; }
    }

    static bool Resolve(string deviceName, out IntPtr h, out uint outputId) {
        h = IntPtr.Zero; outputId = 0;
        if (!Init()) return false;
        IntPtr pa = Q(ID_Assoc);
        if (pa == IntPtr.Zero) { LastError = "assoc handle missing"; return false; }
        var fa = (D_AssocHandle)Marshal.GetDelegateForFunctionPointer(pa, typeof(D_AssocHandle));
        if (fa(deviceName, ref h) != 0 || h == IntPtr.Zero) {
            LastError = "no NVIDIA handle for " + deviceName; return false;
        }
        IntPtr po = Q(ID_OutputId);
        if (po != IntPtr.Zero) {
            var fo = (D_OutputId)Marshal.GetDelegateForFunctionPointer(po, typeof(D_OutputId));
            uint oid = 0;
            if (fo(h, ref oid) == 0) outputId = oid;
        }
        return true;
    }

    public static string Describe(string deviceName) {
        IntPtr h; uint oid;
        if (!Resolve(deviceName, out h, out oid)) return "no NVIDIA handle";
        return "handle 0x" + h.ToString("X") + "  outputId 0x" + oid.ToString("X");
    }

    public static bool GetVibrance(string deviceName, ref NV_DVC info) {
        IntPtr h; uint oid;
        if (!Resolve(deviceName, out h, out oid)) return false;
        IntPtr p = Q(ID_GetDVCEx);
        if (p == IntPtr.Zero) { LastError = "GetDVCInfoEx missing"; return false; }
        var fn = (D_GetDVC)Marshal.GetDelegateForFunctionPointer(p, typeof(D_GetDVC));
        info.version = VER_DVC;
        int st = fn(h, oid, ref info);
        if (st != 0) { LastError = "GetDVCInfoEx status " + st; return false; }
        return true;
    }

    public static bool SetVibrance(string deviceName, int level) {
        IntPtr h; uint oid;
        if (!Resolve(deviceName, out h, out oid)) return false;
        IntPtr p = Q(ID_SetDVCEx);
        if (p == IntPtr.Zero) { LastError = "SetDVCLevelEx missing"; return false; }
        var fn = (D_SetDVC)Marshal.GetDelegateForFunctionPointer(p, typeof(D_SetDVC));
        var info = new NV_DVC();
        info.version = VER_DVC;
        info.currentLevel = level;
        int st = fn(h, oid, ref info);
        if (st != 0) { LastError = "SetDVCLevelEx status " + st; return false; }
        return true;
    }
}
'@

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Windows CCD (Connecting and Configuring Displays) API.
// Gives the real monitor friendly name, the physical connector type
// (HDMI / DisplayPort / DVI) and the HDR state - none of which
// EnumDisplayDevices can tell us.
public class CCD
{
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; }

    [StructLayout(LayoutKind.Sequential)]
    struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_TARGET_INFO {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int  targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    // No ByValArray field here: a null byte[] in an array element makes the
    // marshaler and Windows disagree about the buffer and corrupts the heap.
    [StructLayout(LayoutKind.Sequential)]
    struct MODE_INFO {
        public uint infoType; public uint id; public LUID adapterId;
        public uint b0,b1,b2,b3,b4,b5,b6,b7,b8,b9,b10,b11;   // 48 bytes of union
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SOURCE_DEVICE_NAME {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_DEVICE_NAME {
        public DEVICE_INFO_HEADER header;
        public uint   flags;
        public uint   outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint   connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ADVANCED_COLOR_INFO {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);

    // IntPtr buffers, not struct arrays - we control the exact byte count.
    [DllImport("user32.dll")]
    static extern int QueryDisplayConfig(uint flags, ref uint numPath, IntPtr paths,
        ref uint numMode, IntPtr modes, IntPtr current);

    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);
    [DllImport("user32.dll")]
    static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO r);

    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const uint GET_SOURCE_NAME = 1;
    const uint GET_TARGET_NAME = 2;
    const uint GET_ADVANCED_COLOR_INFO = 9;

    public class Mon {
        public string GdiName;
        public string Friendly;
        public string Connector;
        public uint   ConnectorIndex;
        public bool   HdrSupported;
        public bool   HdrEnabled;
        public string DevicePath;
    }

    static string TechName(uint t) {
        switch (t) {
            case 0:  return "VGA";
            case 1:  return "S-Video";
            case 2:  return "Composite";
            case 3:  return "Component";
            case 4:  return "DVI";
            case 5:  return "HDMI";
            case 6:  return "LVDS";
            case 10: return "DisplayPort";
            case 11: return "DisplayPort (embedded)";
            case 12: return "UDI";
            case 13: return "UDI (embedded)";
            case 15: return "Miracast";
            case 0x80000000: return "Internal";
            case 0xFFFFFFFF: return "Other";
            default: return "Type " + t;
        }
    }

    public static string LastError = "";

    public static List<Mon> Query()
    {
        var result = new List<Mon>();
        IntPtr pPaths = IntPtr.Zero, pModes = IntPtr.Zero;
        try {
            int pathSize = Marshal.SizeOf(typeof(PATH_INFO));
            int modeSize = Marshal.SizeOf(typeof(MODE_INFO));

            // Guard: if our layout does not match what Windows expects, do not
            // call at all. A wrong size here corrupts the process heap.
            if (pathSize != 72 && pathSize != 68) {
                LastError = "unexpected PATH_INFO size " + pathSize;
                return result;
            }
            if (modeSize != 64) {
                LastError = "unexpected MODE_INFO size " + modeSize;
                return result;
            }

            uint nPath, nMode;
            int rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out nPath, out nMode);
            if (rc != 0) { LastError = "GetDisplayConfigBufferSizes rc=" + rc; return result; }
            if (nPath == 0) { LastError = "no active paths"; return result; }

            pPaths = Marshal.AllocHGlobal((int)nPath * pathSize);
            pModes = Marshal.AllocHGlobal((int)nMode * modeSize);

            rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref nPath, pPaths, ref nMode, pModes, IntPtr.Zero);
            if (rc != 0) { LastError = "QueryDisplayConfig rc=" + rc; return result; }

            for (int i = 0; i < nPath; i++)
            {
                IntPtr slot = new IntPtr(pPaths.ToInt64() + (long)i * pathSize);
                PATH_INFO path = (PATH_INFO)Marshal.PtrToStructure(slot, typeof(PATH_INFO));

                var m = new Mon();

                var src = new SOURCE_DEVICE_NAME();
                src.header.type = GET_SOURCE_NAME;
                src.header.size = (uint)Marshal.SizeOf(typeof(SOURCE_DEVICE_NAME));
                src.header.adapterId = path.sourceInfo.adapterId;
                src.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref src) == 0)
                    m.GdiName = src.viewGdiDeviceName;

                var tgt = new TARGET_DEVICE_NAME();
                tgt.header.type = GET_TARGET_NAME;
                tgt.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
                tgt.header.adapterId = path.targetInfo.adapterId;
                tgt.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0) {
                    m.Friendly       = tgt.monitorFriendlyDeviceName;
                    m.Connector      = TechName(tgt.outputTechnology);
                    m.ConnectorIndex = tgt.connectorInstance;
                    m.DevicePath     = tgt.monitorDevicePath;
                } else {
                    m.Connector = TechName(path.targetInfo.outputTechnology);
                }

                var adv = new ADVANCED_COLOR_INFO();
                adv.header.type = GET_ADVANCED_COLOR_INFO;
                adv.header.size = (uint)Marshal.SizeOf(typeof(ADVANCED_COLOR_INFO));
                adv.header.adapterId = path.targetInfo.adapterId;
                adv.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref adv) == 0) {
                    m.HdrSupported = (adv.value & 0x1) != 0;
                    m.HdrEnabled   = (adv.value & 0x2) != 0;
                }

                if (!string.IsNullOrEmpty(m.GdiName)) result.Add(m);
            }
        } catch (Exception ex) {
            LastError = ex.Message;
        } finally {
            if (pPaths != IntPtr.Zero) Marshal.FreeHGlobal(pPaths);
            if (pModes != IntPtr.Zero) Marshal.FreeHGlobal(pModes);
        }
        return result;
    }
}
'@

# ===========================================================================
# State
# ===========================================================================
$script:Displays = [CP11]::GetDisplays()
if (-not $script:Displays -or $script:Displays.Count -eq 0) {
    [void][System.Windows.Forms.MessageBox]::Show('No attached displays found.')
    return
}
# CCD gives real monitor names + connector type, but it is native interop.
# Drop a file called  noccd.txt  next to this script to skip it entirely.
$script:Ccd = @{}
$script:CcdSkipped = $false
if (Test-Path (Join-Path $PSScriptRoot 'noccd.txt')) {
    $script:CcdSkipped = $true
} else {
    try {
        foreach ($m in [CCD]::Query()) { if ($m.GdiName) { $script:Ccd[$m.GdiName] = $m } }
    } catch {
        $script:Ccd = @{}
    }
}

$script:NvOk     = [CP11]::Init()
$script:VibRange = @{}
$script:suspend  = $false
$script:LastKey  = $null

# Per-monitor, per-channel values.  key -> @{ R=@{B;C;G}; G=...; B=...; Vib=n }
$script:State = @{}

function New-ChannelSet {
    return @{
        R   = @{ B = 50.0; C = 50.0; G = 1.0 }
        G   = @{ B = 50.0; C = 50.0; G = 1.0 }
        B   = @{ B = 50.0; C = 50.0; G = 1.0 }
        Vib = 50
    }
}

# ---- profiles -------------------------------------------------------------
$script:ProfilePath = Join-Path $PSScriptRoot 'profiles.json'
$script:Profiles = [ordered]@{}

function Convert-ToChannelSet {
    param($Obj)
    $cs = New-ChannelSet
    foreach ($ch in 'R','G','B') {
        if ($Obj.$ch) {
            $cs[$ch] = @{
                B = [double]$Obj.$ch.B
                C = [double]$Obj.$ch.C
                G = [double]$Obj.$ch.G
            }
        }
    }
    if ($null -ne $Obj.Vib) { $cs.Vib = [int]$Obj.Vib }
    return $cs
}

if (Test-Path $script:ProfilePath) {
    try {
        $raw = Get-Content $script:ProfilePath -Raw | ConvertFrom-Json
        foreach ($p in $raw.PSObject.Properties) {
            $script:Profiles[$p.Name] = Convert-ToChannelSet $p.Value
        }
    } catch { }
}
if ($script:Profiles.Count -eq 0) {
    $script:Profiles['Default'] = New-ChannelSet
}
function Save-Profiles {
    try { $script:Profiles | ConvertTo-Json -Depth 6 | Set-Content $script:ProfilePath -Encoding UTF8 } catch { }
}

# ===========================================================================
# Colour maths
# ===========================================================================
function Build-Ramp {
    param($Set)
    $ramp = New-Object 'System.UInt16[]' 768
    $order = @('R','G','B')
    for ($c = 0; $c -lt 3; $c++) {
        $s = $Set[$order[$c]]
        $bOff = ($s.B - 50.0) / 100.0
        $cMul = $s.C / 50.0
        $invG = 1.0 / $s.G
        $base = $c * 256
        for ($i = 0; $i -lt 256; $i++) {
            $v = [Math]::Pow($i / 255.0, $invG)
            $v = ($v - 0.5) * $cMul + 0.5 + $bOff
            if ($v -lt 0.0) { $v = 0.0 }
            if ($v -gt 1.0) { $v = 1.0 }
            $ramp[$base + $i] = [System.UInt16][Math]::Round($v * 65535.0)
        }
    }
    return $ramp
}

# ===========================================================================
# UI
# ===========================================================================
$accent = [System.Drawing.Color]::FromArgb(0, 120, 212)
$bgCol  = [System.Drawing.Color]::FromArgb(243, 243, 243)
$cardCol= [System.Drawing.Color]::White

$form = New-Object System.Windows.Forms.Form
$form.Text            = 'Colour Panel'
$form.ClientSize      = New-Object System.Drawing.Size(560, 660)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox     = $false
$form.StartPosition   = 'CenterScreen'
$form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)
$form.BackColor       = $bgCol

function New-Card {
    param([int]$Y, [int]$H, [string]$Title)
    $p = New-Object System.Windows.Forms.Panel
    $p.Location  = New-Object System.Drawing.Point(16, $Y)
    $p.Size      = New-Object System.Drawing.Size(528, $H)
    $p.BackColor = $cardCol
    $form.Controls.Add($p)

    if ($Title) {
        $t = New-Object System.Windows.Forms.Label
        $t.Text      = $Title
        $t.Location  = New-Object System.Drawing.Point(16, 12)
        $t.Size      = New-Object System.Drawing.Size(300, 20)
        $t.Font      = New-Object System.Drawing.Font('Segoe UI Semibold', 9.5)
        $t.ForeColor = [System.Drawing.Color]::FromArgb(40,40,40)
        $p.Controls.Add($t)
    }
    return $p
}

# ---------------- card 1: monitor + profile ----------------
$card1 = New-Card -Y 16 -H 132 -Title 'Target'

$lblMon = New-Object System.Windows.Forms.Label
$lblMon.Text = 'Monitor'
$lblMon.Location = New-Object System.Drawing.Point(16, 44)
$lblMon.Size = New-Object System.Drawing.Size(58, 22)
$card1.Controls.Add($lblMon)

$cboMon = New-Object System.Windows.Forms.ComboBox
$cboMon.Location = New-Object System.Drawing.Point(80, 41)
$cboMon.Size = New-Object System.Drawing.Size(432, 24)
$cboMon.DropDownStyle = 'DropDownList'
$card1.Controls.Add($cboMon)

$lblProf = New-Object System.Windows.Forms.Label
$lblProf.Text = 'Profile'
$lblProf.Location = New-Object System.Drawing.Point(16, 82)
$lblProf.Size = New-Object System.Drawing.Size(58, 22)
$card1.Controls.Add($lblProf)

$cboProf = New-Object System.Windows.Forms.ComboBox
$cboProf.Location = New-Object System.Drawing.Point(80, 79)
$cboProf.Size = New-Object System.Drawing.Size(196, 24)
$cboProf.DropDownStyle = 'DropDownList'
$card1.Controls.Add($cboProf)

function New-SmallButton {
    param($Parent, [string]$Text, [int]$X, [int]$Y, [int]$W = 56)
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text
    $b.Location = New-Object System.Drawing.Point($X, $Y)
    $b.Size = New-Object System.Drawing.Size($W, 26)
    $b.FlatStyle = 'System'
    $Parent.Controls.Add($b)
    return $b
}

$btnNew    = New-SmallButton -Parent $card1 -Text 'New'    -X 284 -Y 79
$btnSave   = New-SmallButton -Parent $card1 -Text 'Save'   -X 344 -Y 79
$btnRename = New-SmallButton -Parent $card1 -Text 'Rename' -X 404 -Y 79 -W 60
$btnDelete = New-SmallButton -Parent $card1 -Text 'Delete' -X 468 -Y 79 -W 44

# ---------------- card 2: channel + sliders ----------------
$card2 = New-Card -Y 160 -H 296 -Title 'Colour adjustment'

$lblCh = New-Object System.Windows.Forms.Label
$lblCh.Text = 'Channel'
$lblCh.Location = New-Object System.Drawing.Point(16, 44)
$lblCh.Size = New-Object System.Drawing.Size(58, 22)
$card2.Controls.Add($lblCh)

$cboCh = New-Object System.Windows.Forms.ComboBox
$cboCh.Location = New-Object System.Drawing.Point(80, 41)
$cboCh.Size = New-Object System.Drawing.Size(180, 24)
$cboCh.DropDownStyle = 'DropDownList'
foreach ($n in 'All channels','Red','Green','Blue') { [void]$cboCh.Items.Add($n) }
$cboCh.SelectedIndex = 0
$card2.Controls.Add($cboCh)

$swatch = New-Object System.Windows.Forms.Panel
$swatch.Location = New-Object System.Drawing.Point(270, 41)
$swatch.Size = New-Object System.Drawing.Size(24, 24)
$swatch.BackColor = [System.Drawing.Color]::Gainsboro
$card2.Controls.Add($swatch)

function New-Row {
    param($Parent, [string]$Text, [int]$Y, [int]$Min, [int]$Max, [int]$Value, [double]$Div = 1.0, [string]$Suffix = '')
    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Text = $Text
    $lbl.Location = New-Object System.Drawing.Point(16, ($Y + 6))
    $lbl.Size = New-Object System.Drawing.Size(110, 20)
    $Parent.Controls.Add($lbl)

    $bar = New-Object System.Windows.Forms.TrackBar
    $bar.Location = New-Object System.Drawing.Point(126, $Y)
    $bar.Size = New-Object System.Drawing.Size(320, 45)
    $bar.Minimum = $Min; $bar.Maximum = $Max; $bar.Value = $Value
    $bar.TickStyle = 'None'
    $Parent.Controls.Add($bar)

    $val = New-Object System.Windows.Forms.Label
    $val.Location = New-Object System.Drawing.Point(452, ($Y + 6))
    $val.Size = New-Object System.Drawing.Size(66, 20)
    $val.TextAlign = 'MiddleRight'
    $val.Font = New-Object System.Drawing.Font('Consolas', 9)
    $Parent.Controls.Add($val)

    $bar | Add-Member -NotePropertyName Readout -NotePropertyValue $val
    $bar | Add-Member -NotePropertyName Div     -NotePropertyValue $Div
    $bar | Add-Member -NotePropertyName Suffix  -NotePropertyValue $Suffix
    return $bar
}

$barB = New-Row -Parent $card2 -Text 'Brightness'       -Y 80  -Min 0  -Max 100 -Value 50 -Suffix '%'
$barC = New-Row -Parent $card2 -Text 'Contrast'         -Y 130 -Min 0  -Max 100 -Value 50 -Suffix '%'
$barG = New-Row -Parent $card2 -Text 'Gamma'            -Y 180 -Min 30 -Max 280 -Value 100 -Div 100.0
$barV = New-Row -Parent $card2 -Text 'Digital vibrance' -Y 236 -Min 0  -Max 100 -Value 50 -Suffix '%'

# ---------------- card 3: status ----------------
$card3 = New-Card -Y 468 -H 96 -Title $null

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Location = New-Object System.Drawing.Point(16, 14)
$lblStatus.Size = New-Object System.Drawing.Size(496, 36)
$lblStatus.ForeColor = [System.Drawing.Color]::DimGray
$card3.Controls.Add($lblStatus)

$lblHint = New-Object System.Windows.Forms.Label
$lblHint.Location = New-Object System.Drawing.Point(16, 56)
$lblHint.Size = New-Object System.Drawing.Size(496, 32)
$lblHint.ForeColor = [System.Drawing.Color]::Gray
$lblHint.Font = New-Object System.Drawing.Font('Segoe UI', 8)
$card3.Controls.Add($lblHint)

# ---------------- bottom buttons ----------------
$btnReset = New-Object System.Windows.Forms.Button
$btnReset.Text = 'Reset to neutral'
$btnReset.Location = New-Object System.Drawing.Point(16, 584)
$btnReset.Size = New-Object System.Drawing.Size(130, 32)
$btnReset.FlatStyle = 'System'
$form.Controls.Add($btnReset)

$btnInfo = New-Object System.Windows.Forms.Button
$btnInfo.Text = 'Info'
$btnInfo.Location = New-Object System.Drawing.Point(154, 584)
$btnInfo.Size = New-Object System.Drawing.Size(80, 32)
$btnInfo.FlatStyle = 'System'
$form.Controls.Add($btnInfo)

$btnApply = New-Object System.Windows.Forms.Button
$btnApply.Text = 'Apply'
$btnApply.Location = New-Object System.Drawing.Point(444, 584)
$btnApply.Size = New-Object System.Drawing.Size(100, 32)
$btnApply.FlatStyle = 'System'
$form.Controls.Add($btnApply)

# ===========================================================================
# Monitor list
# ===========================================================================
$screens = [System.Windows.Forms.Screen]::AllScreens
foreach ($d in $script:Displays) {
    $short = ($d.DeviceName -replace '^\\\\\.\\', '')
    $scr = $screens | Where-Object { $_.DeviceName -eq $d.DeviceName } | Select-Object -First 1
    $geom = if ($scr) { '{0}x{1} at {2},{3}' -f $scr.Bounds.Width, $scr.Bounds.Height, $scr.Bounds.X, $scr.Bounds.Y } else { '' }
    $c = $script:Ccd[$d.DeviceName]
    $name = if ($c -and $c.Friendly) { $c.Friendly } else { $d.Model }
    $conn = if ($c -and $c.Connector) {
        if ($c.ConnectorIndex -gt 0) { '{0} #{1}' -f $c.Connector, ($c.ConnectorIndex + 1) }
        else { $c.Connector }
    } else { '' }

    $label = '{0} - {1}' -f $short, $name
    if ($conn) { $label += "  via $conn" }
    if ($geom) { $label += "  ($geom)" }
    if ($c -and $c.HdrEnabled) { $label += '  [HDR ON]' }
    if ($d.Primary) { $label += '  * primary' }
    [void]$cboMon.Items.Add($label)
}
[void]$cboMon.Items.Add('All monitors')
$cboMon.SelectedIndex = 0

function Test-AllSelected { return ($cboMon.SelectedIndex -ge $script:Displays.Count) }

function Get-TargetDevices {
    # leading comma stops PowerShell unwrapping a 1-element array into a string
    if (Test-AllSelected) {
        return @($script:Displays | ForEach-Object { $_.DeviceName })
    }
    return @($script:Displays[$cboMon.SelectedIndex].DeviceName)
}

function Get-Key {
    if (Test-AllSelected) { return '*ALL*' }
    return $script:Displays[$cboMon.SelectedIndex].DeviceName
}

function Get-CurrentSet {
    $k = Get-Key
    if (-not $script:State.ContainsKey($k)) { $script:State[$k] = New-ChannelSet }
    return $script:State[$k]
}

function Get-VibRange {
    param([string]$Device)
    if ($script:VibRange.ContainsKey($Device)) { return $script:VibRange[$Device] }
    $info = New-Object CP11+NV_DVC
    if ([CP11]::GetVibrance($Device, [ref]$info)) {
        $r = @{ Min=$info.minLevel; Max=$info.maxLevel; Default=$info.defaultLevel; Current=$info.currentLevel; Ok=$true }
    } else {
        $r = @{ Min=0; Max=100; Default=50; Current=50; Ok=$false }
    }
    $script:VibRange[$Device] = $r
    return $r
}

# ===========================================================================
# Slider <-> state plumbing
# ===========================================================================
function Get-ActiveChannels {
    # Leading commas stop PowerShell unwrapping a 1-element array into a bare
    # string (indexing which would return a char and break hashtable lookups).
    switch ($cboCh.SelectedIndex) {
        1 { return @('R') }
        2 { return @('G') }
        3 { return @('B') }
        default { return @('R','G','B') }
    }
}

function Update-Swatch {
    $swatch.BackColor = switch ($cboCh.SelectedIndex) {
        1 { [System.Drawing.Color]::FromArgb(220,60,60) }
        2 { [System.Drawing.Color]::FromArgb(60,175,75) }
        3 { [System.Drawing.Color]::FromArgb(60,110,220) }
        default { [System.Drawing.Color]::Gainsboro }
    }
}

function Update-Readouts {
    $barB.Readout.Text = '{0}{1}' -f [int]$barB.Value, $barB.Suffix
    $barC.Readout.Text = '{0}{1}' -f [int]$barC.Value, $barC.Suffix
    $barG.Readout.Text = '{0:0.00}' -f ($barG.Value / $barG.Div)
    if ($barV.Enabled) { $barV.Readout.Text = '{0}%' -f [int]$barV.Value }
    else { $barV.Readout.Text = 'n/a' }
}

# Push the state for the selected monitor/channel into the sliders
function Load-SlidersFromState {
    $set = Get-CurrentSet
    $chs = @(Get-ActiveChannels)
    $script:suspend = $true

    $first = $set[$chs[0]]
    $mixed = $false
    foreach ($c in $chs) {
        if ($set[$c].B -ne $first.B -or $set[$c].C -ne $first.C -or $set[$c].G -ne $first.G) { $mixed = $true }
    }

    $barB.Value = [int][Math]::Round($first.B)
    $barC.Value = [int][Math]::Round($first.C)
    $barG.Value = [int][Math]::Round($first.G * 100)

    $devs = @(Get-TargetDevices)
    $r = Get-VibRange -Device $devs[0]
    $barV.Minimum = $r.Min
    $barV.Maximum = $r.Max
    $barV.Enabled = $r.Ok
    $v = [int]$set.Vib
    if ($v -lt $r.Min) { $v = $r.Min }
    if ($v -gt $r.Max) { $v = $r.Max }
    $barV.Value = $v

    $script:suspend = $false
    Update-Readouts
    Update-Swatch

    if ($mixed) {
        $lblHint.Text = 'Channels currently differ - moving a slider here will set all three to the same value.'
    } else {
        $lblHint.Text = ''
    }
}

# Pull slider values back into state
function Save-SlidersToState {
    $set = Get-CurrentSet
    foreach ($c in @(Get-ActiveChannels)) {
        $set[$c].B = [double]$barB.Value
        $set[$c].C = [double]$barC.Value
        $set[$c].G = [double]$barG.Value / 100.0
    }
    $set.Vib = [int]$barV.Value
}

function Apply-Now {
    Save-SlidersToState
    Update-Readouts

    $set  = Get-CurrentSet
    $ramp = Build-Ramp -Set $set
    $problems = @()
    $clampedAny = $false

    foreach ($dev in @(Get-TargetDevices)) {
        $clamped = $false
        $ok = [CP11]::ApplyRampChecked($dev, $ramp, [ref]$clamped)
        if (-not $ok) { $problems += "gamma failed on $dev" }
        elseif ($clamped) { $clampedAny = $true }

        if ($barV.Enabled) {
            if (-not [CP11]::SetVibrance($dev, [int]$barV.Value)) {
                $problems += "vibrance: $([CP11]::LastError)"
            }
        }
    }

    if ($problems.Count -gt 0) {
        $lblStatus.Text = ($problems -join '   ')
        $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick
    } elseif ($clampedAny) {
        $lblStatus.Text = 'Applied, but Windows clamped part of the curve.'
        $lblStatus.ForeColor = [System.Drawing.Color]::DarkOrange
        $lblHint.Text = 'To unlock the full range, run as admin then reboot:  reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f'
    } else {
        $lblStatus.Text = 'Applied.'
        $lblStatus.ForeColor = [System.Drawing.Color]::SeaGreen
    }
}

$onSlide = {
    if ($script:suspend) { return }
    Apply-Now
}
foreach ($b in @($barB, $barC, $barG, $barV)) {
    $b.Add_Scroll($onSlide)
    $b.Add_ValueChanged($onSlide)
}

$cboCh.Add_SelectedIndexChanged({
    if ($script:suspend) { return }
    Load-SlidersFromState
})

$cboMon.Add_SelectedIndexChanged({
    if ($script:suspend) { return }
    # Just switch which monitor we are editing. Do NOT push the previous
    # monitor's values onto it - load its own instead.
    Load-SlidersFromState
    $script:LastKey = Get-Key
    $lblStatus.Text = 'Now editing: ' + (Get-Key)
    $lblStatus.ForeColor = [System.Drawing.Color]::DimGray
})

# ===========================================================================
# Profiles
# ===========================================================================
function Refresh-ProfileList {
    param([string]$Select)
    $script:suspend = $true
    $cboProf.Items.Clear()
    foreach ($n in $script:Profiles.Keys) { [void]$cboProf.Items.Add($n) }
    if ($Select -and $cboProf.Items.Contains($Select)) { $cboProf.SelectedItem = $Select }
    $script:suspend = $false
}

function Copy-Set {
    param($S)
    return @{
        R = @{ B = $S.R.B; C = $S.R.C; G = $S.R.G }
        G = @{ B = $S.G.B; C = $S.G.C; G = $S.G.G }
        B = @{ B = $S.B.B; C = $S.B.C; G = $S.B.G }
        Vib = $S.Vib
    }
}

$cboProf.Add_SelectedIndexChanged({
    if ($script:suspend) { return }
    $n = $cboProf.SelectedItem
    if (-not $n -or -not $script:Profiles.Contains($n)) { return }
    $script:State[(Get-Key)] = Copy-Set $script:Profiles[$n]
    Load-SlidersFromState
    Apply-Now
    $lblStatus.Text = "Applied profile '$n'."
    $lblStatus.ForeColor = [System.Drawing.Color]::SeaGreen
})

function Read-Name {
    param([string]$Title, [string]$Default = '')
    $dlg = New-Object System.Windows.Forms.Form
    $dlg.Text = $Title
    $dlg.ClientSize = New-Object System.Drawing.Size(320, 110)
    $dlg.FormBorderStyle = 'FixedDialog'
    $dlg.StartPosition = 'CenterParent'
    $dlg.MaximizeBox = $false; $dlg.MinimizeBox = $false
    $dlg.Font = New-Object System.Drawing.Font('Segoe UI', 9)

    $tb = New-Object System.Windows.Forms.TextBox
    $tb.Location = New-Object System.Drawing.Point(16, 20)
    $tb.Size = New-Object System.Drawing.Size(288, 24)
    $tb.Text = $Default
    $dlg.Controls.Add($tb)

    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'OK'; $ok.DialogResult = 'OK'
    $ok.Location = New-Object System.Drawing.Point(140, 62)
    $ok.Size = New-Object System.Drawing.Size(78, 28)
    $ok.FlatStyle = 'System'
    $dlg.Controls.Add($ok)

    $ca = New-Object System.Windows.Forms.Button
    $ca.Text = 'Cancel'; $ca.DialogResult = 'Cancel'
    $ca.Location = New-Object System.Drawing.Point(226, 62)
    $ca.Size = New-Object System.Drawing.Size(78, 28)
    $ca.FlatStyle = 'System'
    $dlg.Controls.Add($ca)

    $dlg.AcceptButton = $ok; $dlg.CancelButton = $ca
    if ($dlg.ShowDialog($form) -eq 'OK') { return $tb.Text.Trim() }
    return $null
}

$btnNew.Add_Click({
    $n = Read-Name -Title 'New profile name'
    if (-not $n) { return }
    if ($script:Profiles.Contains($n)) {
        [void][System.Windows.Forms.MessageBox]::Show("A profile named '$n' already exists.")
        return
    }
    Save-SlidersToState
    $script:Profiles[$n] = Copy-Set (Get-CurrentSet)
    Save-Profiles
    Refresh-ProfileList -Select $n
    $lblStatus.Text = "Created '$n'."
    $lblStatus.ForeColor = [System.Drawing.Color]::SeaGreen
})

$btnSave.Add_Click({
    $n = $cboProf.SelectedItem
    if (-not $n) {
        $lblStatus.Text = 'No profile selected - use New.'
        $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick
        return
    }
    Save-SlidersToState
    $script:Profiles[$n] = Copy-Set (Get-CurrentSet)
    Save-Profiles
    $lblStatus.Text = "Saved into '$n'."
    $lblStatus.ForeColor = [System.Drawing.Color]::SeaGreen
})

$btnRename.Add_Click({
    $n = $cboProf.SelectedItem
    if (-not $n) { return }
    $new = Read-Name -Title 'Rename profile' -Default $n
    if (-not $new -or $new -eq $n) { return }
    if ($script:Profiles.Contains($new)) {
        [void][System.Windows.Forms.MessageBox]::Show("A profile named '$new' already exists.")
        return
    }
    $re = [ordered]@{}
    foreach ($k in $script:Profiles.Keys) {
        if ($k -eq $n) { $re[$new] = $script:Profiles[$k] } else { $re[$k] = $script:Profiles[$k] }
    }
    $script:Profiles = $re
    Save-Profiles
    Refresh-ProfileList -Select $new
})

$btnDelete.Add_Click({
    $n = $cboProf.SelectedItem
    if (-not $n) { return }
    if ($script:Profiles.Count -le 1) {
        [void][System.Windows.Forms.MessageBox]::Show('Keep at least one profile.')
        return
    }
    if ([System.Windows.Forms.MessageBox]::Show("Delete profile '$n'?", 'Confirm', 'YesNo') -ne 'Yes') { return }
    $script:Profiles.Remove($n)
    Save-Profiles
    Refresh-ProfileList
    $lblStatus.Text = "Deleted '$n'."
})

$btnReset.Add_Click({
    $devs = @(Get-TargetDevices)
    $r = Get-VibRange -Device $devs[0]
    $fresh = New-ChannelSet
    $fresh.Vib = $r.Default
    $script:State[(Get-Key)] = $fresh
    Load-SlidersFromState
    Apply-Now
    $lblStatus.Text = 'Reset to neutral.'
    $lblStatus.ForeColor = [System.Drawing.Color]::DimGray
})

$btnApply.Add_Click({ Apply-Now })

$btnInfo.Add_Click({
    $lines = @("Displays found: $($script:Displays.Count)", '')
    foreach ($d in $script:Displays) {
        $lines += $d.DeviceName + '   primary=' + $d.Primary
        $lines += '   model : ' + $d.Model
        $c = $script:Ccd[$d.DeviceName]
        if ($c) {
            $lines += '   ccd   : ' + $c.Friendly + '  via ' + $c.Connector + ' #' + ($c.ConnectorIndex + 1)
            $lines += ('   hdr   : supported={0} enabled={1}' -f $c.HdrSupported, $c.HdrEnabled)
            $lines += '   path  : ' + $c.DevicePath
        } else {
            $lines += '   ccd   : (not found)  ' + [CCD]::LastError
        }
        $lines += '   id    : ' + $d.MonitorId
        $lines += '   nvapi : ' + [CP11]::Describe($d.DeviceName)
        $r = Get-VibRange -Device $d.DeviceName
        $lines += ('   vibr  : ok={0} cur={1} min={2} max={3} def={4}' -f $r.Ok,$r.Current,$r.Min,$r.Max,$r.Default)
        $lines += ''
    }
    $lines += "NVAPI init: $script:NvOk"
    $lines += "Last error: $([CP11]::LastError)"
    [void][System.Windows.Forms.MessageBox]::Show(($lines -join [Environment]::NewLine), 'Display info')
})

# Windows wipes the ramp on resume / resolution change
$reapply = {
    Start-Sleep -Milliseconds 500
    foreach ($k in @($script:State.Keys)) {
        if ($k -eq '*ALL*') { continue }
        [void][CP11]::ApplyRamp($k, (Build-Ramp -Set $script:State[$k]))
    }
}
[Microsoft.Win32.SystemEvents]::add_DisplaySettingsChanged($reapply)
[Microsoft.Win32.SystemEvents]::add_PowerModeChanged($reapply)
$form.Add_FormClosed({
    try {
        [Microsoft.Win32.SystemEvents]::remove_DisplaySettingsChanged($reapply)
        [Microsoft.Win32.SystemEvents]::remove_PowerModeChanged($reapply)
    } catch { }
})

# ===========================================================================
# Startup - read state, but do NOT apply anything
# ===========================================================================
Refresh-ProfileList
Load-SlidersFromState
$script:LastKey = Get-Key

if ($script:NvOk) {
    $lblStatus.Text = 'Ready. Nothing applied yet - move a slider or pick a profile.'
    $lblStatus.ForeColor = [System.Drawing.Color]::DimGray
} else {
    $lblStatus.Text = "NVAPI unavailable: $([CP11]::LastError)  (brightness/contrast/gamma still work)"
    $lblStatus.ForeColor = [System.Drawing.Color]::Firebrick
}

[void]$form.ShowDialog()
