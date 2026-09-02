# Lux
Lux is an image processing utility for the Light L16 camera. It was built to be a cross-platform replacemenet for the original Lumen software. Its goal is to offer byte-exact processing of the original Lumen software while also adding new features and improvements.

Currently, only the Lux 'light' CLI exists. This is meant to be a lightweight GUI replacement. Eventually, a full GUI will be developed.

## Requirements
- The .NET 10 SDK
- ffmpeg on PATH for the animated parallax formats (parallax-wiggle, parallax-wiggle-interp, parallax-orbit, parallax-single, parallax-rack, parallax-dolly).
  - Frames are produced by Lux, the GIF/WebP/AVIF/APNG container is written by ffmpeg. The CLI will exit if it cannot be found and one of these formats is requested,
- libgphoto2 on Linux only if MTP connection to the camera is desired

## Features
- .DNG and .JPG outputs 1:1 with Lumen
- MTP device connection for querying storage and pulling images (only tested on Linux right now)
- .HDR and .PPM export formats (contained in Lumen code but never used)
- GDepth and stereo depth map export support
- Individual module image export support (JPG only)
- Parallax effect animations and stills
- Full control over the image processing pipeline
- Bulk processing with multithreading

## Projects
- **Lux.Engine** — the image-processing engine (`.lri` parse, module ISP, registration/depth, fusion, ResAmp,
  colour profile, DNG/JPEG/HDR output, camera pull). Reused by the CLI and (later) a GUI.
- **Lux.Cli** — `lux-light`: `convert` (the one picture-producing verb — Lumen's DNG, JPG, HDR, PPM and
  JPEG+GDepth, plus the Lux formats that share the same render: the stereo depth pair, `lens-frames` and the
  experimental `parallax-*` animations and stills), `inspect`, `profile`, `isp`, `isp-run`, `mod-info`, `devices`,
  `pull`.

## Build & run
```
dotnet build Lux.slnx -c Release
./Lux.Cli/bin/Release/net10.0/lux-light convert <file-or-dir> [--out-directory out] [--formats all] [-j threads]
```
The [full CLI usage instructions](#full-cli-usage-instructions) are at the bottom of this document.

## Status
- DNG and JPG are byte-exact to Lumen (excluding the dense-stereo race condition and assuming processed in Lumen with no pre-existing .lris). 
- Performance needs some work, exports take 2-4 minutes to process all formats
- The library is heavily tested on Linux, Windows and OSX testing would be appreciated
- Automated builds are not up yet, coming soon

## Sample Images
All samples are one capture, `L16_00049`, written by `lux-light convert` and checked in under
[Samples/](https://github.com/mprovenz/lux/tree/main/Samples). Every image links to the full-size file on GitHub.

### Full-resolution JPG
The Lumen-identical JPG export (format `jpg`) at full size.

[![L16_00049.jpg](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049.jpg)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049.jpg)

### Depth map
The stereo depth map (format `depth`): the JPG half of the `<stem>_depth.f32` + `<stem>_depth.jpg` pair, on the exported grid.

[![L16_00049_depth.jpg](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_depth.jpg)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_depth.jpg)

### Lens frames
Every module of the capture as its own display JPG (format `lens-frames`): the five 28 mm A modules, of which A2 is the
monochrome module, and the five 70 mm B modules.

| A1 (28 mm) | A2 (28 mm, monochrome) | A3 (28 mm) | A4 (28 mm) | A5 (28 mm) |
|---|---|---|---|---|
| [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_A1.jpg" width="180" alt="L16_00049_A1.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_A1.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_A2.jpg" width="180" alt="L16_00049_A2.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_A2.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_A3.jpg" width="180" alt="L16_00049_A3.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_A3.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_A4.jpg" width="180" alt="L16_00049_A4.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_A4.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_A5.jpg" width="180" alt="L16_00049_A5.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_A5.jpg) |

| B1 (70 mm) | B2 (70 mm) | B3 (70 mm) | B4 (70 mm) | B5 (70 mm) |
|---|---|---|---|---|
| [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_B1.jpg" width="180" alt="L16_00049_B1.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_B1.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_B2.jpg" width="180" alt="L16_00049_B2.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_B2.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_B3.jpg" width="180" alt="L16_00049_B3.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_B3.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_B4.jpg" width="180" alt="L16_00049_B4.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_B4.jpg) | [<img src="https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_B5.jpg" width="180" alt="L16_00049_B5.jpg">](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_B5.jpg) |

### Parallax animations
Animated GIFs synthesised from the same capture.

#### Parallax wiggle
Format `parallax-wiggle`: the colour A-module frames, colour-matched and swept in spatial order. Module renders only, no registration.

[![L16_00049_parallax-wiggle.gif](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_parallax-wiggle.gif)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_parallax-wiggle.gif)

#### Parallax wiggle, interpolated viewpoints
Format `parallax-wiggle-interp`: virtual viewpoints along the rig's axis, synthesised from the depth, with disocclusions filled from the other real modules.

[![L16_00049_parallax-wiggle-interp.gif](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_parallax-wiggle-interp.gif)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_parallax-wiggle-interp.gif)

#### Parallax orbit
Format `parallax-orbit`: the same synthesis on a closed circular path.

[![L16_00049_parallax-orbit.gif](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_parallax-orbit.gif)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_parallax-orbit.gif)

#### Parallax single-view
Format `parallax-single`: single-view 2.5D, the sweep with no multi-view fill.

[![L16_00049_parallax-single.gif](https://raw.githubusercontent.com/mprovenz/lux/main/Samples/L16_00049_parallax-single.gif)](https://github.com/mprovenz/lux/blob/main/Samples/L16_00049_parallax-single.gif)

## Detailed image processing pipeline technical reference

A Light L16 `.lri` is a container of 10–16 independent camera frames. Producing one image is therefore a
multi-camera fusion problem, not a single-sensor develop. The pipeline runs in five phases — **load**, **per-module
ISP**, **registration + depth**, **fusion**, **export render** — and only the last phase differs between output
formats. All formats share one `PipelineCache`; the fusion render is ~74 % of wall-clock, so producing four formats
costs barely more than one.

Verification status: DNG, JPEG, PPM and Radiance HDR are byte-identical to Lumen apart from the export timestamp;
JPEG+GDepth is byte-identical including the embedded depth map.

---

### 1. Container and load

#### 1.1 Container

`.lri` is a chain of `LELR` blocks: 32-byte header (magic, `u64` block length, `u64` message offset, `u32` message
length, `u8` type) followed by a protobuf message. Type 0 = `LightHeader` (merged across blocks; carries a
`CameraModule` per camera), type 1 = `ViewPreferences`, type 2 = `GPSData`. Frame data lives at
`blockBase + surface.data_offset`.

Modules are identified by `CameraID`: **A1–A5** (28 mm), **B1–B5** (70 mm), **C1–C6** (150 mm). The header's
`image_reference_camera` names the reference module, which selects several downstream branches.

#### 1.2 Surface formats

| format | layout | decode |
|---|---|---|
| `RAW_PACKED_10BPP` | 10-bit packed Bayer, 8 px per 10 bytes | bit unpack |
| `RAW_BAYER_JPEG` | `BJPG` container: header, encoder compand LUT, 256-entry `u16` dequant LUT, then baseline JPEG streams | TurboJPEG `JDCT_ISLOW`, `TJPF_GRAY`, then dequant + scatter |

The Bayer-JPEG variant is chosen by CFA: version 0 stores **four half-resolution planes, one per CFA site**
(scattered back as `dst[(2y+(p>>1))·stride + 2x + (p&1)]`), so the DCT never sees the mosaic pattern; version 1
stores **one full-resolution plane** and is used only for the monochrome module, which has no pattern to split.

#### 1.3 Stacked captures

In low light the camera writes **N frames per module** (one `CameraModule` block each, distinguished by
`frame_index`). Observed stacks are always 4 frames with identical exposure, gain, lens and mirror position.
`lt::StackFusion` merges them per module, upstream of everything else: the same 16×16 wavelet block merge used by
the inter-module fusion, with per-frame optical flow, outlier rejection, Hann-window accumulation and ÷N. Frame 0
is the reference. There is **no exposure-ratio term** — the algorithm assumes identical frames, which is what the
camera produces.

Consequence: for a stacked capture the module ISP sets `hot_pixel_removal`, `hot_pixel_leakage_removal` and
`highlight_restore` to `none`. The stack merge rejects transient outliers better than a single-frame spatial
filter can, so the dedicated stages are turned off rather than allowed to damage real detail.

#### 1.4 Load-time state

Derived once per capture and used throughout: **AsShotNeutral** (from the reference module's colour calibration at
the capture CCT/tint), **BaselineExposure** (log₂ of the gain·integration-time ratio), **`lens_shading.multiplier`**
(from CFA site histograms of the reference frame), and per-frame **black level** estimates.

---

### 2. Per-module ISP (`lt::SoftISP`)

Runs independently on each module's Bayer frame, in the Bayer domain, at a *config level* that selects resolution
and denoise strength. Stage order as executed (level 0, colour module):

| # | stage | algorithm | purpose |
|---|---|---|---|
| 1 | `HotPixelRemoval` | per-CFA-site ring/diagonal comparison against σ-tables at the frame's gain; candidate when `excess > σ[base]·k·4` | remove stuck sensor sites |
| 2 | `HighlightRestore` | reconstruct clipped channels from unclipped neighbours | recover blown highlights |
| 3 | `Placeholder` (linearize) | black subtract, normalise by `1/(white−black)`, apply WB scale | to normalised float |
| 4 | `CrossTalkCorrection` (`ir_correction`) | IR blend estimated from R/B site ratios | remove IR contamination |
| 5 | `Demosaicking` | `light_v1` (gradient-directed) at full res; `collapse2/4/8` (box-collapse) at lower levels | Bayer → RGB |
| 6 | `ColorNoiseReduction` | vec4 Gaussian/Laplacian pyramid, 5-tap `[.05 .25 .4 .25 .05]` | chroma denoise |
| 7 | `AdaptiveDesaturation` | shadow/highlight cutoff-driven saturation roll-off | tame extreme chroma |
| 8 | `Denoising` (`hybrid`) | bilateral + patch NLM over an STD plane, window 3/5/7/9 by ISO | luma denoise (off at level 0) |
| 9 | `PostProcessing` | sharpen / local contrast | detail |
| 10 | `LensShading` | calibrated per-module vignetting grid | flat field |

Branches: stages 1–2 are gated on **frames-per-stack < 2**, not on sensor colour. The monochrome module (A2,
`sensor_bayer_red_override = (-1,-1)`) never runs this ISP in Lumen — its path is the fusion one — so CFA-dependent
stages here have no defined behaviour on it.

---

### 3. Registration and depth

#### 3.1 Sparse registration

`StereoAsyncApi` builds the geometric relationship between modules:

1. **Setup** — per-camera crops, view poses, and the initial `AlignedCalib` pair from factory calibration.
2. **Feature matching**, per pyramid level, per non-reference camera:
    - top level, A-reference: `initLowerACamera<8>` — 8×8 templates, calibration- and plane-depth-guided prediction;
    - top level, B4/C5 reference: `initLowerBCamera<8>` — no calibration or plane depth, prediction is `ref − offset`,
      search is a ±`w/4` epipolar band of half-width 5, scoring is **zero-mean SAD**;
    - lower levels: `matchFeaturesPerLevelLowerCams<8>` / `…HigherCams<12>`.
      Constants: Lowe ratio 0.8, reverse ratio 0.95, score 10.0.
3. **RANSAC gate** — per-view inlier selection with distinct thresholds for the two views.
4. **Bundle adjustment** — `LightBA` over the observation set, then per-camera write-back for cameras with ≥ 26
   usable observations.
5. **Mirror-angle refinement** — coarse angular search for the movable-mirror B/C modules.

Output: per-camera `(view, module)` `AlignedCalib` pairs.

#### 3.2 Dense depth

Truncated-SAD cost volume + **semi-global matching** on the half-resolution green plane, then `DenseUpsampleLayer`
(guided upsample against a `ReferenceGuide` built from the reference frame) to a full-frame inverse-depth image
covering the whole canvas.

Note: Lumen's dense stage is **run-to-run non-deterministic** — two runs over the same capture disagree in ~1 500 of
3.2 M level-5 samples. Byte-exact comparison of a level-0 export requires pinning to the matching run.

---

### 4. Fusion

#### 4.1 Level 1 — `PackedBayerFusion`

Merges the reference-group modules onto the reference grid. 16×16 block wavelet merge: per-block optical flow
(`BlockFlow`) against the reference, CDF-style wavelet decomposition, coefficient `Shrink` driven by the sensor
noise model at the frame's gain, Hann-window accumulation, normalise by contributor count. Pyramid depth is 4
levels (5 for a C-group reference — unreachable in practice).

`MonoFusion` folds the monochrome module's luminance detail into the colour result over a 5-level wavelet pyramid.

#### 4.2 Level 0 — `ImageResolutionAmp`

Super-resolution using the higher optical group (B for an A reference, C for a B reference). Coarse alignment,
CDF 9/7 analysis, per-module preparation and warping through the dense depth (`WarpField`), merge and inverse
merge. This is the step that turns 13 MP sensors into a 52 MP output.

---

### 5. Colour

`LumenProfile` computes, per capture:

- **Illuminant entries** — every `ColorCalibration` of the reference module, with CCT/tint from its (x, y).
- **ColorMatrix 1/2** — the proto `color_matrix` of the lowest- and highest-CCT entries, verbatim.
- **ForwardMatrix 1/2** — a **refit**, not the proto value: a Ceres line-search optimisation over Lab error using
  the Macbeth reference data.
- **HueSatMap 1/2** — a 32×32×1 hue/saturation/value LUT from a thin-plate-spline fit of the same data, in linear
  ProPhoto D50.
- **Tone curve**, **AsShotNeutral**, **BaselineExposure**.

---

### 6. Export

#### 6.1 Geometry (all formats)

`setInputDataStream` establishes the canvas: `canvas = (int)(sensor · f)` snapped to the sensor's 64×48 aspect grid,
where `f` is the focal ratio of **the next optical group up over the reference group** — 70/28 = 2.5 for an
A reference, 150/70 ≈ 2.143 for a B reference. Export level 0 is `crop × canvas`, likewise snapped; the crop comes
from `ViewPreferences`. Five levels are built, each a halving.

`GetExportTransformOutput` then resolves a requested output size to a source level, a source rect and a scale. When
`scale > 1.5` the resampler is a separable **Lanczos-2 blur followed by bilinear warp**; otherwise it is a 64-phase
**Catmull-Rom** (`ImageWarpClamped<2>`).

#### 6.2 Per-format paths

The output ISP is gated on `(fmt | 4) == 4`, i.e. **only JPEG and JPEG+GDepth re-run it**. Formats 1, 2 and 3 take
the float render directly.

| fmt | output | path |
|---|---|---|
| 0 | JPEG | output ISP → 8-bit → libjpeg-turbo baseline, quality 98, 4:2:0, standard Huffman tables, Exif + `LibCP` comment |
| 1 | PPM | float → `×255`, `cvtps2dq` (round-half-to-even), saturate → `P6` header + flat RGB |
| 2 | DNG | float tiles → vignetting removal → resample → `×16384` → lossless JPEG (SOF3, predictor 1) tiles → DNG container |
| 3 | Radiance HDR | float → RGBE, flat uncompressed, `#?RADIANCE` header; **scene-linear camera raw**, not display-referred |
| 4 | JPEG + GDepth | fmt 0, plus the depth map as XMP |

#### 6.3 Output (display) ISP — formats 0 and 4 only

Runs in the Color domain on the fused tile, with a constant halo of 64:

| slot | stage | algorithm |
|---|---|---|
| 2 | ÷ neutral | divide by AsShotNeutral |
| 10 | `ColorCorrection` (`optimized`) | matrix + CCT-interpolated HSV LUT, 33×33 bilinear hue/sat, in linear ProPhoto D50, gamut clip |
| 11 | `PostProcessing` | sharpen |
| 12 | `LensShading` (`inverse`) | re-apply shading model inverted |
| 13 | `ToneAdjust` (`laplacian_pyramid`) | local Laplacian tone adjustment |
| 14 | `ContrastAdjust` | `0.217·(x/0.217)^(2^(0.3a))` via fast log2/exp2 |
| 15 | `ToneMapping` | ACR-family LUT + `RGBTone` hue preservation, then ProPhoto → sRGB |

The tone-mapping variant (`light_v1` / `light_v2` / `acr`) is selected by a renderer flag that differs between the
GUI and a headless render; both branches are implemented.

#### 6.4 Colour-space property

The application sets export property `0x13` from the format: `fmt ≤ 1 → 4 (sRGB)`, `fmt == 2 → 0 (none)`,
`fmt == 3 → 1 (linear sRGB)`, anything else throws — which is why format 4 is unreachable from Lumen's UI. A DNG
therefore carries `ColorSpace = 65535` and no Interop IFD; a JPEG carries `ColorSpace = 1`. For formats 1–3 the
property is **inert**: those paths never read the output tuning.

#### 6.5 GDepth (format 4)

The depth map comes from a dedicated double-buffered depth image cache at **pipeline level 1**, holding
`InverseDepthClip(ImageResample<0>(InverseDepth(fullDepth)), 100000)` — metric millimetres, resampled nearest in
inverse-depth space, clipped at 100 m. It is warped to the export grid by the same transform as the colour image,
scanned for `Near`/`Far`, quantised to 8-bit by

```
E = recip(d·(far − near)) · (d − near) · (255·far)
```

encoded as a grayscale JPEG, base64'd into `GDepth:Data`, and split across extended-XMP APP1 chunks of 65 458 bytes.
`Far` is always the 100 m clip constant, not a scene value, so the top of the range is coarsely quantised.

Inverse mapping, for consumers: `d = far·near / (far − u·(far − near))` with `u = E/255`.

---

### 7. Modification options

| option | effect |
|---|---|
| export size / level | selects the source level and resampler via `GetExportTransformOutput` |
| crop | from `ViewPreferences`; sets export level 0 relative to the canvas |
| rotation | baked into the transform; `Orientation` stays 1 |
| tone mapping type | `acr` / `light_v1` / `light_v1_lowlight` / `light_v2` |
| compression | DNG: uncompressed strips or lossless JPEG tiles |
| colour space | property `0x13`; only consumed on the JPEG path |
| exposure / f-number / ISO / focal | Exif values; `AllInFocusFNumber` is computed as `aperture · equiv35 / physical` |


## Full CLI usage instructions
### USAGE

```
lux-light <command> [options] [input...]
lux-light --help
```

### COMMANDS

| Option | Description |
|---|---|
| `convert <input...>` | Process a .lri and export it exactly as Lumen does: the image pipeline run once, written out in any combination of export formats (see below). Everything comes from the .lri, and no option changes the Lumen pixels unless you pass one from ADJUSTMENTS; --level/--size/--origin only pick the output grid. |
| `inspect <input...>` | Print the load-time state derived (neutral, ev, multiplier) |
| `mod-info <input...>` | Per-module CameraID, gain, exposure, frame black, relative_brightness |
| `profile <input...>` | compute and print the colour profile (matrix fit, ForwardMatrix, HueSatMap) |
| `isp <input...>` | Module-ISP tuning per config level, stage list, white-balance state |
| `isp-run <input...>` | Run the module ISP over a centre ROI, write a gamma-encoded PPM |
| `devices` | List connected MTP cameras |
| `pull [options]` | Pull matching files off the camera |

### INPUT

convert, inspect, mod-info, profile, isp and isp-run take one or more .lri files and/or directories (directories are scanned for *.lri). devices and pull take no input files.

### CONVERT OPTIONS

With no options specified, `convert` replicates Lumen output exactly: DNG (fmt 2) + companion JPG (fmt 0), full size, every value processed from the .lri. The flags marked * depart from that, and the run echoes which were applied.

#### OUTPUT

| Option | Description |
|---|---|
| `-o, --out-directory <dir>` | Write `<stem>.<ext>` per input (default: a lux_convert/ beside the .lri) |
| `--out-file <path>` | Name the output file (only when the run makes exactly one file) |
| `-j, --threads <n>` | Inputs converted in parallel (default: CPU count) |
| `--formats <list>` | Original (extended): dng, jpg, hdr, ppm, jpg+depth<br>New:   depth, lens-frames, parallax-wiggle, parallax-wiggle-interp, parallax-orbit, parallax-single, parallax-rack, parallax-dolly, parallax-dof, parallax-anaglyph, parallax-crosseye, parallax-sbs, parallax-still<br>all:   every format above except hdr and ppm<br>Comma-separated (default dng,jpg)<br>`depth` is the metric-millimetre stereo pair `(<stem>_depth.f32` + `<stem>_depth.jpg)` on the exported grid. The Lux formats are named `<stem>_<format>.<ext>;` lens-frames writes `<stem>_<module>.jpg.` |

#### GRID (picks the pixel grid; leaves tone and colour alone)

| Option | Description |
|---|---|
| `--level <n>` | export level, 0 = full size (default) |
| `--size <w,h>` | explicit output size (default: the level's export window) |
| `--origin <x,y>` | level-0 export window origin |

#### SHARED ADJUSTMENTS

| | Option | Description |
|---|---|---|
| \* | `--rotate <90\|180\|270>` | bake the orientation (Orientation stays 1). All four raster formats; a depth map written alongside them follows the same rotation. Not combinable with the parallax formats (their rig geometry is in the unrotated frame) |
| \* | `--fnum <f>` | Exif FNumber [dng, jpg, jpg+depth] |
| \* | `--iso <n>` | Exif ISO [dng, jpg, jpg+depth] |
| \* | `--focal <n>` | Exif focal length in mm [dng, jpg, jpg+depth] |

#### DNG

| | Option | Description |
|---|---|---|
| \* | `--dng-cs <n>` | colour-space property 0x13 (default 0 = none, the app path) |
| \* | `--dng-tone <profile>` | acr \| light_v1 \| light_v1_lowlight \| light_v2 |
| \* | `--dng-comp <0\|1>` | 0 uncompressed, 1 lossless JPEG (default) |

#### JPEG

(each of these also applies to jpg+depth)

| | Option | Description |
|---|---|---|
| \* | `--jpeg-cs <n>` | colour-space property 0x13 (default 4 = srgb) |
| \* | `--jpeg-quality <n>` | libjpeg quality (default 98) |
| \* | `--jpeg-sub <0\|1\|2>` | chroma subsampling (default 2 = 4:2:0) |
| \* | `--jpeg-v2` | the renderer+0x64 v2 tone-mapping gate |
| \* | `--jpeg-modify <ts>` | Exif 0x0132 ModifyDate, YYYY-MM-DDTHH:MM:SS (default: now) |
| \* | `--jpeg-comment <s>` | the JPEG COM marker text |
| \* | `--jpeg-software <s>` | the Exif Software string |

#### HDR

| | Option | Description |
|---|---|---|
| \* | `--hdr-cs <n>` | colour-space property 0x13 (default 1 = linear_srgb) |

#### PPM

*(no options)*

#### DEPTH

*(no options)*

#### LENS-FRAMES

(Each module of the capture as a display JPG through the ported module ISP. A STACKED capture has several frames per module: `all` writes every one as `<stem>_f<k>_<module>.jpg,` an index picks one, and the default is frame 0, the frame Lumen's StackFusion references.)

| Option | Description |
|---|---|
| `--lens-quality <n>` | JPEG quality 1-100 (default 92) |
| `--lens-ev <float>` | exposure adjust in stops (default 0.95) |
| `--lens-level <n>` | module-ISP config level (default 0 = full-res, no denoise) |
| `--lens-profile <p>` | CIAPI RendererProfile 0-3 (default 3 = Desktop) |
| `--lens-modules <list>` | comma-separated module filter, e.g. A1,B4 |
| `--lens-stack <all\|n>` | which frame of a stacked capture (default 0) |

#### PARALLAX

(EXPERIMENTAL: The base imagery is the pipeline's module-ISP frames, or this run's JPEG render and its metric depth. Only the 28 mm A array is a multi-view rig; a telephoto capture has no A modules, so parallax-wiggle refuses it and the depth formats run single-view.)

| Option | Description |
|---|---|
| `parallax-wiggle` | A-group frames, colour-matched, swept in spatial order. Module renders only, no registration [ffmpeg] |
| `parallax-wiggle-interp` | N virtual viewpoints along the rig's axis, synthesised from the depth, disocclusions filled from the other real modules [ffmpeg] |
| `parallax-orbit` | the same synthesis on a closed circular path [ffmpeg] |
| `parallax-single` | single-view 2.5D: the sweep with no multi-view fill [ffmpeg] |
| `parallax-rack` | animated rack focus [ffmpeg] |
| `parallax-dolly` | dolly zoom, pulling back while zooming in [ffmpeg] |
| `parallax-dof` | synthetic depth of field, one PNG still |
| `parallax-anaglyph` | red/cyan (Dubois) anaglyph of a synthesised stereo pair, PNG |
| `parallax-crosseye` | cross-eye side-by-side stereo pair, PNG |
| `parallax-sbs` | parallel-view side-by-side stereo pair, PNG |
| `parallax-still` | one synthesised viewpoint, PNG |

Every format but parallax-wiggle forces the level-0 build (announced, as jpg+depth is) and synthesises from this run's JPEG render at --level, downscaled to --parallax-size: --level 2 renders the base at 2608 px, plenty for a 1600 px animation and far faster than full size.

| Option | Description |
|---|---|
| `--parallax-format <c>` | animation container: gif (default) \| webp \| avif \| apng |
| `--parallax-size <n>` | long edge of the working image in px (default 1600; 0 = native, for parallax-wiggle) |
| `--parallax-ms <n>` | per-frame duration in ms (default 100 for parallax-wiggle, 70 for -rack and -dolly, 60 otherwise) |
| `--parallax-frames <n>` | virtual viewpoints / animation frames (default 24) |
| `--parallax-loop <k>` | pingpong (default) \| forward (the orbit is closed and plays forward) |
| `--parallax-fill <k>` | donors (default: disocclusions from the other real A modules, then inpaint) \| inpaint \| none (holes left black, to see where they are) |
| `--parallax-path <k>` | sweep (default, along the rig's dominant axis, 12 deg off horizontal from the factory extrinsics) \| arc \| line, for parallax-wiggle-interp and -single |
| `--parallax-baseline <mm>` | peak-to-peak path extent (default 71.49, the widest physical colour baseline A4-A5; usable to about twice that) |
| `--parallax-converge <k>` | convergence plane: auto (default, the median depth) \| none \| metres. Everything at that depth stays fixed and the scene swings around it |
| `--parallax-converge-at <x,y>` | read the convergence depth off the depth map at that pixel of the working image instead |
| `--parallax-ipd <mm>` | stereo interocular distance (default 25 for the anaglyph, 63 for the side-by-side pairs; 63 with a near foreground is hard to view) |
| `--parallax-anaglyph <k>` | dubois (default, least-squares de-ghosting) \| colour \| grey |
| `--parallax-focus <m>` | parallax-dof focus distance in metres (default: the 10th-percentile depth) |
| `--parallax-focus-at <x,y>` | read the focus depth off the depth map at that pixel instead |
| `--parallax-aperture <mm>` | aperture DIAMETER for -dof and -rack (default 20 = f/1.4 on the A group's 28 mm equivalent) |
| `--parallax-layers <n>` | depth layers in the composite (default 8) |
| `--parallax-rack <m1,m2>` | rack the focus from m1 to m2 metres (default: the 10th- to the 90th-percentile depth) |
| `--parallax-rack-at <x1,y1;x2,y2>` | rack between the depths at two pixels instead |
| `--parallax-subject <m>` | parallax-dolly: the depth held constant, metres (default: the 10th percentile) |
| `--parallax-subject-at <x,y>` | read it off the depth map at that pixel instead |
| `--parallax-dz <mm>` | dolly travel (default 400; positive pulls back and zooms in, the one direction a single capture can carry — negative is clamped) |
| `--parallax-t <tx,ty>` | parallax-still: the virtual camera translation in mm (default 40,0) |
| `--parallax-quality <n>` | WebP encoder quality (default 88) [--parallax-format webp] |
| `--parallax-crf <n>` | AVIF encoder crf (default 18) [--parallax-format avif] |
| `--parallax-order <k>` | parallax-wiggle frame order: sweep (default, along the rig's dominant axis from the factory extrinsics) \| label (module-name order) \| an explicit list, e.g. A5,A1,A3,A4 |
| `--parallax-pivot <x,y,w,h>` | parallax-wiggle: hold a region still so the scene swings around it; integer shift only, no resampling. Native sensor pixels |

### INSPECT OPTIONS

*(none — inspect takes only input paths)*

### MOD-INFO OPTIONS

*(none — mod-info takes only input paths)*

### PROFILE OPTIONS

*(none — profile takes only input paths)*

### ISP OPTIONS

| Option | Description |
|---|---|
| `--isp-level <n>` | module-ISP config level (default 0; -1 prints every level 0-5) |
| `--profile <p>` | CIAPI RendererProfile 0-3 (default 3 = Desktop) |

### ISP-RUN OPTIONS

| Option | Description |
|---|---|
| `--isp-level <n>` | module-ISP config level (default 0) |
| `--profile <p>` | CIAPI RendererProfile 0-3 (default 3 = Desktop) |

*(the PPM is written beside the .lri as `<stem>_isp_l<n>.ppm)`*

### DEVICES OPTIONS

*(none)*

### PULL OPTIONS

| Option | Description |
|---|---|
| `-o, --out-directory <dir>` | file destination (default: ./lux_pull) |
| `--ext <list>` | extensions to include (default .lri) |
| `--glob <pattern>` | filename glob, e.g. "L16_004*" |
| `--since <date>` | only files modified on/after this date |
| `--overwrite` | re-download even if a same-size file exists locally |
| `--list` | list matching files without downloading |

### GLOBAL OPTIONS

| Option | Description |
|---|---|
| `-h, --help` | this help |

### ENVIRONMENT VARIABLES

\* For diagnostic / development use only

#### LOGGING

| Option | Description |
|---|---|
| `LUX_VERBOSE=1` | convert: per-file progress detail, and the full exception trace when a file fails instead of just its message |

#### BEHAVIOUR — these change what the pipeline computes

| Option | Description |
|---|---|
| `LUX_CC_FM=<fm1;fm2>` | replace the fitted ForwardMatrix1/ForwardMatrix2 in colour correction with two space-separated 9-float matrices |
| `LUX_CC_M=<m>` | replace the camera→working matrix outright (9 floats, or 0x-prefixed bit patterns), bypassing the fit and the CCT interpolation |
| `LUX_CNR_NEUTRAL=r,g,b` | colour-noise reduction uses this neutral instead of the ISP stats' |
| `LUX_CNR_NOEXT=1` | colour-noise reduction reads no pixels outside its own region (drops the ±2-pixel ring) |
| `LUX_FRAME_BLACK=<f>` | linearize uses this black level instead of the stats'/frame's |
| `LUX_FUSION_SRC_BLACK=db\|estimate` | the black level for the fusion's non-reference source frames: db = the sensor database value, estimate = the per-frame estimate |
| `LUX_GUIDE_SKIP=<list>` | set these comma-separated stages to type "none" in the reference-guide ISP tuning |
| `LUX_JPEG_ROUND=rne` | the JPEG export stores through the display path's round-half-to-even convert instead of the export path's round-half-away-from-zero |
| `LUX_LU_RANK=rank` | the Ceres line search's FullPivLU uses the Eigen 3.3 rank threshold instead of the 3.2 nonzero-pivot rule Lumen's ceres.dll behaves like |
| `LUX_NO_GUIDE=1` | registration runs without building the reference guide image |
| `LUX_NO_MONO=1` | force the colour fusion branch on a capture that has a monochrome module |
| `LUX_SGM_XSW=1` | the SGM cost sweep clamps its forward x start to W instead of W-1 |
| `LUX_SKIP_MISSING=1` | an unimplemented stage is skipped with a warning instead of throwing |
| `LUX_SKIP_STAGE=<list>` | these comma-separated stages keep their padding and alignment but are never run |
| `LUX_STACK_MARGIN=<n>` | ISP tile margin for a stacked capture's reference fusion, instead of the halo the sensor gain selects |
| `LUX_STEREO_DEMOSAIC=<t>` | the demosaicking type of the stereo ISP tuning (default collapse2) |
| `LUX_STEREO_SKIP=<list>` | set these comma-separated stages to type "none" in the stereo ISP tuning |
| `LUX_STEREO_TILE=<n>` | the stereo ISP work-image tile size (default 256, Lumen's) |
| `LUX_TELE_CFG=<n>` | the telephoto module's ISP config level (default 1, Lumen's) |
| `LUX_TELE_ONLYRECT=x0,y0,x1,y1` | run the telephoto ISP only for that one grown rect; every other rect comes back zeroed |
| `LUX_TELE_SET=k=v;k=v` | apply these tuning overrides to the telephoto module's ISP tuning |
| `LUX_WARP_ORDER=cols\|rows\|swap\|prod` | accumulation order of the 6-tap warp interpolation (default cols) |

#### PRINTING — stderr only; the pixels are unaffected

| Option | Description |
|---|---|
| `LUX_BIL_DEBUG=1` | the bilateral kernel window and size at each call |
| `LUX_CC_DEBUG=1` | the colour-correction CCT interpolation inputs, with bit patterns |
| `LUX_CNR_DEBUG=<any>` | the colour-noise-reduction per-tile statistics for the (0,0) tile |
| `LUX_HP_DEBUG=x0,y0,x1,y1` | per-pixel hot-pixel decisions inside that rect |
| `LUX_ISP_DEBUG=1` | the ISP runner's stage list and per-stage rects (once), plus the linearize black/white, the crosstalk blend and the hybrid-denoise thresholds |
| `LUX_PP_DEBUG=x,y` | the post-processing tile arithmetic around the pixel at x,y |
| `LUX_TELE_DEBUG=1` | per-telephoto-module white balance, black, IR blend and tone tuning |

#### WRITING FILES — each writes raw intermediate images beside the prefix you give

| Option | Description |
|---|---|
| `LUX_CNR_DUMP=<prefix>` | colour-noise reduction: the kernel input region, its arguments and every pyramid level, as `<prefix>_cnr_*.f32` / _cnr_args.txt |
| `LUX_DEMOSAIC_DUMP=<dir>` | the light_v1 demosaic's A/B/C planes, as A.f32/B.f32/C.f32 in `<dir>` |
| `LUX_HP_DUMP=<prefix>` | hot-pixel removal: the per-channel sensor σ tables, as `<prefix>_sigma<c>.f32` |
| `LUX_HYB_DUMP=<prefix>` | hybrid denoise: every intermediate, as `<prefix>_hyb_<tag>.f32` |
| `LUX_MONO_DUMP=<prefix>` | mono fusion: the per-stage RGB and float-Bayer images of the mono module's own ISP, as `<prefix>_own_st<i>_<stage>.<kind>.bin` |
| `LUX_TELE_ISPDUMP=<pre>` | the telephoto level-0 cache's whole grown-rect ISP output, as `<prefix>_<module>_isp_<x0>_<y0>_<x1>_<y1>.bin` |
