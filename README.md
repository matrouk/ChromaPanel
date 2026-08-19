# ChromaPanel

Per-display colour control for Windows — brightness, contrast, gamma, digital vibrance
and hue, with named presets that apply per monitor.

It replicates NVIDIA Control Panel's **Adjust desktop colour settings → Apply colour
enhancements** panel, but as a small standalone app with presets, a tray icon and a
command line.

- Single **~75 KB executable**. No installer, no .NET download, no dependencies.
- Correct **per-monitor** targeting, including two panels of the same model.
- Settings **save themselves** — there is no Save button.

---

## Install

Download `ChromaPanel.exe` from the [Releases](../../releases) page and run it.
Nothing else is needed: it uses the .NET Framework already built into Windows 10 and 11.

Settings live in `%APPDATA%\ChromaPanel\settings.json`.

## Use

| | |
|---|---|
| **Display** | Pick the monitor to edit. Each keeps its own settings *and its own preset*. Tick *Mirror* to change all at once, or *HDR* to toggle Windows HDR for that display. |
| **Preset** | Named looks. Switching display switches to that display's preset. A display you have never set one for falls back to the one marked `(default)`, which is also what the tray menu and `--apply` use. |
| **Colour** | Brightness / contrast / gamma per channel (All, Red, Green, Blue), plus digital vibrance and hue. |
| **Response** | Live plot of the three output curves. |

Changes apply as you drag, and are written to disk about a second after you stop.

### Command line

```
ChromaPanel.exe                 open the window
ChromaPanel.exe --tray          start hidden in the notification area
ChromaPanel.exe --apply         re-apply the saved per-display settings
ChromaPanel.exe --apply NAME    apply preset NAME to every display
ChromaPanel.exe --reset         return every display to neutral
```

`--apply` is useful in a scheduled task or a shortcut, since Windows wipes the gamma
ramp on resume and on some fullscreen exits.

---

## Build

```powershell
.\build.ps1            # -> build\ChromaPanel.exe
.\build.ps1 -Run       # build and launch
.\build.ps1 -Tools     # also build build\DumpDisplays.exe
.\build.ps1 -Test      # run the colour-maths and persistence tests
```

The build uses `csc.exe` from the .NET Framework that ships with Windows — no SDK,
no NuGet, no MSBuild. `build.ps1` locates it automatically.

`DumpDisplays.exe` is a read-only console dump of the same report the app's
**Diagnostics** button shows. It never writes a gamma ramp, so it is safe to run any
time, and it is the right thing to attach to a bug report about monitor naming.

### Layout

| File | Purpose |
|---|---|
| `src/Native.cs` | GDI gamma ramp, `EnumDisplayDevices`, CCD, EDID, NVAPI |
| `src/Model.cs` | Colour maths, presets, settings, persistence |
| `src/MainForm.cs` | The window |
| `src/Ui.cs` | Dark theme, custom slider, cards, curve plot |
| `src/Report.cs` | Diagnostics text, shared by the app and the console tool |
| `src/Json.cs` | Small dependency-free JSON reader/writer |
| `tests/Tests.cs` | Pure-logic tests: ramp maths, JSON, settings |
| `legacy/` | The original PowerShell build this grew out of |

---

## How it works

The five sliders are **two unrelated mechanisms**, which is the single most important
thing to know before changing anything:

| Setting | Mechanism |
|---|---|
| Brightness, Contrast, Gamma | Windows **gamma ramp** (`SetDeviceGammaRamp`, gdi32). Not NVIDIA-specific — NVIDIA's own panel drives the same OS API. |
| Digital Vibrance, Hue | **NVAPI** (`nvapi64.dll`), reached through `nvapi_QueryInterface` function-ID lookup. NVIDIA only. |

The registry values under `HKCU\Software\NVIDIA Corporation\Global\NVTweak` only
*persist* a value for next login — writing them applies nothing.

Curve, per channel:

```
bOffset = (Brightness - 50) / 100
cMul    = Contrast / 50
v = pow(i / 255, 1 / Gamma)
v = (v - 0.5) * cMul + 0.5 + bOffset
ramp[channel * 256 + i] = round(clamp(v, 0, 1) * 65535)
```

Exactly linear at neutral (50 / 50 / 1.00); an approximation of NVIDIA's curve
elsewhere.

### Things that look like bugs but are Windows

- **HDR disables the gamma ramp entirely.** Brightness, contrast and gamma do nothing
  on an HDR display. ChromaPanel detects this, says so, and gives you a checkbox to turn
  HDR off without opening Settings. Wide colour gamut is *not* HDR and does not disable
  the ramp — see below.
- **The ramp is wiped** on resume, resolution change and some fullscreen exits.
  ChromaPanel re-applies automatically while it is running.
- **Windows clamps extreme ramps** (`GdiIcmGammaRange`). ChromaPanel writes, reads back,
  compares, and only mentions the registry unlock if it actually detects clamping.

### Identifying monitors correctly

Three traps, all of which produced the same symptom — every display showing one
monitor's name:

1. `DISPLAYCONFIG_PATH_SOURCE_INFO` ends with a `statusFlags` field. Omitting it makes
   `DISPLAYCONFIG_PATH_INFO` 68 bytes instead of 72, so `QueryDisplayConfig` writes past
   every array element. That corrupts the heap (`STATUS_HEAP_CORRUPTION`, exit
   `-1073740940`) and misaligns every path after the first.
2. `EnumDisplayDevices(adapter, 0, …)` is **not** the attached panel. Windows keeps a
   record for every monitor ever plugged into that adapter, stale ones first. Take the
   first record with `DISPLAY_DEVICE_ACTIVE` set, or you will report a monitor that was
   unplugged months ago — the same one for every display.
3. Two panels of the same model share a friendly name. Distinguish them by connector,
   connector instance and EDID serial.

And one more, for HDR rather than naming:

4. `DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO`'s `advancedColorEnabled` bit **does not mean
   HDR**. It is also set when the display is merely in wide-colour-gamut mode, so a
   Windows 11 WCG display reports as HDR. Use
   `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2` (type **15**, 36 bytes) and read
   `activeColorMode` (0 = SDR, 1 = WCG, 2 = HDR). Where that is unavailable, fall back to
   `advancedColorEnabled && !wideColorEnforced`.

Names are taken from CCD first (what Windows Settings shows), then the EDID descriptor,
then `EnumDisplayDevices` last, because only the first two read the live connection.
Per-display settings are keyed on the device instance
(`DISPLAY#ABC1234#5&1a2b3c4&0&UID4353`), so they survive `\\.\DISPLAYn` renumbering.

---

## Releases

Source lives here; built binaries are attached to releases. Tagging is enough — CI
builds and publishes:

```bash
git tag v1.0.0
git push origin v1.0.0
```

## Licence

MIT — see [LICENSE](LICENSE).

Not affiliated with NVIDIA. NVAPI is used through its public entry point; the function
IDs are the documented published values.
