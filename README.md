# Space Engineers Vulkan Renderer

A native **Vulkan** renderer for Space Engineers 1, delivered as a Pulsar client plugin. It replaces
the stock Direct3D 11 renderer through the game's `IMyRender` / render-message boundary instead of
patching D3D11, with one code path intended for Windows and Linux (Linux is tested first).

The project goal is to match stock visuals first, then exceed them with modern techniques: GPU-driven
rendering, better lighting and shadows, IBL/GI, TAA/FSR, HDR output and scalable graphics options.

## Status

Implemented and verified in game:

- Main menu, HUD, text, Vulkan settings dialog, world load/save/reload.
- Camera, environment, MWM models, cube-grid instancing, static groups (generic instance buffers
  are stored but not drawn yet).
- Pure alpha-masked and transparent materials, weighted OIT, GPU skinning.
- Stock material/color pipeline with colorization: block paints and skins work (`ColorMaskHSV` is a
  paint delta plus the stock `ColorizeHSV` conversion, using the shipped sRGB/HSV `ColorizeGray` path);
  per-vertex tangent normal mapping (derivative fallback), face-normal lighting on beveled panels (the
  normal map supplies gloss only, and shadow offset/bias use the face normal), a dynamic reflection probe, an
  ambient floor, and Toksvig gloss are in place.
- **Voxel/planet rendering is always enabled**: the game clipmap supplies cells, meshes upload to device-local
  buffers, and stock ColorMetal/NormalGloss/Extensions textures are sampled with triplanar mapping, including
  snow/ice/rock/grass materials. Safety limits: 256 MB budget, 8192 draw cap, distance culling, `VULKAN_NO_VOXEL`
  diagnostic mode, device-fault logging and a crash-loop marker. **Known blocker:** framing a tree still causes a
  GPUVM page fault at address 0 even with voxels disabled; tree/foliage support remains open. Atmosphere, foliage
  billboards/grass and voxel shadows are not implemented yet.
- Cube instance buffers (generic instance buffers are stored but not drawn yet), per-actor LOD
  selection, frustum/view-distance culling.
- GPU-driven opaque rendering: persistent part/material/LOD ranges, GPU LOD selection with stock
  hysteresis, indirect cull/no-cull buckets, including instanced cubes and static groups.
- Static skybox IBL, dynamic point/spot lights, offscreen LCD/cockpit screens. Lights honor per-light
  color/intensity, range, falloff, gloss/diffuse factors and spot aperture; block-local light transforms are
  applied through their parent cull object and the nearest 64 are uploaded each frame.
- Plugin-owned auto-exposure defaults, Hable tonemapping, HDR bloom that includes OIT/glow, and live
  lighting/exposure/bloom sliders (including **Emissive strength**). Slider values are GPU-resident and updated
  only when changed. Exposure, white point, emissive scale and LOD selection no longer follow the vanilla video
  settings, so the plugin and stock renderer keep independent looks.
- Deferred G-buffer reflections: skybox IBL, a camera-centred dynamic scene probe at configurable
  128²/256²/512² (**Reflection probe**), including skinned characters and thruster/glow billboards. The probe
  captures **one face per frame** into a target cube and blends current/target over a live 0.05–1 s window; a
  completed cube is used directly between cycles. Live **Reflection close/far distance**, **far blur** and
  **minimum blur** sliders select a smooth receiver-distance mip tier, so near surfaces keep detail while distant
  ships/planets contribute colour and broad shapes from the same live scene cube. Probe captures are single-sample
  and skip shadow sampling. Opaque IBL now applies to all opaque materials (stock AmbientSpecular), so grounded
  ships pick up green/white ground colour; screen-space reflections are **off by default** and remain an optional
  metal/high-gloss sharpening pass (Low/Medium/High).
- **FXAA** runs after tonemapping with a **Low / Medium / High / Very high** quality slider (Medium matches stock).
  Optional **MSAA** 2x/4x/8x smooths block silhouettes and round objects (restart required); **Supersampling**
  (1.0-2.0) remains available for stronger GPUs.
- Advanced **Light distance** (150-1000 m), **Shadow distance** (50-1000 m, default 250 m), **Model detail**, MSAA and Supersampling quality controls.
- Thruster/particle billboards: the stock thruster flame animator is reproduced from the game's render-component
  messages, and standard particle billboards are drawn through OIT. Idle thrusters keep their point glow; firing
  adds the line plume (hydrogen and ion).
- Opt-in cascaded sun shadows: four 4096² reverse-Z maps fitted to the view frustum every frame, with a
  **Shadow distance** slider (50-1000 m, default 250 m), 16-tap Poisson PCF with bilinear depth, face-normal
  offset/bias, alpha-tested casters and back-face culling. Shadow casters
  come from a separate **LOD0 bucket**, so a ship switching to LOD1+ does not lose its shadows. Two-sided
  lighting flips the camera-oriented face normal; the normal map supplies gloss only, so normal-map detail cannot
  flicker the front/back boundary at grazing angles. Enable with **Graphics > Vulkan Renderer > Shadows** (restart required) or
  `VULKAN_SHADOWS=1` (`VULKAN_SHADOWS=0` forces it off for tests). Validated on a test platform and the user's
  close-up view; contact shadows/SSAO, LOD1+ visual verification and RT shadows
  remain.
- GPU text layout for HUD, chat, notifications and diagnostics; a 60 s diagnostics overlay with
  frame-generation time, GPU counts, GPU memory and a rolling graph; frame limits (30/60/120/240/unlimited).
- Asynchronous asset uploads on a dedicated transfer queue (timeline semaphore), worker-side model GPU
  building/staging, budgeted texture uploads and coalesced instance-buffer updates, so large grids load
  without stalling the render thread.

Known rendering gaps (see the roadmap): reflection calibration against stock and probe quality on large, close
surfaces (shadowed faces still lean on the ambient floor). `DECAL_CUTOUT` paneling/decals are now rendered;
blended and projected decals are not. Still open: tree/foliage camera crash, voxel/planet atmosphere and foliage billboards; contact shadows/SSAO,
LOD1+ shadow verification, skinned/transparent casters, SSAO/GTAO,
additive glares, GPU particles, TAA/FSR 3, HDR output, video playback, Windows test pass.

## Requirements

- Space Engineers 1 (Steam) and [Pulsar](https://github.com/SpaceGT/Pulsar) 2.4.3 or newer.
- .NET 10 SDK to build the plugin.
- A Vulkan 1.3-capable GPU and driver. Software rasterizers are rejected.
- On Linux, Pulsar's LinuxCompat plugin supplies the SDL3 window.

## Build

```bash
dotnet build Vulkan.sln -c Debug
```

Compile Slang shaders after editing `Shaders/` and commit the generated bytecode:

```bash
dotnet run --project Tools/ShaderCompiler
# verify generated shader code is current:
dotnet run --project Tools/ShaderCompiler -- --check
```

## Run

Start Pulsar in development mode with the repository loaded as a source folder:

```bash
cd ~/.local/share/PulsarRelease
./Interim.bin -sources
```

The plugin configuration is in `~/.config/SpaceEngineers/Storage/Vulkan.cfg` (renderer choice,
validation, frame limit, VSync, diagnostics overlay, sun shadows and shadow distance, MSAA, reflection quality,
reflection probe resolution and live reflection blend/distance, SSR, supersampling, model detail and
lighting/exposure/bloom tuning). The in-game **Graphics > Vulkan Renderer** screen edits the
same settings; VSync/MSAA need a restart. Only monitor/output and resolution are shared with the vanilla renderer.

For GPU debugging, the plugin supports `VULKAN_VALIDATION=1` and
`VULKAN_VALIDATION_GPU=1` (GPU-assisted validation). If the Khronos layer is not installed system-wide,
download the `vulkan-validationlayers` package, extract it, and point
`VK_LAYER_PATH`/`LD_LIBRARY_PATH` at its `explicit_layer.d`/library directories. RADV fault reports can be
combined with `AMD_DEBUG=hang` and `RADV_DEBUG=zerovram`.

For automated visual checks, the renderer supports `VULKAN_AUTOLOAD=<seconds>`,
`VULKAN_SCREENSHOT=/path.png`, `VULKAN_SCREENSHOT_DELAY=<seconds>` and a long-duration test
notification via `VULKAN_NOTIFICATION=<text>` with optional `VULKAN_NOTIFICATION_MS` (default 60000).
Shadows: `VULKAN_SHADOWS=1` (`0` forces off for tests); debug views: `VULKAN_DEBUG_VIEW=6` (shadow factor), `7` (dynamic-light contribution), `8` (light count), `9` (raw cascade 0), `12` (raw probe cube as background), `13` (probe with the old z correction) or `14` (2x2 probe orientation comparison);
camera: `VULKAN_DEBUG_LOOKAT=<model|*>`, `VULKAN_DEBUG_VOXEL=1`, `VULKAN_DEBUG_DARK=1`, `VULKAN_DEBUG_TOP=1`,
`VULKAN_DEBUG_DISTANCE=<m>`; voxels are always on; diagnostics: `VULKAN_NO_VOXEL=1` safe mode,
`VULKAN_VOXEL_NO_DRAW=1` uploads without drawing, `VULKAN_VOXEL_PROBE=1` includes voxels in the probe; stock reference
capture: `VULKAN_STOCK_SCREENSHOT=/path.png`. GPU validation: `VULKAN_VALIDATION=1` and `VULKAN_VALIDATION_GPU=1`
(with `VK_LAYER_PATH`/`LD_LIBRARY_PATH` pointing at a locally extracted validation-layer package).
See the roadmap.

## Documentation

- [Docs/Roadmap.md](Docs/Roadmap.md) — current state, decisions, architecture, GPU layouts, build/test
  instructions and the session log. **Read this before changing renderer behavior.**
- The renderer consumes the stable render-message boundary; the decompiled game source and shipped
  shaders are reference material only and are not redistributed.

## License

See [LICENSE](LICENSE).
