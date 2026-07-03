# HdrScope — HDR/SDR calibration tool for Windows

AI-assisted display calibration without a colorimeter: your eyes are the sensor,
the math does the rest. Built for and tested on a **Dell G2724D** (DisplayHDR 400)
with an AMD RX 6800, but the approach works on any HDR-capable Windows 11 display.

> Інструмент калібрування HDR/SDR без колориметра: око користувача виступає пороговим
> датчиком, решту робить математика. 12-кроковий майстер вимірює реальні можливості
> панелі та генерує системний ICC-профіль з тегом MHC2.

## What it does

- **12-step visual calibration wizard** — psychophysical threshold tests:
  - HDR peak luminance (MaxTML), 10% window and full-frame — staircase boundary-merge method
  - HDR black level threshold (MinTML)
  - Near-black and near-peak gradation ladders (detects shadow crush and tone-map headroom)
  - Panel SDR gamma via checkerboard luminance matching (25/50/75% densities)
  - Grayscale neutrality with **live DDC/CI RGB gain control** (F1–F6 adjust the monitor hardware)
  - Contrast white-clipping test (DDC/CI)
- **MHC2 ICC profile generator** — writes an ICC v4.3 profile with the Microsoft `MHC2` tag:
  - reports your *measured* peak/black to games (like Windows HDR Calibration, but with real data)
  - optional **sRGB→gamma 2.2 regamma LUT** (4096-point, PQ domain) that fixes the notorious
    "washed-out SDR content in HDR mode" problem
  - primaries taken from your panel's EDID
  - installs and associates via `ColorProfileAddDisplayAssociation` (advanced color, per-user)
- **HDR frame capture diagnostics** — grabs the composited scanout via
  `Windows.Graphics.Capture` in `R16G16B16A16Float` (scRGB) and verifies signal integrity
  patch-by-patch in absolute nits (histogram, false-color PNG, CSV)
- **Live system state** — advanced color status, bit depth, SDR white level via
  `DisplayConfigGetDeviceInfo`; HDR on/off toggle via `DisplayConfigSetDeviceInfo`
- **EDID parser** — panel primaries and CTA-861 HDR static metadata (factory max/min luminance)
- **Session logging** — every test result goes into a JSON designed to be handed to an
  AI assistant for analysis and recommendations

## Requirements

- Windows 11 (Advanced Color APIs), HDR-capable display
- .NET 9 SDK (build) / .NET 9 Desktop Runtime (run)
- GPU with D3D11 support

## Build & run

```
dotnet publish HdrScope -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
dist\HdrScope.exe
```

`HdrScope.exe --selftest` runs a headless pipeline check (render → capture → analyze → ICC write).

## How the gamma fix works

In HDR mode Windows composites SDR content assuming the piecewise sRGB EOTF, while
most monitors in SDR mode follow a pure 2.2 power curve — the mismatch lifts shadows
and washes out every non-HDR window. The generated profile embeds a scanout regamma LUT
(PQ domain) that remaps `W·srgb(c)` → `W·c^2.2` below the SDR white level `W` and is
identity above it. The LUT is tied to the SDR white level at generation time —
regenerate the profile after moving the slider.

## Safety

- DDC/CI writes are volatile monitor settings (same as OSD buttons); initial values are
  logged, `Home` restores them, factory reset always works
- ICC profiles are additive; roll back anytime via `colorcpl.exe`
- No undocumented APIs for destructive operations

## Results on the reference unit (Dell G2724D)

Two independent runs, test-retest within ±2%:

| Metric | Measured | EDID claims |
|---|---|---|
| Peak (10% window) | 576 nits | 427 nits |
| Peak (full frame) | 569 nits | 427 nits |
| Black threshold | 0.044 nits | 0.213 nits |
| Distinct levels up to 600 nits | 8/8 | — |

The panel tone-maps far beyond its certified DisplayHDR 400 rating — set 575 as the
in-game HGIG peak.

---

Built with [Claude Code](https://claude.com/claude-code). MHC2 tag layout per
[dantmnf/MHC2](https://github.com/dantmnf/MHC2); capture interop patterns from
Microsoft's Windows.UI.Composition samples (MIT).
