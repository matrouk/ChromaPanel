# ChromaPanel

Per-display colour control for Windows — brightness, contrast, gamma, digital vibrance
and hue, with named presets that apply per monitor.

It replicates NVIDIA Control Panel's **Adjust desktop colour settings → Apply colour
enhancements** panel, but as a small standalone app with presets, a tray icon and a
command line.

- Single **~350 KB executable**. No installer, no .NET download, no dependencies.
- Correct **per-monitor** targeting, including two panels of the same model.
- Settings **save themselves** — there is no Save button.

---

## Install

Download `ChromaPanel.exe` from the [Releases](../../releases) page and run it.
Nothing else is needed: it uses the .NET Framework already built into Windows 10 and 11.

Settings live in `%APPDATA%\ChromaPanel\settings.json`.

**Updating:** download the new `ChromaPanel.exe` and run it. If an older copy is still running in
the tray, the new one closes it and takes over. If *Start with Windows* points at the older copy,
it is moved over to the new one. Then you can delete the old file.

## Use

| | |
|---|---|
| **Display** | Pick the monitor to edit. Each keeps its own settings *and its own preset*. Tick *Mirror* to change all at once, or *HDR* to toggle Windows HDR for that display. |
| **Preset** | Named looks. Switching display switches to that display's preset. A display you have never set one for falls back to the one marked `(default)`, which is also what the tray menu and `--apply` use. |
| **Colour** | Brightness / contrast / gamma per channel (All, Red, Green, Blue), plus digital vibrance and hue. |
| **Response** | Live plot of the three output curves. |
| **Blackout** | A shortcut (default **Ctrl+Alt+B**) that turns chosen displays pure black. See below. |
| **Sharpening** | *Desktop:* five filters over the whole screen, per display, fullscreen games included. *NIS:* NVIDIA Image Scaling through the driver, currently locked while its driver settings are confirmed. |

Changes apply as you drag, and are written to disk about a second after you stop.

### Command line

```
ChromaPanel.exe                 open the window
ChromaPanel.exe --tray          start hidden in the notification area
ChromaPanel.exe --apply         re-apply the saved per-display settings
ChromaPanel.exe --apply NAME    apply preset NAME to every display
ChromaPanel.exe --reset         return every display to neutral
ChromaPanel.exe --blackout      black out the chosen display(s) until a key or click
```

`--apply` is useful in a scheduled task or a shortcut, since Windows wipes the gamma
ramp on resume and on some fullscreen exits.

### Desktop sharpening

Windows and the NVIDIA driver have no way to sharpen the desktop itself, so ChromaPanel does
it the way ShaderGlass's *Desktop Glass* mode does: each display is captured with DXGI desktop
duplication, run through a filter, and drawn into a click-through overlay. The desktop is
already at native resolution, so these restore detail rather than upscale — upscaling only
helps before an image has been stretched, which by the time it reaches the screen it has.

| Filter | Best for | GPU time, 2560x1440 (RTX 5070 Ti) |
|---|---|---|
| **AMD CAS** | Everyday use and text. Adaptive: sharpens soft areas more than sharp ones. | 0.05 ms |
| **AMD FSR 1 RCAS** | Games. Crisper than CAS, never overshoots its neighbours. | 0.03 ms |
| **Luma sharpen** | Text. Sharpens brightness only, so it cannot add coloured fringes. Gentlest. | 0.03 ms |
| **AdaptiveSharpen** | TV shows and films. Made for video that has already been resized, with anti-ringing. | 0.12 ms |
| **Anime4K Restore** | Anime. A neural network that rebuilds line art. Makes ordinary text look heavy, so use it while watching. | 0.6 ms |

At 0% the overlay switches off entirely. It only redraws when the screen changes, so an idle
desktop costs nothing. Anime4K keeps seven half-float feature maps per display (about 200 MB
of VRAM at 1440p) while it is selected.

What comes with the technique (ShaderGlass and Magpie share these):

- **About one frame of latency** on whatever is underneath. The mouse cursor is unaffected.
- **Fullscreen games are sharpened too**, unless you untick *Sharpen fullscreen games too*.
  Nothing is injected into the game; the overlay simply stays on top of it. Over a game the
  overlay adds its frame of lag, and Windows has to compose the game instead of flipping it
  straight to the screen, so G-Sync / FreeSync may not stay active. Untick it for competitive
  play.
- **Browsers and Store apps always get fullscreen to themselves** (Chrome, Edge, Firefox,
  Netflix, Disney+...), so fullscreen streaming plays normally.
- **Windowed DRM video (Netflix, Disney+...) shows black** while it is on. Protected content is
  blanked in every screen capture, and Windows reports any capture-excluded window, including
  this overlay, as protected content, so it cannot be detected and avoided. Turn sharpening
  off from the tray for windowed streaming.
- Games in true exclusive fullscreen (older DirectX 9 to 11 titles with fullscreen
  optimisations turned off) draw over everything and cannot be sharpened this way. Use
  borderless.
- **OBS records the unsharpened desktop**, because the overlay is excluded from capture.
- SDR and wide-gamut displays only; it switches itself off on a display with HDR on.

Sharpening already-sharp text can make ClearType's coloured fringes more visible, especially on
OLED sub-pixel layouts. Start low.

### Blackout (for OLED)

OBS keeps the display awake while it runs, so Windows' screen-off timer and screensaver
never kick in, and a static image sits on an OLED for hours. The blackout shortcut covers
the chosen display(s) with pure `#000000` — on an OLED those pixels are simply off.

- **OBS keeps recording what's underneath.** The black window is excluded from screen
  capture (`WDA_EXCLUDEFROMCAPTURE`), so Display Capture sees your real desktop, not a black
  frame. Untick the option if you *want* the capture to go black too. If it still shows up
  black in OBS, switch Display Capture's method to *Windows 10 (1903 and up)*.
- **Nothing loses focus.** The window never activates, so a game or OBS keeps running.
- **Coming back:** any key, click, scroll, or a deliberate mouse move (a bumped desk won't
  do it). The waking key or click is swallowed, so it doesn't land in whatever is underneath.
- Exclusive-fullscreen games draw over everything, including this. Borderless / windowed is fine.

`--blackout` does the same from a Stream Deck button or an OBS hotkey script.

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
| `src/DesktopSharpen.cs`, `src/Overlay.cs` | Desktop duplication, the click-through overlay, per-display render threads |
| `src/Sharpen.cs`, `src/Dx.cs` | The filter shaders and multi-pass renderer, and the raw Direct3D 11 / DXGI calls behind them |
| `src/Anime4K.cs` | Generated by `tools/gen_anime4k.py` from `third_party/anime4k` — do not edit by hand |
| `src/Blackout.cs`, `src/Hotkey.cs` | OLED blackout and the global shortcut |
| `src/Drs.cs`, `src/Sharpening.cs` | NVIDIA driver settings (game sharpening) |
| `tests/GpuTests.cs` | Runs every filter through Direct3D: CPU references for CAS, RCAS and Luma; a CPU Anime4K network parsed from the original GLSL; behaviour checks for AdaptiveSharpen; GPU-versus-WARP agreement for all |
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

MIT — see [LICENSE](LICENSE). The desktop filters use AMD FidelityFX CAS and FSR 1 RCAS
(MIT), bacondither's AdaptiveSharpen (BSD 2-clause) and bloc97's Anime4K (MIT); their notices
are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Not affiliated with NVIDIA. NVAPI is used through its public entry point; the function
IDs are the documented published values.
