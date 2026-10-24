# Vulkan Renderer — Roadmap

> **Living document.** Every session working on this project must read this file first and
> update it before finishing: tick completed items, adjust the plan, keep [Start here](#start-here)
> current, and add an entry to the [Session log](#session-log). If a decision changes, update the
> [Decisions](#decisions) table rather than leaving contradictory notes.

## Start here

**Current state (through 2026-10-24):** Phase 0 and Phase 1 done (menus, HUD, text, Vulkan settings dialog all
verified by the user; world load/save/reload works). Phase 2 first slice is implemented, builds clean and is
**verified in game by frame capture**: camera + environment state, actor registry, background MWM loader (LOD0),
texture kinds (model CM/Extensions sRGB, normal maps and alphamasks linear), forward opaque model pass with sun +
ambient, cube-grid instancing (2 instance buffers, 18 instances captured; astronaut, armor blocks, cockpit and grid
geometry all render, log clean), and an early placeholder ACES tonemap (superseded by the exposure/bloom path
below). The renderer now has IBL/eye adaptation, transparency/OIT, skinning, culling/LOD and the GPU-driven path;
the dynamic reflection probe now updates **one face per frame** with a current/target blend and a distance-based
close/far mip tier (see the newest 2026-10-24 entry); **voxel planets/asteroids are always enabled** and now use
stock ColorMetal/NormalGloss/Extensions textures through a triplanar material buffer, with GPU budget,
device-fault reporting and a crash-loop marker; **a tree-model camera path still reproduces a GPUVM page fault at
address 0 even with voxels disabled**, so it is the current blocker for tree/foliage work; sun shadows are opt-in
and validated below. **Monitor selection fixed and verified by the user across restarts** (2026-10-17): the adapter (game
"Video Adapter", an adapter+output pair) is now forwarded on adapter changes too, and the SDL window targets the
matching display so fullscreen lands on the selected monitor. The world correctness pass is also in: all MWM LODs
load (SquarePlate 2, Cockpit 4, astronaut 3, EdgeStraight 3; zero warnings), per-actor LOD selection with the
stock distance formula and 10% hysteresis, infinite far-plane frustum culling, `MaxViewDistance` culling, and
D3D-matching clockwise front-face culling (`FrontFace=Clockwise`, back culling). Verified in game: 47-59 draws
depending on view, 1 culled when looking away, solid blocks with no holes, clean log. **Static IBL is also in**:
the `BackgroundCube` skybox is drawn as the depth-less background, `BackgroundCubeIndirect.dds` (10 prefiltered
mips) provides diffuse irradiance and roughness-based sky reflections, and `ambient_brdf.dds` provides the
split-sum BRDF; the game's 0.02 probe ambient floor is kept as a minimum. Verified by capture (stars/nebula,
cockpit reflections, correct texture loading: IBL slot 1 with 10 mips, BRDF ready within 0.3 s). **Transparent
materials are implemented and verified** (cockpit windows are visible; the stock material's `ColorAdd.a`
contributes to transparency, which was the missing piece). **Character parenting is fixed** (`CreateManualCullObject`
/ `SetParentCullObject` hierarchy; the camera now moves the character) and per-material visibility flags work.
**GPU skinning is implemented**: the MWM skinned vertex buffer feeds a compute pass that deforms vertices with the
per-actor bone palette (skin transforms built from `SetCharacterSkeleton` + `SetCharacterTransforms`) into the
normal static vertex layout; the forward pass is unchanged. The critical missing link was **visibility output**:
the game only calls `Render.Draw()` (and thus sends `SetCharacterTransforms`) for IDs in
`MyRenderProxy.VisibleObjectsWrite`, so `ModelPass` now reports every visible actor with `SetVisibilityUpdates`.
The character appears and is correctly posed/animates; the skinning position bug (dropped bone translations from
row 3) is fixed. The bone palette is now also assembled on the GPU (`Scene/BonePalette.slang`); only the small
input arrays are uploaded. `MetalnessColorable` now controls stock's metalness-based coloring reduction. **Cockpit/
LCD texture changes** (`ChangeMaterialTexture` + generated offscreen textures +
per-material Visible flags) are implemented; need an inside-cockpit check. **GPU-driven opaque rendering is in**:
opaque non-skinned actors (including instanced cubes and static groups) are cached as **persistent part records**
(material, vertex/index addresses, index range) and **LOD ranges**; the per-frame upload is only actor world/colour/
bounds/visibility state. `Scene/Culling.slang` selects the LOD on the GPU with the stock distance formula and 10%
hysteresis (per-actor LOD state lives in a persistent buffer), frustum/view-distance-culls the actor, then appends
one indirect command + per-draw data per part into cull/no-cull buckets. The CPU issues two
`vkCmdDrawIndirectCount` calls instead of one draw per part (captured scene: 3152 cached parts, 13 total draw calls,
2 indirect). Instanced cube actors use the same buckets (the command carries the instance count and the draw data
carries the instance pointer/range). Skinned actors and transparent parts keep the direct/OIT paths. **OIT is implemented**: transparent parts render order-independently into RGBA16F accumulation +
R16F revealage targets (depth read-only), and the composite resolves them over the opaque HDR color; the CPU sort is
gone. **Dynamic point/spot lights are implemented**: `CreateRenderLight`/`UpdateRenderLight` feed the 64 nearest
camera-relative lights; block-local positions and directions are transformed through the parent cull object, and
the forward shader applies each light's color/intensity, range, falloff, gloss/diffuse factors, point offset and
spot cone. `AddRuntimeModel` (mod runtime meshes, material lookup by name, replacement alias), `UnloadTextures` and
`DeprioritizeTextures` are handled too. **Exposure now follows the game's postprocess settings**: the constant/eye-
adaptation formula from the shipped `CalculateExposure`, a sparse scene-luminance pass into a 1x1 exposure texture,
Hable tonemapping with the world's white point, and world `BloomEmissiveness` (120 in the test world) multiplying
emissive radiance, so battery/LED indicators stay bright in shadow. The test world's eye adaptation is enabled; a
user `ExposureScale` multiplier remains until full histogram adaptation and stock multi-scale bloom are implemented. **LCD apps are fixed and verified**: the Linux platform's
`GetMemoryBudgetForGeneratedTextures()` reports 0, so `MySessionComponentPanels` never counted any panel as visible
and every working screen stayed on the Online texture. A Harmony patch sets the session component's budget to
1 GiB; the game then creates the generated textures, renders sprites/text offscreen, and swaps `ScreenArea` to them
(verified: "This Is A Test" text and the cockpit script screens). Fonts now load synchronously and offscreen jobs
retry while a generated target is still being created. The persistent opaque part cache and GPU LOD selection are in
place; skinned actors and transparent parts keep the direct/OIT paths, and the per-frame actor state is still
uploaded from the CPU. **Automated tests** now work via
`VULKAN_AUTOLOAD=<seconds>`; `VULKAN_DEBUG_LOOKAT=<model substring>` (or `*` for the nearest non-character mesh)
frames the nearest matching actor for visual checks, with `VULKAN_DEBUG_DARK=1`, `VULKAN_DEBUG_TOP=1` and
`VULKAN_DEBUG_DISTANCE=<m>`; `VULKAN_STOCK_SCREENSHOT=<path>` captures the active renderer for stock references.
A first **HDR bloom pass** is in (half-resolution bright pass + separable blur +
composite add; `BloomScale` slider); matching stock's multi-scale prefilter/cascaded blur remains. A dynamic scene probe now exists
(**live** whole-cube capture every frame, 128-512², single-sample and no shadow sampling, with thruster/glow
billboards); the hard sun terminator on the dark side still leans on the ambient floor until the probe is
validated across more scenes.
Never run git; the user commits. **2026-10-22 additions:** deferred G-buffer2 reflections with screen-space ship
reflection (quality slider), stock Phong direct specular, restored stock normal mapping, 2x/4x/8x MSAA for opaque
geometry, OIT-aware bloom, ion glow cores, and independent plugin exposure/LOD/VSync settings. The screen-space
emission bounce was removed after it ghosted bright structure across the scene; it needs an emissive-only, blurred
source before it can return. See the newest session-log entry for details and remaining verification.

**Sun shadows: cascaded and LOD-stable (2026-10-21, quality-distance rework 2026-10-23).** Close-up feedback
(blocky edges, LOD pop, dither) replaced the single 100 m map with cascaded reverse-Z shadows. The current version
uses **four 4096² cascades** whose split ends and light-space extents are fitted to the camera frustum every frame.
The earlier fixed `1.35 * split` extents wasted more than half of a typical map, which made a 15-40 m view step onto
coarse texels and read as large blocks. The **Shadow distance** slider (50-1000 m, default 250 m) sets where shadows
stop; the near cascade stays at 15 m and the two middle cascades are capped (70/300 m) so increasing the slider does
not degrade close texels. A **separate GPU caster bucket uses LOD0 regardless of the forward LOD**, so a ship at LOD1+ keeps its
shadow silhouette. Sampling is a **fixed 16-tap Poisson PCF with bilinear depth** (4/3.2/2.4-texel radius by
cascade), slope-scaled bias and the **camera-oriented face normal** for offset/bias; per-pixel stochastic rotation
was removed because without TAA it read as static. Casters are alpha-tested (`shadowFragment`), the main shadow
pipeline back-face culls, and a no-cull variant handles two-sided parts. Matrices are texel-snapped.

The **diamond/line on individual red panels** was bad model vertex normals: across the bevel they point away from
the sun, so a large area read unlit. Diffuse, specular, IBL/ambient-specular and the shadow offset/bias now all use
the camera-oriented triangle face normal; the normal map supplies Toksvig gloss only. The old two-sided
geometric-normal flip is gone. An `albedo * 0.25 * AmbientScale` floor stands in for the missing dynamic probe and
makes the **Ambient** slider effective. The two-sided front/back choice uses the face normal, so grazing angles no
longer dither. Verified in `/tmp/vk_faceall.png`, `/tmp/vk_ambient25.png`, `/tmp/vk_shadowface.png`/`_off.png` and
the final cascade capture `/tmp/vk_wide_restored.png`.

Remaining: verify LOD1+ on the user's ship, contact shadows/SSAO for vertical junctions, near-cascade
resolution/quality slider, skinned/transparent casters; RT shadows later. Captures: `/tmp/vk_fixed.png` and
`/tmp/vk_fixed_shadows.png` (after the face-normal fix, shadows off/on), `/tmp/vk_facediffuse2.png` (same, with
ambient floor), `/tmp/vk_ambient_same.png` (ambient proof), `/tmp/vk_faceall.png` (face normal for all lighting),
`/tmp/vk_ambient25.png` (0.25 floor), `/tmp/vk_shadowface.png` / `_off.png` (face-normal shadow offset, shadows
on/off), `/tmp/vk_twosided_geom.png` (cascaded close-up after the two-sided fix),
`/tmp/vk_geomnormal_factor.png` (factor view), `/tmp/vk_lod60_factor.png` (60 m sun-side factor),
`/tmp/vk_lod60_dark.png` (60 m dark side), `/tmp/vk_test_shadow_on.png` / `_off.png` (on/off),
`/tmp/vk_shadow_side_sun3.png` (test platform), `/tmp/vk_wide.png` (first wide-extent capture) and
`/tmp/vk_wide_restored.png` (same camera after restoring the extents).

**Latest text/UI and diagnostics state (2026-10-18):** GPU text is live for HUD, chat, notifications and the
diagnostics overlay; the overlay has a rolling 60 s history, GPU triangle/part/cull counts, device-local GPU
memory with usage percent, GPU text counts and actual frame-generation time. `Config.FrameLimit` offers
`Unlimited`, `Fps30`, `Fps60`, `Fps120` and `Fps240`; the observed 120 fps is VSync/FIFO at the monitor refresh,
so higher/unlimited needs VSync off. Build and shader check are clean.

**Async asset uploads are in (2026-10-19).** GPU copies now run on a **dedicated transfer queue**
(RADV exposes family 1; the RX 7900 XTX uses it) with a timeline semaphore: the graphics submission
waits on the batch value, so the copy engine overlaps the previous frame and the CPU never blocks on
a transfer. Resources are created with concurrent sharing when the transfer family differs. Model GPU
building (vertex/skinned/index packing, buffer allocation and worker-side staging) runs on up to three
worker threads; the render thread only publishes the result and records the copies. Texture uploads
are time-budgeted (4 ms/frame), model publishing/startup 2 ms, instance buffers coalesce to one upload
per buffer per frame, and the persistent part cache waits for a quiet frame (rebuilds at most every
0.75 s, forced after 1.5 s of continuous changes). Per-texture `ChangeMaterialTexture` logging was
removed (it wrote hundreds of thousands of lines during a large paste). Verified in the user's world:
9,728 actors / 130,779 GPU parts / 72,032 instances render correctly with the large grid loaded,
no log errors, and the 60 s frame max dropped to ~50 ms (was a multi-second UI freeze). The remaining
~50-123 ms cost is the full part-cache rebuild; incremental append is the next optimization.

**Block paints and skins are verified (2026-10-18).** Space Engineers stores a block's paint as a
`ColorMaskHSV` **delta**, not a final colour; stock `ColorizeGray` adds
`TextureDebugMultipliers.ColorizeHSV = (0, 0.8, -0.1)` before converting HSV back to RGB. Our shader
had omitted that conversion, so the user's applied colours collapsed to grey. With the stock conversion
restored, the saved green world renders green (capture verified), and the user changed a battery to red
in game and confirmed it displays correctly. `ChangeMaterialTexture` and `UpdateRenderEntity` updates
that arrive before `CreateRenderEntity` are now held in pending state and applied at creation (skins
included); the CarbonFibre skin pattern is visible. Slider values are GPU-resident in
`GpuRenderSettings` (set 0 binding 3) and only uploaded when `Config.PropertyChanged` fires; slider tuning
has no per-frame CPU reads or transfers. Frame-limit and diagnostics visibility are polled per frame.

**Decals, seam and current focus (2026-10-18).** `DECAL_CUTOUT` parts (cockpit/battery `Paneling*`/`Decals`)
now render through the opaque alpha-masked path with stock `clip(alphamask - 0.5)` and the stock `+0.0001`
clip-space depth nudge (`MaterialFlags.Decal`); they are cached as normal `GpuScenePart`s, so the GPU-driven
path handles them too. Verified by automated capture in the user's world (`/tmp/vk_decals.png`): the battery
front's inset panel outline and screws, the respawn pod's white light strips/vents and the battery cylinder's
`DANGER HIGH VOLTAGE` decal + grille now appear; 9 actors / 162 GPU parts / 12 draws, no log errors. The
derivative-tangent path stays: the old centre seam is no longer visible on the battery front, though the
respawn pod front is still in shadow in the current camera, so a lit-front capture is still desirable.
The new stock reference is the user's `/tmp/ai-chat-custom-attachment-temp-file-3b729709-...png` (green
respawn pod + green battery); the in-game red battery is intentional and correct. Remaining visual gap versus
that reference: our unlit faces are much darker (no dynamic scene probe yet) and the provisional
`ExposureScale=0.5` / `Specular=1` / `Reflection=1` defaults still read brighter and harsher than stock.

**Two-sided per-block lighting fixed (2026-10-19).** The hangar door was fully direct-lit from the
shadow side because the MWM normal belongs to the authored front face, while no-back-face-cull actors also
draw the visible back face with that same normal. `Model.slang` now aligns the normal with the view vector
after the normal map (`if (dot(n, toCamera) < 0) n = -n`) before evaluating sun/ambient lobes. This is
per-visible-surface two-sided lighting, not a shadow pass: the side is chosen from the camera-oriented face normal, so
normal-map detail cannot flicker the front/back decision; correctly authored front faces are unchanged.
`FrontFace=Clockwise` is retained; a CCW experiment swapped every door face and was reverted. An IBL
diffuse-ambient experiment washed the whole grid out and was reverted; the stock `AmbientForwardPass` /
`AmbientMinClamp` floor (0.02) remains. Verified with the debug dark/sun-side cameras: direct-only hangar
door is black from the dark side and bright red from the sun side; player capture shows the expected lit
rims and dark visible faces. Frame ~2.9 ms CPU / 4.7 ms GPU during capture; build and shader check clean.
Temporary lighting diagnostics and the flip env flags were removed. No shadow work and no git operations.

**Immediate next steps:**
1. Calibrate exposure/specular/reflection against the stock reference with a lit camera (ideally on the
   respawn pod front), then set the defaults.
2. **Shadow quality follow-up**: verify the new LOD0 caster bucket at LOD1+ on the user's ship; tune near-cascade
   resolution/filter width; add contact shadows/SSAO for vertical junctions; then skinned/transparent casters and
   a quality slider. The cascaded pass is opt-in and documented above.
3. **Dynamic reflection probe follow-up**: validate one-face current/target blending and close/far mip quality at
   the user's 4K viewpoint; add GGX prefiltering if box mips are too soft.
4. Resume the larger roadmap items below.

**Next tasks, in order:**
1. **Lighting calibration** (above) against the stock reference, then validate the dynamic probe's contribution.
2. **Finish the GPU-driven scene path**: persistent opaque parts, GPU LOD selection (stock hysteresis) and
   static-group buckets are in. Remaining: make the part cache incremental (append new actors/ranges in place and
   grow the host-visible buffers instead of rebuilding all 130k parts; the rebuild is now the largest load hitch at
   ~50-123 ms), upload actor state only when it changes, move skinned actors and transparent parts into GPU buckets,
   and keep the direct path only for unsupported techniques. Goal: no per-frame actor/part walk.
3. **Voxel/tree blocker (highest priority)**: the planet surface with triplanar textures is stable, but framing a tree still causes a GPUVM page fault at address 0 even with voxels disabled. Reproduce with the tree debug camera, isolate the exact model/part/texture, then continue foliage/grass/atmosphere.
4. **G-buffer + motion vectors** (frame constants already carry the camera; add previous-frame matrices and the
   MRT pass) so TAA/FSR/GI can build on it.
5. Remaining Phase 2: static/merge groups, world sprites/particles; voxel ground textures are in (trees/foliage camera crash, atmosphere and voxel shadows open); remaining Phase 1 odds: options
   presets.
6. When flying away, watch the LOD histogram in the diagnostics overlay to confirm LOD switching.

**User preferences / rules (all sessions):**
- Never run git commands. The user commits and pushes.
- Do all work directly in this session: **no subagents and no task tool**.
- Never guess. Verify claims against the decompiled source, the shipped shaders or official docs; when a
  runtime behaviour is unknown, instrument it instead of assuming.
- If there is a problem to diagnose, add console logging (`RenderLog.Info/Warning/Error`, which prefixes
  `[Vulkan]`) and run an in-game test. After every test read the actual game log file (plus the Pulsar
  log when relevant) for errors and other messages, not only the console output.
- Keep this roadmap current; the user relies on it to continue across sessions without re-explaining.
- One code path for Windows and Linux; Linux tested first. Modernize, don't just match stock.
- Ask only when a decision is genuinely the user's; otherwise pick a sensible default and say so.

## How to build, run and test

**Machine (user's):** native Linux, Wayland, AMD RX 7900 XTX (RADV), .NET SDK 10, Steam at
`~/.local/share/Steam` (`~/.steam/steam` symlink exists). Game: `~/.local/share/Steam/steamapps/common/SpaceEngineers`.

| Task | Command / location |
|------|--------------------|
| Build plugin + tools | `dotnet build Vulkan.sln -c Debug` (must stay warning/error free) |
| Compile shaders | `dotnet run --project Tools/ShaderCompiler` after editing `Shaders/`; commit the generated `ClientPlugin/Render/Shaders/ShaderBytecode.g.cs`. `-- --check` verifies it is current. First run downloads pinned slangc into `Tools/ShaderCompiler/.slang/` (git-ignored). |
| Khronos validation (local package, no root) | `apt-get download vulkan-validationlayers`, then `dpkg-deb -x <deb> /tmp/vklayer`; run with `VK_LAYER_PATH=/tmp/vklayer/usr/share/vulkan/explicit_layer.d LD_LIBRARY_PATH=/tmp/vklayer/usr/lib/x86_64-linux-gnu VULKAN_VALIDATION=1 VULKAN_VALIDATION_GPU=1`. GPU-AV adds shader access checks; use `AMD_DEBUG=hang RADV_DEBUG=zerovram` for a RADV fault dump. |
| Vulkan smoke test (no game) | `dotnet run --project Tools/VulkanSmokeTest -- [--validation] [--seconds N] [--shadow]` — opens an SDL3 window, runs the plugin's device layer; `--shadow` creates the depth-only shadow pipeline |
| Shadow debug / validation | `VULKAN_SHADOWS=1` enables the opt-in shadow pass; `VULKAN_DEBUG_VIEW=6` shows the per-fragment shadow factor, `9` shows the raw shadow map. `VULKAN_DEBUG_LOOKAT=<model or *>`, `VULKAN_DEBUG_DARK=1`, `VULKAN_DEBUG_TOP=1`, `VULKAN_DEBUG_DISTANCE=<m>` position the debug camera. |
| DDS loader survey | `dotnet run --project Tools/VulkanSmokeTest -- --dds ~/.local/share/Steam/steamapps/common/SpaceEngineers/Content/Textures` (expect 1 failure: a PSD misnamed `.dds`) |
| Run the game | `cd ~/.local/share/PulsarRelease && ./Interim.bin -sources` (Pulsar **v2.4.3** official release). Steam launch option alternative: `/home/sentorx/.local/share/PulsarRelease/Interim.bin %command% -sources` with Proton disabled. Pulsar recompiles the plugin from `~/Development/Vulkan` when files change (debug build). |
| Frame capture | Run with `VULKAN_SCREENSHOT=/tmp/x.png VULKAN_SCREENSHOT_DELAY=25` (env vars); the renderer writes one PNG after the delay. View it with the Read tool. This is how to verify rendering without the user. |
| Automatic test | Add `VULKAN_AUTOLOAD=<seconds>` to press **Continue Game** on the main menu automatically (last session), then combine with the frame capture env vars. Fully headless of user input: no clicking needed. |
| Stock reference capture | `VULKAN_STOCK_SCREENSHOT=/tmp/stock.png` (+ optional `VULKAN_STOCK_SCREENSHOT_DELAY=<s>`) captures through the active renderer (DX11 or Vulkan) after auto-load, for side-by-side calibration. |
| Stop the game | `kill -TERM $(pgrep Interim)`. **Never `pkill -f <pattern>`** — it matches the invoking shell's own command line and kills the tool shell instead. |
| Logs | Game log (has `[Vulkan]` lines): newest `~/.config/SpaceEngineers/SpaceEngineers_*.log`; console: `~/.config/SpaceEngineers/Console_*.log` or redirect stdout. Pulsar log: `~/.local/share/PulsarRelease/Legacy/info.log` (plugin compile errors appear here). |
| Pulsar dev folder config | `~/.local/share/PulsarRelease/Legacy/Sources/sources.xml` (`<LocalPlugin>` Folder=repo, File=`Vulkan.xml`) and `Legacy/Profiles/Current.xml` (`<DevFolder><LocalFolderConfig><Id>7C5A605C-1041-4C94-988A-5DF95F3145F8</Id>`). Already set up. |
| Plugin config file | `~/.config/SpaceEngineers/Storage/Vulkan.cfg` (`Config.Renderer`, `Validation`; XML). Only exists once saved (config dialog, or the automatic switch to DirectX11 after a device creation failure); defaults apply otherwise (Renderer=Vulkan). |

Testing note: Space Engineers' **trash collector** deletes unowned/nearby-abandoned grids when the player flies
away (`Info: Trash collector removed grid ...` in the game log), including their physics. Disable trash removal in
the world settings (or make the grid a station/owned) when testing rendering at a distance; a disappearing grid is
not necessarily a renderer bug. Our renderer correctly drops actors/instance buffers on `RemoveRenderObject`.

Expected/benign log noise: `Failed to load plugin 'libdecor-gtk.so'`; on exit the shell prints `Killed` (LinuxCompat
ends the process with `Process.Kill()` in `MySandboxGame.ExitThreadSafe`; `DisposeDevice` does not run). The
validation layer is **not installed** on the user's machine (`vulkan-validationlayers` package), so `Validation` does nothing yet.

Old/unused: `~/.local/share/Pulsar` (July native dev bundle, Pulsar v2.3.0, different dev-folder format: Id = folder
name + `DataFile` in the profile) and its data in `~/.config/Pulsar`. Do not use.

## Goal

Replace Space Engineers 1's Direct3D 11 renderer with a native Vulkan renderer, delivered as a
Pulsar client plugin, that:

- runs from a **single code path on Windows and Linux** (tested on Linux first),
- **exceeds** stock visuals with modern techniques (global illumination, ray-traced effects,
  TAA, FSR upscaling, HDR output, volumetrics, …),
- lets players **scale every new feature down or off** from the in-game options menu, so
  weaker GPUs remain supported,
- stays **independent of game updates** as far as possible: we consume the stable
  `IMyRender` / render-message boundary instead of patching the D3D11 renderer.

## Decisions

| Topic | Decision |
|-------|----------|
| Runtime | Pulsar **Interim (.NET 10) only**, Windows and Linux. `net48`/Legacy dropped. `<Runtimes>CoreCLR</Runtimes>` in `Vulkan.xml` (Pulsar 2.4 IDs are `CLR`/`CoreCLR`/`Mono`; the template comment listing `NETCoreApp` is outdated and makes Pulsar skip the plugin **silently**). Target Pulsar **v2.4.3+**. |
| Vulkan | **Vulkan 1.3** required. Binding: **Vortice.Vulkan 3.3.0** (`<NuGetReferences>` in `Vulkan.xml` + `PackageReference` in csproj); uses the system loader. Required device features: dynamicRendering, synchronization2, timelineSemaphore, bufferDeviceAddress, descriptor indexing set, shaderDrawParameters, textureCompressionBC, samplerAnisotropy, VK_KHR_swapchain. Software rasterizers (llvmpipe) are rejected. |
| Code paths | One path. **No DXVK interop**, no D3D11 hybrid. |
| Shaders | Written by us in **Slang** (`Shaders/*.slang`, modules + `import`, generics/interfaces instead of define permutations, real pointers for buffer device addresses). Compiled offline by `Tools/ShaderCompiler` with the pinned official `slangc` (v2026.20) into one SPIR-V module per file (all entry points), committed as generated `ReadOnlySpan<uint>` data plus entry-point name constants in `ShaderBytecode.g.cs`. Keen's shaders are an algorithm reference only, never copied or redistributed. (DXC/HLSL was tried first; switched to Slang 2026-10-17 at the user's request for cleaner, more modern code. Vortice.Dxc is broken on Linux: UTF-16 vs 4-byte wchar_t.) |
| Upscaling | **AMD FSR 3** (MIT). No DLSS/XeSS (no NVIDIA licenses). FSR 4 is not open/MIT. |
| Linux window | Provided by the **LinuxCompat** plugin (SDL3), a Pulsar core plugin on Linux. **Detected at runtime** (reflection), not a `<DependencyIds>` entry (that would force it on Windows players too). |
| Hook timing | `IPlugin.Init` is too late (device exists by then). Harmony patches in category `VulkanEarly` are applied from `Preloader.Finish()`, which Pulsar runs before `Game.StartSpaceEngineers` (verified in Pulsar source and in game). |
| Safety | Renderer choice is a config option (restart required). If the Vulkan probe fails, the stock D3D11 renderer starts. If device creation fails later, the config is switched to DirectX11 and a `MyRenderException` explains to restart. |
| Settings UI | New features get options in the **in-game Graphics options screen**, with presets and per-feature overrides; unsupported features are greyed out with a reason. |
| Native interop | `NativeLibrary.Load`/`GetExport` + `delegate* unmanaged` function pointers. **No `[LibraryImport]`** and no other source generators (`[GeneratedRegex]`, JSON source gen, …): Pulsar compiles plugins with plain Roslyn (`CSharpCompilation.Create` + `Emit`) and never runs generators. Avoid `[DllImport]` too. Unsafe code is allowed. A local SDK build passing does **not** prove Pulsar can compile it. |
| Publicizer | Krafs.Publicizer for `VRage.Platform.Windows` and `VRage.Render` (csproj `<Publicize>` items **and** `[assembly: IgnoresAccessChecksTo]` in `ClientPlugin/Tools/GameAssembliesToPublicize.cs` — keep both in sync; Pulsar reads the attributes). `IgnoresAccessChecksToAttribute.cs` defines the attribute for Pulsar builds (`!LOCAL_BUILD`). |
| Root namespace | `ClientPlugin` (template default). LinuxCompat also uses `ClientPlugin.*`; no clash (different assemblies), but look its types up by full name. |
| Monitor selection | The game's "Video Adapter" is an adapter+output pair (`MyAdapterInfo.OutputName`/`DesktopBounds`); SE stores its index (`VideoAdapter`). We forward `AdapterOrdinal` changes (not only window-mode changes) and select the SDL display whose bounds/name matches, via `SDL_SetWindowPosition(SDL_WINDOWPOS_CENTERED_DISPLAY(id), …)`: SDL records it as the pending fullscreen display even on Wayland, where moving a toplevel is refused. Done on LinuxCompat's SDL thread through reflection on `SdlRenderThread.Dispatch`. |
| Winding / culling | D3D's default front face is **clockwise** (`FrontCounterClockwise=false`). Our projection negates `M22` for Vulkan, which cancels Vulkan's opposite framebuffer Y direction, so final screen-space winding stays clockwise: model pipelines use `FrontFace.Clockwise` + `CullMode.Back` (or `None` for `NoBackFaceCulling`). Pipelines that do not follow this will show holes/inside-out geometry. |
| Image-based lighting | Static pass: `BackgroundCube.dds` is the depth-less background; `BackgroundCubeIndirect.dds` (10 prefiltered mips) gives diffuse irradiance (last mip) and roughness-based reflections; `ambient_brdf.dds` (R16G16F, linear) gives the split-sum BRDF. A camera-centred `ProbePass` captures the scene into a configurable 128²/256²/512² cubemap (restart required) and replaces the static cube for reflections. Cubemaps use their own bindless array (set 0, binding 2); sampling follows stock `ApplySkyboxOrientation`. The probe now renders **one face per frame** into a target cube; the target is copied from the completed cube at the start of each cycle, and `Environment.slang` blends current/target over a live-configurable time window (default 0.25 s), so reflections do not step. The completed cube is used directly between cycles. `Reflection close/far distance`, `far blur` and `minimum blur` select a smooth receiver-distance mip tier from the same live scene cube (no second scene pass and no sky-only far cube). The probe renders single-sample (no MSAA), with `ShadowEnabled = 0`; faces use the **plain stock projection** (no D3D→Vulkan flips). Captures use the **static skybox IBL** to avoid feedback. Thruster/glow billboards are included; voxels are **not** drawn into the probe by default (`VULKAN_VOXEL_PROBE=1` enables the old 3 km experiment). SSR is now **off by default** (optional metal/high-gloss sharpening); GGX prefiltering remains later work. A simple 1x1 exposure pass and Hable tonemapping are in place. |
| Voxels / planets | Clipmap renderer in `VoxelPass`/`Voxel.slang`, **always enabled** (no user option): the game's `IMyLodController` supplies cells and the stock `MyMeshes` render-data provider; cell meshes are interleaved into a 24 B position+material/normal stream, uploaded to **device-local** buffers through `UploadQueue`, and drawn with standard vertex input + `DrawIndexed`. A persistent 256-entry material buffer holds stock ColorMetal/NormalGloss/Extensions bindless slots plus tiling and Far3Color; the fragment shader does stock-style triplanar sampling and uses gloss/AO. Safety: 256 MB device-local budget, per-cell validity/index/size checks, 8192 draw cap, 20 km main / 3 km probe cell distance, no probe voxels by default, `VULKAN_NO_VOXEL` diagnostic safe mode, `VK_EXT_device_fault` fault-address logging, and a crash-loop marker (written when drawing starts, cleared after 30 s stable; only logs now, does not disable voxels). Planet surface is verified with textures; **tree/foliage camera path still causes a GPUVM page fault at address 0 even with voxels disabled**. Atmosphere, foliage billboards/grass, voxel shadows and full tree/foliage material support remain. |
| Transparent models | Glass/holo/shield parts look up `VRageRender.MyTransparentMaterials` by material name (fallback `GlassCCW`), use the stock material color (linear), color add, light multiplier, reflectivity/fresnel/gloss and texture/gloss texture. **Weighted-blended OIT**: a second pass writes RGBA16F accumulation (`color * alpha * weight`, additive) and R16F revealage (`dst *= 1 - alpha` via `OneMinusSrcColor`) with depth test read-only/no depth write, per-material `TriangleFaceCulling`; composite resolves `accum.rgb/accum.a` over the scene with `1 - reveal`. No CPU sort. |
| Skinning | **All GPU**: the skinned MWM buffer (64 B/vertex with bone indices/weights) is deformed by a compute pass; a second compute pass (`BonePalette.slang`) assembles the model-bone palette (bind-inverse × absolute) on the GPU. The CPU only uploads the skeleton skin transforms, the per-frame absolute transforms and the model→skeleton index map. Output is the normal 32 B static vertex layout, so the forward pass is unchanged. The game calls `Render.Draw` only for IDs reported in `MyRenderProxy.VisibleObjectsWrite`, so visible IDs of actors with `SetVisibilityUpdates` are published each frame; without this the game never sends `SetCharacterTransforms`. |
| Material coloring | `ColorMaskHSV` is a **delta from the grey/magenta paint base**, not a final colour. Stock `ColorizeGray` adds `TextureDebugMultipliers.ColorizeHSV` (default `(0, 0.8, -0.1)`) after zeroing the texture's HSV; we do the same. `RemoveMetalnessFromColoring` is applied unless the effective material flags include `RenderFlags.MetalnessColorable` (asset-modifier skins). Pre-create colour/skin messages are kept on a pending actor and applied at creation. |
| Normal mapping | Opaque normal maps use the stock **derivative** tangent basis (`Geometry/PixelUtils.hlsli NormalBuildTangent` + `PixelTangentSpace`) ported to `Model.slang`, with `adjust_normalmap_no_precomputed_tangents` (`normalMap.x *= -1`) and stock `ToksvigGloss`. This deliberately ignores the per-vertex tangent/`tangent.w` path (`Normal()`), because the cockpit's mirrored UV halves produced a hard centre seam with it. The derivative path is verified clean on the battery front; the respawn pod front still needs a lit capture. |
| Opaque lighting | Direct sun uses a GGX specular + Lambert diffuse model with stock `SurfaceAlbedo = baseColor * (1 - metalness)` and `SurfaceF0 = lerp(0.04, baseColor, metalness)`; ambient is the game's probe floor times the same albedo; IBL uses the prefiltered skybox + split-sum BRDF. Stock's Forward pass is Lambert-only, but the reference screenshot comes from the deferred PBR path, so specular/reflection are enabled by default (`SpecularScale=1`, `ReflectionScale=1`). Exposure default is provisionally `0.5`; the current captures are too bright versus the stock reference and still need calibration. |
| Static decals | `DECAL_CUTOUT` parts (cockpit/battery `Paneling*`, `Decals`, etc.) render as alpha-masked opaque geometry through the same `Model.slang` pipeline: `clip(alphamask - 0.5)`, `MaterialFlags.AlphaMasked | Decal`, and the stock `+0.0001` clip-space depth nudge. They use the derivative normal path and are part of the persistent GPU-driven part cache. Blended (`DECAL`) and projected screen-space decals (`Decals.hlsl`) are still not implemented. |
| Shadows | Four 4096² reverse-Z sun cascades (default splits 15/55/125/250 m, light 600 units away), opt-in via **Graphics > Vulkan Renderer > Shadows** or `VULKAN_SHADOWS=1` (restart required). **Shadow distance** slider (50-1000 m, default 250) controls where shadows end; split ends and light-space extents are fitted to the camera frustum each frame, with the near cascade fixed at 15 m and the middle two capped so 250 m quality does not collapse when the slider is raised. A separate LOD0 GPU caster bucket prevents shadows from disappearing when the forward pass switches to LOD1+; casters are camera-culled opaque parts with back-face culling for cull-enabled actors and an alpha-tested `shadowFragment`. Sampling is fixed 16-tap Poisson PCF with bilinear depth (4/3.2/2.6/2.2-texel radius), texel-snapped cascade centres, face-normal offset and slope-scaled bias. Still open: LOD1+ visual verification, contact shadows/SSAO, dark-side validation with the dynamic probe, skinned/transparent casters; RT shadows are Phase 6. The two-sided side decision uses the camera-oriented face normal, not the normal-mapped normal. |
| User settings | All lighting/exposure/bloom sliders live in a persistent one-element `GpuRenderSettings` storage buffer (set 0 binding 3) once per device, not in per-frame constants. `Config.PropertyChanged` only sets a dirty flag; the render thread uploads the 48-byte block when it changes. Shaders read `renderSettings[0]` (sun/ambient/specular/metalness, emissive/lights/reflection/screen, exposure/bloom). Slider tuning has no per-frame CPU reads or transfers; the frame limit and diagnostics visibility are polled per frame. Defaults: Exposure 0.5, Specular 1, Reflections 1, Sun/Ambient/Metalness/Emissive/Lights 1, Screen 4, Bloom 0.2 (provisional until calibrated against the stock screenshot). **SSR** is off by default; **Reflection close/far distance**, **far blur**, **minimum blur** and **update blend** are live; Voxels are always enabled; there is no user toggle. **Light distance** (150-1000 m) and **Shadow distance** (50-1000 m, default 250) are separate render-thread settings; the former controls dynamic-light culling and the latter controls the sun cascades. Optional **Supersampling** (1.0-2.0, default 1.0, under Advanced) sizes the scene targets while the composite resolves them to the backbuffer. FXAA follows the game's Graphics > Anti-aliasing setting (None/FXAA), with a **Low/Medium/High/Very high** quality slider; Medium matches stock preset 29, High uses preset 39 and Very high extends it with more search steps and lower edge thresholds. |
| Uploads | Dedicated transfer queue + timeline semaphore when the device exposes a transfer family different from graphics (RADV: family 1); resources use concurrent sharing so copies need no ownership transfer. The graphics submission waits on the batch timeline value (`FrameLoop.EndFrame`), so the copy engine overlaps the previous frame and the CPU never spins. No separate queue: copies fall back into the graphics command buffer. Model GPU building and staging run on workers (max 3); textures are finished on the render thread under a 4 ms/frame budget. |
| GPU buffers | No UBOs by design: per-frame/per-draw constants go through push constants plus host-visible transient buffers read via buffer device addresses (the modern SSBO style). Persistent device-local buffers hold model vertices/indices, instance data, textures and the skinned streams; a small host-visible `GpuRenderSettings` storage buffer (set 0 binding 3) holds user tuning and is written only on changes. Compute writes/reads (skinning, palette, culling) use the same BDA path. Indirect buffers are host-visible storage buffers with `Indirect` usage. Storage images/RW buffers and acceleration structures are still reserved for OIT, GI/RT. |
| Bloom | First version: half-resolution HDR bright pass (threshold 1.0, soft knee), separable 9-tap Gaussian blur, added in `Composite.slang` with a user `BloomScale` (default 0.2). Stock's multi-scale prefilter/cascaded blur and exposure-aware strength are not matched yet. |
| GPU-driven opaque draws | Opaque, non-skinned actors (including instanced cubes and **static groups**) use a **persistent** `GpuScenePart` buffer (material + vertex/index BDAs + index range, built on actor/model/material changes), persistent per-LOD ranges, and a persistent per-actor LOD-state buffer. Per frame the CPU uploads only actor world/colour/bounds/instance state; `Scene/Culling.slang` runs one thread per actor, selects the LOD with the stock distance formula + 10% hysteresis, sphere-tests 5 normalized planes + `MaxViewDistance`, then appends one command + 184-byte draw data per part to a cull/no-cull bucket: `[counter u32][commands @64, 16 B]` followed by draw data. Static groups are expanded into one actor record per local matrix sharing the same ranges. The indirect command is **non-indexed** (`vertexCount=indexCount`); the vertex shader fetches the model index from the draw data's index buffer BDA and then the vertex. `SV_DrawIndex` selects the draw data; the fragment gets the same material through a flat draw-index varying. `push.drawData == 0` selects the direct push path, so one `Model.slang`/pipeline serves both. |
| Per-actor GPU state | Opaque actor state is cached as **absolute** `GpuSceneActor` records (transposed world, bounding sphere, origin, LOD distances, instance address/range, flags/visible) built once per part-cache rebuild and updated only for dirty actors. `SceneActors` tracks dirty actors (world/visibility/instance range/local AABB), marks a parent's subtree on transforms, caches resolved world matrices per `transformRevision`, and keeps small `Skinned`/`VisibilityUpdates` lists. Per frame the render thread copies the record array to transient GPU memory (camera movement costs nothing) and `Scene/Culling.slang` subtracts the camera position for distance/frustum/draw data. This removed the previous per-frame loop over all ~9.6k actors (prepare 5.4 ms -> 0.2-1.0 ms; frame ~8.9 ms -> ~1.8-2.9 ms). |
| Asset preload | `PreloadTextures`/`PreloadModel(s)`/`PreloadMaterials` start background decodes/parses immediately (mapping TextureType to the sampling kind), so GPU buffers/images are created during the loading screen rather than on first draw. Model texture dependencies also start when each model is parsed. `UnloadTextures` frees matching entries/slots (in-flight decodes are discarded) and `DeprioritizeTextures` is a no-op (no streaming priorities). `AddRuntimeModel` builds a one-LOD GPU mesh from `MyModelData`, resolves section material names through the model-material table, and registers the name (plus a `ReplacedModel` alias). |
| Exposure / emissive | `UpdatePostprocessSettings` is cached. `Post/Exposure.slang` samples the HDR scene on a sparse grid, averages log-luminance, applies the shipped `CalculateExposure` (`LuminanceKeyValueCendos`) and writes a 1x1 R32F exposure texture; the composite samples it and applies Hable with the world's `WhitePoint`. The 0.025 calibration was removed (it made the world ~40x too dark); a user `ExposureScale` multiplier is applied and clamped to [0.02, 4]. Current default is 0.5, which is still too bright on the cockpit capture. Emissive radiance is `albedo * extras.y * Emissivity * BloomEmissiveness` (albedo already includes `1-metalness`); the test world uses `BloomEmissiveness=120`, which is what makes battery LEDs read in shadow. Full histogram eye adaptation, temporal smoothing and stock multi-scale bloom are still missing. |
| LCD budget | Linux's platform renderer reports no generated-texture memory budget. `LcdMemoryBudgetPatch` sets `MySessionComponentPanels.m_memoryBudget` to 1 GiB after init; otherwise the panel manager's visible counter never reaches its threshold, `IsInRange` stays false, and every powered LCD keeps the default "Online" texture. |
| Dynamic lights | `CreateRenderLight`/`UpdateRenderLight` are cached per ID. Light positions, point offsets and spot directions are **block-local**, so `SetParentCullObject` supplies the parent cull object and the parent world matrix is applied before upload; this is the stock owner-world transform. Each frame the 64 nearest point/spot lights (by camera distance) are uploaded camera-relative (`GpuLight`, 64 B) through the transient allocator; `FrameConstants` carries the pointer/count. Per-light color (intensity already multiplied), range, falloff, gloss factor, diffuse factor and spot aperture are honored; the stock hard/diffuse attenuation and hard spot clip are applied, with the gloss factor feeding roughness and material AO. Never populated/zero-intensity lights contribute nothing. Shadows, glare/flares and stock tiled light culling are not implemented. |
| Text / UI | **GPU text is implemented for the main target**: font atlas pages stay in the bindless array; a GPU font buffer holds codepoint→glyph metrics (cmap, advances, page/rect, kerning) uploaded once at font creation. `TextLayout.slang` shapes/lays out text from strings + style parameters into glyph `SpriteInstance` records (newlines, alignment, kerning; wrapping is not yet in the shader) and the sprite pipeline draws them. The CPU uploads run descriptions and strings each frame (per-frame caching is not implemented yet). HUD, chat, notifications and the diagnostics overlay all use this path; the diagnostics panel/graph rectangles and 60 s history sampling are CPU-side, its text is GPU. Offscreen LCD text keeps the CPU `RenderFont.DrawString` fallback; complex-script shaping is out of scope. |
| Git | The user does all git operations. Sessions never run git commands. |

## Our code map

| File | Purpose |
|------|---------|
| `ClientPlugin/Preloader.cs` | Global-namespace `Preloader` (Pulsar finds it by name). `Finish()` applies the `VulkanEarly` Harmony category. No Cecil patches. |
| `ClientPlugin/Plugin.cs` | `IPlugin`; `PatchAllUncategorized` only (renderer patches are early). Config dialog. |
| `ClientPlugin/Config.cs` | `RendererKind Renderer` (Vulkan/DirectX11), `bool Validation`, read-only `[Info]` status properties (getter-only, so `XmlSerializer` skips them). Template `SettingsGenerator` UI. |
| `ClientPlugin/Gui/GraphicsOptionsPatch.cs` | Postfix on `MyGuiScreenOptionsGraphics.RecreateControls`: "Vulkan Renderer" button (found by control names `GraphicsPreset` / `EnableDamageEffects`). |
| `ClientPlugin/Testing/AutoLoadMainMenu.cs` | Harmony postfix on the main menu input: `VULKAN_AUTOLOAD=<seconds>` presses Continue Game for automated tests. |
| `ClientPlugin/Settings/Elements/Info.cs` | Read-only label/value settings element. |
| `ClientPlugin/Settings/Elements/Button.cs` | `ButtonAttribute` for action/method settings entries; the slider Reset buttons are created inline in `Slider.cs`, not by this attribute. |
| `ClientPlugin/Settings/Elements/Slider.cs` | Float/int slider with four-decimal float labels and a per-slider Reset button (measured `RectangularBorderLess` button, default from `Config.Default`). |
| `Render/RendererStatus.cs` | Which renderer runs, fallback reason, selected GPU capabilities (for the settings dialog). |
| `Render/RenderProxyInitializePatch.cs` | Prefix on `MyRenderProxy.Initialize(ref IMyRender)`: swaps `MyDX11Render` for `VulkanRender` if selected and the probe passes. |
| `Render/VulkanRender.cs` | `IMyRender` implementation: device lifetime, settings/resize, per-frame flow, frame limiter (`FrameLimit`), diagnostics stats, message switch (non-GUI messages). |
| `Render/MessageQueueProcessor.cs` | Port of stock frame semantics (pre-frames, Draw/DebugDraw only when drawing, StateChangeOnce once, dispose non-persistent). |
| `Render/FrameCapture.cs` | `VULKAN_SCREENSHOT` debug PNG capture. |
| `Render/RenderLog.cs` | `[Vulkan]`-prefixed lines in the game log. |
| `Render/Platform/GameWindowBridge.cs` | Win32 vs LinuxCompat SDL3: window handle (`MyWindowsWindows.WindowHandle`), surface creation, window-mode forwarding. |
| `Render/Platform/Sdl3.cs`, `Kernel32.cs` | Function-pointer interop. |
| `Render/Vulkan/VulkanInstance.cs` | Loader init, instance, validation messenger, device enumeration + `DeviceCapabilities`. |
| `Render/Vulkan/VulkanDevice.cs` | Physical device pick (matches game adapter by PCI IDs), logical device, queue + `QueueLock`. |
| `Render/Vulkan/VulkanSwapchain.cs` | Swapchain (sRGB BGRA/RGBA, FIFO for vsync else mailbox/immediate), per-image present semaphores. |
| `Render/Vulkan/FrameLoop.cs` | 2 frames in flight: fences, acquire, submit (`vkQueueSubmit2`), present, image barriers helper. |
| `Render/Vulkan/GpuContext.cs` | Device + memory + bindless + uploads + **deferred destruction per frame slot** (`Release`). |
| `Render/Vulkan/GpuMemory.cs` | Block sub-allocator (64 MB blocks, dedicated ≥ 32 MB, buffers and images in separate blocks, host-visible blocks persistently mapped, every block has DEVICE_ADDRESS flag) and device-local allocated/total tracking for diagnostics. |
| `Render/Vulkan/GpuBuffer.cs`, `GpuImage.cs` | Resources; `Dispose` defers destruction through `GpuContext.Release`. |
| `Render/Vulkan/UploadQueue.cs` | Per-frame 32 MB staging rings for same-frame uploads (generated textures); copies recorded at the start of the graphics command buffer. Render thread only. |
| `Render/Vulkan/TransferSystem.cs` | Asynchronous asset uploads: dedicated transfer queue + timeline semaphore, worker-side staging (`PrepareBufferUpload`), copy/barrier recording and one submit per frame; falls back to recording into the graphics command buffer when there is no separate queue. |
| `Render/Vulkan/BindlessDescriptors.cs` | Set 0 (binding 0: up to 65536 sampled images, binding 1: 4 immutable samplers, binding 2: 16 cubemaps with a black default, binding 3: persistent `GpuRenderSettings` storage buffer), the shared pipeline layout (128 B push constants, all stages), fixed texture slots White/Black/Missing/Transparent. |
| `Render/Vulkan/MipGenerator.cs` | Blit-chain mip generation (uploads with `generateMips`, offscreen targets). |
| `Render/Vulkan/GraphicsPipelineBuilder.cs` | Init-property description → graphics pipeline (dynamic rendering, shared bindless layout, dynamic viewport/scissor, no vertex input, reverse-Z depth compare by default). Use it for every new pipeline. |
| `Render/Vulkan/TransientAllocator.cs` | `GpuContext.Transient`: per-frame linear host-visible scratch with device addresses (frame constants, per-draw data, instances). Reset in `GpuContext.BeginFrame`. |
| `Render/Scene/SceneTargets.cs` | HDR color (R16G16B16A16_SFLOAT) + depth (D32_SFLOAT, reverse Z, cleared to 0) at the game resolution, bindless slots; recreated on resize. |
| `Render/Scene/SkyboxPass.cs` | Depth-less fullscreen background sampling the environment cubemap along the view ray. |
| `Render/Scene/ShadowMap.cs` | Four 4096² reverse-Z sun cascades, quality-distance split schedule, per-frame frustum-fitted extents, texel-snapped light matrices and bindless slots. |
| `Render/Scene/CameraState.cs` | Camera matrices incl. the stock unflipped culling matrix, `FrameConstants` (view-projection, inverse for rays, skybox orientation, sun, ambient, IBL slots/params, paint conversion `ColorizeHsv`), LOD coefficient. |
| `Render/Scene/GpuRenderSettings.cs` | Persistent one-element setting block: sun/ambient/specular/metalness, emissive/lights/reflection/screen, exposure/bloom. Uploaded only when `Config` changes. |
| `Render/Scene/SceneActors.cs` | Render-object registry: create/update/remove, visibility, model change, per-material overrides/flags, `ChangeMaterialTexture` changes, static groups, manual cull-object hierarchy (parent->children map), instance ranges/bounds, max view distance, per-actor LOD; `Revision` drives persistent part-cache rebuilds, `DirtyActors` drives per-actor GPU state refresh, cached world matrices use `transformRevision`, and `Skinned`/`VisibilityUpdates` avoid full scans. Pending pre-create colour/skin state is applied at creation. |
| `Render/Scene/Frustum.cs` | Five-plane infinite-far-plane culling from the stock view-projection. |
| `Render/Scene/VoxelPass.cs` | Voxel/planet renderer: binds the game's `IMyLodController` through an `IMyVoxelActor` adapter, receives stock `MyVoxelRenderCellData`, uploads device-local vertex/index buffers, loads stock voxel material textures into a persistent material buffer, draws with standard vertex input, and enforces the byte/draw/distance budget. Also owns the crash-loop marker and diagnostic safe mode. |
| `Shaders/Scene/Voxel.slang` | Voxel vertex/fragment shader: 24 B vertex input, camera-relative row-vector transform, stock triplanar ColorMetal/NormalGloss/Extensions sampling, Lambert sun + forward ambient, and opaque G-buffer/F0 outputs. |
| `Render/Scene/ModelPass.cs` | Forward opaque MWM pass: persistent part cache + GPU LOD/culling for opaque non-skinned actors (incl. static groups/cubes), direct path for skinned/transparent, cull-back/no-cull pipelines, bindless materials, IBL + sun shading; GPU skinning compute pre-pass; the sun shadow pass (`RecordShadow`, soft PCF, alpha-tested casters, opt-in); visible-object reporting (`VisibleObjectsWrite`). Per-actor GPU records are built once per cache rebuild, refreshed only for dirty actors/instance-buffer changes, and copied to transient memory per frame; the culling shader subtracts the camera. The full part-cache rebuild waits for a quiet frame (max once per 0.75 s, forced after 1.5 s). |
| `Render/Scene/GpuSceneDraws.cs` | Wire-format structs for persistent parts/LOD ranges/actor state, the cull compute dispatch, and the two indirect cull/no-cull buckets. |
| `Render/Scene/BloomPass.cs` | Half-resolution HDR bloom: bright-pass, separable blur targets, resize/slot lifetime. |
| `Render/Scene/InstanceBuffers.cs` | GPU instance buffers for cube/generic ranges. Updates are coalesced: the newest message per buffer is kept (`AddRef`) and uploaded once per frame through `TransferSystem`, replacing the old buffer for frames-in-flight safety. |
| `Render/Models/MwmLoader.cs`, `MwmMeshData.cs` | Background MWM parsing through `MyModelImporter`, all LODs, stock vertex decodes, texture path resolution. |
| `Render/Models/ModelCache.cs` | Model path → parsed LOD data → device-local GPU buffers, parts and texture entries. Parsing and GPU building/staging run on workers (max 3); the render thread publishes prepared models and submits their transfer uploads. `Version` bumps on publish so `ModelPass` rebuilds its part cache. |
| `Render/Scene/CompositePass.cs` | Fullscreen triangle: HDR scene + OIT resolve → ACES/Hable backbuffer using the exposure texture. |
| `Render/Vulkan/ShaderModule.cs`, `VkNames.cs`, `DeviceCapabilities.cs` | Helpers. |
| `Render/Textures/DdsLoader.cs`, `TextureData.cs` | DDS → upload layout (DX10, FourCC, masks, palette; conversions to RGBA8 where Vulkan has no format). |
| `Render/Textures/TextureCache.cs` | Name → texture; async decode, render-thread finish/upload, sRGB + GUI premultiply like stock, size/loaded queries. |
| `Render/Sprites/GuiRenderer.cs` | GUI messages (sprites, text, scissor, fonts, viewport scale) for the main target; records the GUI pass. |
| `Render/Sprites/SpriteBatcher.cs`, `SpriteRenderer.cs`, `SpriteInstance.cs` | CPU batching (stock geometry rules) and GPU drawing (instances by device address, bindless textures). |
| `Render/Sprites/RenderFont.cs` | Port of stock `MyRenderFont` text layout on the game's `MyFont`; CPU path used by offscreen LCD text and as the diagnostics fallback. |
| `Render/Sprites/GpuTextRenderer.cs` | GPU text: font-table upload, per-frame run/string upload, `TextLayout` compute dispatch, glyph-instance draw through `SpriteRenderer`. |
| `Render/Sprites/DiagnosticsOverlay.cs` | 60 s diagnostics panel: rolling timestamped 0.5 s buckets, GPU/frame/memory/text stats, GPU text lines, CPU panel/graph rectangles. |
| `Render/Scene/GpuDiagnostics.cs` | `GpuDiagnosticsCounters` shared with `Scene/Culling.slang`. |
| `Testing/TestNotification.cs` | `VULKAN_NOTIFICATION` long-duration notification for automated visual checks. |
| `Testing/StockScreenshot.cs` | `VULKAN_STOCK_SCREENSHOT` reference capture through the active renderer, scheduled after the automated Continue Game. |
| `Render/Shaders/ShaderBytecode.g.cs` | Generated SPIR-V. Do not edit. |
| `Shaders/` | Slang sources: `Common/Bindless.slang` (2D + cube arrays + `RenderSettings` storage buffer, must match `BindlessDescriptors`), `Common/Color.slang`, `Common/Environment.slang` (shared `FrameConstants` + skybox orientation/IBL lookups), `Sprites/Sprite.slang`, `Sprites/TextLayout.slang`, `Post/Composite.slang` (includes the raw-shadow-map debug view), `Scene/Model.slang` (`vertexMain`/`fragmentMain` plus `shadowMain`/`shadowFragment`), `Scene/Skybox.slang`, `Scene/Transparent.slang`, `Post/Bloom.slang`, `Scene/Culling.slang` (compute), `Scene/Skinning.slang` (compute), `Scene/BonePalette.slang` (compute), `Common/SkinningCommon.slang` (shared layouts); `shaders.json` manifest. |
| `Tools/ShaderCompiler`, `Tools/VulkanSmokeTest` | Dev tools (not in `<SourceDirectories>`, never shipped). |

## Architecture

### Startup / hook
- Stock flow: `MyProgram.InitializeRender` (SpaceEngineers.exe, `MyProgram.cs:189-225`) creates
  `MyDX11Render` → `MyRenderProxy.Initialize(render)` (`VRage.Render/VRageRender/MyRenderProxy.cs:404`).
  `MyInitializer.InvokeBeforeRun` (file system, log) has already run, so the config can be loaded in the hook.
- We prefix `MyRenderProxy.Initialize` and replace the instance (MyDX11Render's constructor only stores settings).
- `VulkanRender.TryCreate` only probes (throwaway instance, device requirements); the real instance and device are
  created in `CreateDevice` on the render thread, after LinuxCompat created the window.
- Our `MessageProcessingSupported` must be `true` (else the proxy drops all messages).

### Render thread (reused from the game)
- `VRage.Render/VRageRender.ExternalApp/MyRenderThread.cs` drives us: `CreateDevice` → loop
  { `ApplySettingsChanges` → `BeforeRender` → `Draw(bool)` → `AfterRender` → `Present` } → `DisposeDevice`.
- `CreateDevice` must return a valid `AdapterOrdinal` (≠ -1) and the adapter list, and `BackBufferResolution`/
  `MainViewport` must be right: `UpdateSize`/`SizeChanged` unblocks the main thread.
- Window size/mode changes arrive via `SwitchDeviceSettings` → `MyRenderThread.SwitchSettings` → `SettingsChanged`/
  `ApplySettings`. On Linux, SDL window resizes come back through LinuxCompat's `BackbufferResizeRequest` →
  `MySandboxGame.SwitchSettings`. We forward **only window mode transitions** to the window (same-mode size changes
  flow window → backbuffer only, else HiDPI feedback), mirroring LinuxCompat's `ModeChangeForwarder.Forward`.

### Per-frame flow (`VulkanRender.Draw`) — keep this order
1. Apply `Config.FrameLimit` (sleep + short spin), recreate the swapchain if outdated, then
   `FrameLoop.TryBeginFrame()` (waits the slot fence, acquires). The frame-generation timer starts here, after
   the limiter/fence wait.
2. `GpuContext.BeginFrame(slot)` (runs deferred releases, resets the slot's staging ring), `GuiRenderer.BeginFrame()`.
3. `messages.Process(frameStarted)` — **after** the fence wait, because uploads issued while processing (e.g.
   `WaitTillLoaded` textures) write into this slot's staging ring. Without a frame, only state changes run.
4. `TextureCache.ProcessDecoded()` → `UploadQueue.Record(cmd)` → `GuiRenderer.RecordOffscreen` (LCD/generated
   targets) → `RecordScene` (only if a `DrawScene` message arrived this frame: build frame constants, skybox
   background, then opaque geometry into the HDR + depth pass) → read GPU counters and
   `GuiRenderer.SetDiagnostics` (queues diagnostics text runs and panel/graph sprites) →
   `GuiRenderer.RecordTextLayout` (text compute, outside the rendering pass) → `RecordFrame` (backbuffer: clear,
   composite scene if drawn, GUI and GPU text) → `FrameLoop.EndFrame()` (also ends the frame-generation timer).
5. `Present()` → `FrameLoop.Present()` (+ frame capture completion).

### Invariants (easy to break)
- Bindless texture slots are written **once** (when the texture is ready) and freed only via deferred release:
  descriptors use `UPDATE_AFTER_BIND | PARTIALLY_BOUND | UPDATE_UNUSED_WHILE_PENDING`; never overwrite a slot a frame
  in flight may read. Textures still loading sample the Transparent slot; failed ones Missing. Cubemaps use their
  own array (binding 2, slot 0 = black); cube slots are allocated/freed the same way.
- Every GPU object destroyed while frames are in flight must go through `GpuContext.Release` (`Dispose` on
  `GpuBuffer`/`GpuImage` already does).
- `UploadQueue`, `GuiRenderer` message handling and `TextureCache.Get`/`ProcessDecoded` are render-thread only.
  `TextureCache.GetSize`/`IsLoaded` are called from the game thread (thread-safe).
- Push constants: one 128-byte range for all stages, shared layout `BindlessDescriptors.PipelineLayout`.
- `SV_InstanceID` excludes the base instance: per-draw instance offsets go through the push-constant pointer, draws
  use `firstInstance = 0`.
- Color: textures (except normal maps) sampled as sRGB; swapchain is sRGB; GUI blends premultiplied in linear space.
  GUI block-compressed textures are authored premultiplied; raw RGBA GUI textures are premultiplied at load in gamma
  space (stock table); fonts premultiply in the shader unless the page is raw RGBA or named `FontDataPA`.
- Messages are pooled and ref-counted; `MessageQueueProcessor` disposes them. Keep a message beyond its frame only
  with `AddRef`.
- Release object IDs like stock: `RemoveRenderObject` → `MyRenderProxy.RemoveMessageId`, `CloseVideo` likewise.

### Message boundary
- ~147 `MyRenderMessageEnum` values; stock dispatch is `MyRender11.ProcessMessageInternal`
  (`MyRender11.cs:2429-3533`). Frames come from `SharedData.GetRenderFrame` (`MySharedData.cs`).
- Handled now: DrawCommands (recursive), VideoAdaptersRequest, SwitchDeviceSettings, SwitchRenderSettings (stored),
  UpdateDebugOverrides (stored), CollectGarbage, RemoveRenderObject/CloseVideo (ID release), CreateFont,
  SpriteMainViewportScale, DrawSprite/Ext/Atlas, DrawString/Aligned, SpriteScissorPush/Pop (main target only).
  Everything else is ignored (disposed).
- Output to the game goes through `MyRenderProxy` helpers (`SendVideoAdapters`, `SendCreatedDeviceSettings`
  (sent by MyRenderThread), `ScreenshotTaken`, `ClipmapsReady`, …).
- Billboards come through `SharedData.Billboards`/`TriangleBillboards`, not messages.

### Reused from VRage.Render (renderer-agnostic, no SharpDX)
`MyRenderProxy`, `MySharedData`, message classes, settings types, `MyModelImporter` (MWM),
`MyFont` (font XML + metrics), `MyImage` (PNG/JPG load + PNG save; **no DDS**), animations, particle data,
voxel interfaces, `VRage.Render.Scene` base classes, `SpriteScissorStack`.

### Planned architecture (later phases)
- **Frame graph**: passes declare resources; automatic barriers; parallel recording into secondary command
  buffers (mirrors stock `MyRenderScheduler` deferred contexts).
- **Resources**: mip skipping for texture quality, streaming, mesh/instance buffers, voxel cell buffers.
- **Feature/settings system**: each feature declares its options, cost tier, required device capabilities
  (`DeviceCapabilities` already records rayQuery/accelerationStructure/meshShader), and whether a change needs
  restart/pipeline rebuild/nothing.
- **Pipeline cache** persisted to disk (not done yet; pipelines are created without a cache).

## Graphics features and options

Presets: **Low / Medium / High / Ultra / Custom**, auto-selected on first run from the GPU
capability probe and VRAM. "Req" = hard requirement; features lacking it are disabled and greyed
out in the UI with a reason.

| Feature | Options | Req | Phase |
|---------|---------|-----|-------|
| Renderer | Vulkan / DirectX 11 (restart) | — | 0 (done, plugin config dialog) |
| Upscaling | Off / FSR 3 (Quality…Ultra Perf) / dynamic resolution | — | 5 |
| Anti-aliasing | Off / FXAA with Low/Medium/High/Very high quality (done) / TAA planned (FSR 3 implies its own AA) | — | 5 |
| Frame generation | Off / FSR 3 FG | — | 6 |
| Global illumination | Off / SSGI / Probe GI (DDGI, ray query) | DDGI: `VK_KHR_ray_query` | 6 |
| Shadows | Cascades count, resolution, filtering (PCF/PCSS), contact shadows | — | 3 |
| Ray-traced shadows | Off / sun / sun + local lights | ray query | 6 |
| Ambient occlusion | Off / SSAO / GTAO / RTAO | RTAO: ray query | 3 / 6 |
| Reflections | Env probe / SSR / RT reflections | RT: ray query | 3 / 6 |
| Volumetric lighting & fog | Off / Low / High | — | 6 |
| Lights | Max shadowed spot lights, clustered light budget | — | 3 |
| Tonemapper | Stock-like / ACES / AgX | — | 5 |
| HDR output | Off / On (HDR10) | HDR surface format | 6 |
| Textures | Quality (mip bias), anisotropy | — | 1–2 |
| Bloom, eye adaptation, CA/vignette | On/Off + strength | — | 5 |

### Options UI
- Inject a **"Vulkan"** button/section into the game's Graphics options screen
  (`SpaceEngineers.Game/SpaceEngineers.Game.GUI/MyGuiScreenOptionsGraphics.cs`: `RecreateControls` at :355, controls
  added around :674, OK button :705, `OnOkClick` :880) opening our screen. Needs `SpaceEngineers.Game` (and probably
  `Sandbox.Game`) added to the publicizer (both places, see Decisions) or reflection.
- Build that screen on the template's `SettingsGenerator` (`ClientPlugin/Settings`) where possible;
  extend it with presets, greyed-out unsupported items with tooltips, and "requires restart" marks.
- The same options stay reachable from Pulsar's plugin config dialog (`Plugin.OpenConfigDialog`).
- Persist via `ClientPlugin/Settings/ConfigStorage.cs` (`Storage/Vulkan.cfg`).

## Next task details

(Both tasks below are implemented and verified — LCD offscreen targets are [x] after the cockpit screen test. Kept as
reference for the stock behaviour.)

### Offscreen sprite targets and generated textures
Stock behaviour to reproduce:
- Sprite messages with `TargetTexture` set (not null / not `"DefaultOffscreenTarget"`) are bucketed per target by
  `MySpritesManager.AddMessage` (`VRage.Render11/VRage.Render11.Sprites/MySpritesManager.cs:53`) and drawn by
  `MyOffscreenRenderer.Render` (`VRage.Render11/VRage.Render11.Render/MyOffscreenRenderer.cs`) when a
  `RenderOffscreenTexture` message (`MyRender11.cs:3097`) asks for it, via `MyRender11.DrawSpritesOffscreen`
  (`MyRender11.cs:1730`/`:1759`, default format B8G8R8A8_UNorm, optional clear color, aspect ratio handling).
- Generated textures: `CreateGeneratedTexture` / `DestroyGeneratedTexture` / `ResetGeneratedTexture`
  (`MyRender11.cs:3314-3332` → `MyFileTextureManager.CreateGeneratedTexture`, `MyGeneratedTextureManager`). Names are
  checked with `MyRenderProxy.IsValidGeneratedTextureName`; the texture cache must resolve these names to the
  generated image instead of a file.
- Also still ignored: `PreloadTextures` (:3275), `UnloadTexture(s)` (:3289/:3308), `DeprioritizeTextures` (:3297),
  `ChangeMaterialTexture` (:3091), `RenderColoredTexture` (:3428).
Plan: `GuiRenderer` keeps a `SpriteBatcher` per target name; a render-target image per offscreen texture registered
in `TextureCache` under its name (bindless slot written once, contents re-rendered in place with proper barriers:
the slot stays valid, only the image contents change — fine because the slot itself is not rewritten); render these
targets before the main GUI pass each frame; `SpriteRenderer` needs a pipeline per target format (B8G8R8A8 UNORM).

### Vulkan options screen
See [Options UI](#options-ui). Start with: renderer choice, validation, texture anisotropy, and a placeholder
"Presets" dropdown; greyed items with reason strings come from `DeviceCapabilities`.

## World message spec

Researched 2026-10-17 from the decompiled source and the shipped shaders. File paths below are relative to
`SpaceEngineersDecompiled/` unless they start with `Content/` (shipped shaders). This is the reference the Phase 2
implementation follows; keep it updated when behaviour is discovered in game.

### Camera (`SetCameraViewMatrix`)

`VRage.Render/VRageRender.Messages/MyRenderMessageSetCameraViewMatrix.cs`: `ViewMatrix` (MatrixD),
`ProjectionMatrix`, `ProjectionFarMatrix` (skybox), `FOV`, `FOVForSkybox`, `NearPlane`, `FarPlane`, `FarFarPlane`,
`ProjectionOffsetX/Y`, `CameraPosition` (Vector3D), `LastMomentUpdateIndex`, `Smooth`, `UpdateTime`.
Stock processing: `VRage.Render11/VRageRender/MyRender11.cs:1825` and `:1844-1896` (`SetupCameraMatricesInternal`).

- `ViewAt0` = rotation-only view: copy the matrix, zero `M14/M24/M34/M41/M42/M43`, set `M44=1` (`:1850-1857`).
- Projection when `FOV > 0`: `Matrix.CreatePerspectiveFovRhInfiniteComplementary(FOV, aspect, near)` —
  `VRage.Math/VRageMath/Matrix.cs:1984`: `M11=1/(tan(fov/2)*aspect)`, `M22=1/tan(fov/2)`, `M33=0`, `M34=-1`,
  `M43=near`. Depth is reverse-Z (`near/z_view`), 1 at the near plane, 0 at infinity. `M31/M32` are overwritten
  with `ProjectionOffsetX/Y` (`:1864-1865`). Aspect = viewport width/height (`:1858`).
- `ViewProjectionAt0 = ViewAt0 * Projection` (row-vector XNA convention). `FovV = 2*atan(tan(FovH/2) * height/width)`.
- Stock renders camera-relative for precision: view has no translation, object matrices are translated by
  `-CameraPosition` (`MyCommon.UpdateFrameConstants`, `MyCommon.cs:396-416`, `WorldOffset`). We do the same.
- **Vulkan**: the stock projection is D3D Y-up. Our `CameraState` negates `M22` and `M32` so a positive-height
  viewport renders upright (clip Y is down in Vulkan); scene shaders then use normal viewports.
- Environment (`UpdateRenderEnvironment`, `MyRender11.cs:3223`): `MyEnvironmentData` (`MyEnvironmentData.cs`) has
  `EnvironmentLight`, `Skybox`, `SkyboxOrientation`, `EnvironmentProbe`, env map sizes, `TextureMultipliers`.
  `MyEnvironmentLightData` (`MyEnvironmentLightData.cs`): `SunColorRaw` and `SunSpecularColorRaw` are **linear**;
  `SunLightDirection` is the direction **from** the sun (shaders use `L = -directionalLightVec`,
  `Content/Shaders/Lighting/LightingModel.hlsli:41`); defaults `SunDiffuseFactor=2.9`, `SunSpecularFactor=1`,
  `AmbientDiffuseFactor=1`, `AmbientForwardPass=0.01`. No IBL/probe yet, so the forward pass uses
  `SunColorRaw * AmbientForwardPass` as ambient.

### Entities and actors

| Message | Fields (message class) | Stock handling |
|---------|------------------------|----------------|
| `CreateRenderEntity` | ID, DebugName, Model, WorldMatrix (MatrixD), Technique, RenderFlags, DepthBias, CullingOptions, MaxViewDistance, Rescale, ForceReload | `MyRender11.cs:2587` |
| `UpdateRenderObject` | ID, LastMomentUpdateIndex, `MyRenderObjectUpdateData` (WorldMatrix/LocalMatrix/LocalAABB + Has* flags) | `:2728` |
| `RemoveRenderObject` | ID, FadeOut | `:2748` |
| `UpdateRenderObjectVisibility` | ID, Visible, NearFlag | `:2797` |
| `ChangeModel` | ID, Model, Scale | `:2541` |
| `UpdateModelProperties` | ID, MaterialName (MyStringId), FlagsChange {Add,Remove}, DiffuseColor, Emissivity | `:2988` |
| `UpdateColorEmissivity` | ID, LOD, MaterialName, DiffuseColor, Emissivity | `:3061` |
| `UpdateRenderEntity` | ID, DiffuseColor, ColorMaskHSV, Dithering, FadeIn | `:2485` |
| `CreateStaticGroup` | ID, Model, Translation (Vector3D), LocalMatrices (Matrix[]) | `:2648` |
| `CreateManualCullObject` / `SetParentCullObject` | ID, DebugName/WorldMatrix; ID, CullObjectID, ChildToParent | `:2883`/`:2892` (culling only) |
| `CreateRenderCharacter` | ID, DebugName, Model, LOD1, WorldMatrix, RenderFlags, DiffuseColor, ColorMaskHSV, FadeIn | `:2453` |
| `SetCharacterSkeleton` | CharacterID, SkeletonBones (MySkeletonBoneDescription[]), SkeletonIndices | `:2473` |
| `SetCharacterTransforms` | CharacterID, BoneAbsoluteTransforms (Matrix[]), BoneDecalUpdates | `:2479` |

- `RenderFlags` (`VRage.Render/VRageRender/RenderFlags.cs`): `Visible=0x10`, `Near=0x40`,
  `SkipInMainView=0x400`, `SkipInForward=0x20000`, `NoBackFaceCulling=0x200`, `CastShadows=8`,
  `DistanceFade=0x4000`, etc. Our main pass skips `SkipInMainView | SkipInForward`; `Visible` gates drawing.
- `ColorMaskHSV` is passed to the shader **as an HSV delta**, unchanged (`ColorFromMask` at
  `MyRender11.cs:2398` returns its argument). `ColorizeGray` adds it to the grey texture HSV **and then
  adds stock's `TextureDebugMultipliers.ColorizeHSV` default `(0, 0.8, -0.1)`** before converting back
  to RGB; omitting that last term makes default/painted masks render grey.
- `UpdateRenderEntity.DiffuseColor` is not applied to entities by stock Render11 (only debris/instances use their
  own data); `UpdateModelProperties.DiffuseColor`/`Emissivity` are per material. Default emissivity is 1
  (`VRage.Render11.Scene.Components/MyModelProperties.cs:8`). Our actor registry applies these as color/emissive
  overrides keyed by material name.
- Static groups: the actor matrix is `CreateTranslation(Translation)`; `LocalMatrices` are model transforms
  relative to it (`VRage.Render11.GeometryStage2.StaticGroup/MyStaticGroupComponent.cs:64-90`). World =
  local matrix, translation moved by the group translation (camera-relative in our pass).
- Initial visibility comes from `RenderFlags.Visible` (all game `CreateRenderEntity` calls set it; verified in
  `Sandbox.Game/.../MyRenderComponent.cs:105`, `MyCubeGridRenderCell.cs:536`, `MyInstancedRenderSector.cs:72`).

### Cube grid instancing

Messages: `CreateRenderInstanceBuffer` (ID, ParentID, DebugName, `MyRenderInstanceBufferType`), then
`UpdateRenderCubeInstanceBuffer` (ID, `List<MyCubeInstanceData>`, `List<MyCubeInstanceDecalData>`, Capacity) for
cubes, or `UpdateRenderInstanceBufferRange` (ID, `MyInstanceData[]`, StartOffset, Trim) for generic instances.
`SetInstanceBuffer` (ID, InstanceBufferId, InstanceStart, InstanceCount, InstanceData, LocalAabb) binds a range of
an instance buffer to a render entity; stock handling `MyRender11.cs:2828-2880`, instances live in
`MyInstancing`/`MyRenderInstanceBuffer`.

- `MyCubeInstanceData` (`VRage/VRage/MyCubeInstanceData.cs`): 64 bytes:
  `byte bones[32]` (8 x Vector4UByte: bone indices/weights/range/flags/texture offset; index 8 is spread over
  the W bytes), then `Vector4 m_translationAndRot`, then `Vector4 ColorMaskHSV`. `LocalMatrix` is decoded from
  `m_translationAndRot` via `Vector4.UnpackOrthoMatrix` (`VRage.Math/VRageMath/Vector4.cs:370`):
  translation in xyz, `w = forwardDir*6 + upDir` with `Base6Directions` order
  `(0,0,-1),(0,0,1),(-1,0,0),(1,0,0),(0,1,0),(0,-1,0)` (`Content/Shaders/VertexTransformations.hlsli:117-127`);
  `Matrix.CreateWorld(position, forward, up)`. Bone flag bit 1 (in bone3.w) = hologram; bit 2 =
  skinning; `ColorMaskHSV.w < 0` sets the hologram bit and stores the absolute value.
- `MyInstanceData` (`VRage/VRage/MyInstanceData.cs`): 32 bytes, three `HalfVector4` matrix rows (translation in
  row W) + `ColorMaskHSV` `HalfVector4`.
- Shader side: `Content/Shaders/Geometry/VertexTemplateBase.hlsli` reads `__cube_transformation` /
  `__packed_bone0..7` and calls `construct_cube_instance_matrix` (`:262`, `:276`), key color comes from
  `__colormask` (`:490`, hologram/dithering flags at `:492-502`). Instance transforms are applied before the
  entity world matrix (`:356`, `:369`).
- Planned: one GPU buffer per instance-buffer ID, entity IDs referencing it; draw the cube model instanced;
  skip decals initially.

### MWM models and materials

`VRage.Render/VRageRender.Import/MyModelImporter.cs` reads the binary MWM. Version >= 1066002 has a string→offset
index dictionary (`:886-936`); older files are sequential (`:938`). Modern tag readers: `Vertices`
(`HalfVector4[]`), `Normals`/`Tangents`/`Binormals` (`Byte4[]`), `TexCoords0` (`HalfVector2[]`), `BlendIndices`
(`Vector4I[]`), `BlendWeights` (`Vector4[]`), `Bones` (`MyModelBone[]`), `MeshParts`
(`List<MyMeshPartInfo>`), `Sections`, `BoundingBox`, `BoundingSphere`, `LODs` (`MyLODDescriptor[]`),
`PatternScale`, `GeometryDataAsset`, animations.

`VRage.Render11/VRage.Render11.GeometryStage2.Model/MyMwmData.cs:153-297` is the authoritative load path:

- `GeometryDataAsset` present = stub; the actual geometry is that file (recursively resolved).
- `PatternScale` divides UVs (`:216-222`).
- Tangents are rebuilt from normals/tangents/binormals: `w = sign(cross(t,n) . b)`, packed with
  `VF_Packer.PackTangentSignB4` (`:134-151`).
- Parts are sorted by technique, indices concatenated in that order (`:229-281`). Each part stores technique,
  material name and the four texture paths (ColorMetal/NormalGloss/AddMaps/Alphamask, `MyMwmDataPart.cs`).
- LOD0 is in the main file; `LODs` describes the others (`MyLODDescriptor.cs`).

Vertex encodings (`VRage/VRage.Import/PositionPacker.cs`, `VF_Packer.cs`):

- Position `HalfVector4`: xyz scaled by `1/floor(max(|x|,|y|,|z|))`, w = that scale, capped at 2048; decode
  `w * xyz`.
- Normal `Byte4`: 15-bit x and 15-bit z packed little-endian in the low/high halves; the high bit of the x half
  is the sign of z; decode with `Content/Shaders/VertexTransformations.hlsli:62-74`.
- Tangent `Byte4` = same layout plus the sign of bitangent in the high bit of the second half; decode
  `:77-87`. Stock's per-vertex path flips normal-map y (`adjust_normalmap_precomputed_tangents`); our current
  opaque path instead uses stock's derivative path, which flips normal-map x (`NormalBuildTangent`).
- UV `HalfVector2`. Skinned models add `BlendIndices` (4 x int) and `BlendWeights` (4 x float).

Material channels (`Content/Shaders/Geometry/Materials/Standard/Pixel.hlsl`,
`Content/Shaders/Geometry/Materials/PixelUtilsMaterials.hlsli:67-103`,
`Content/Shaders/GBuffer/GBufferWrite.hlsli:18-33`):

- ColorMetal (`MyFileTextureEnum.COLOR_METAL`): sRGB; `.rgb` albedo, `.a` metalness.
- NormalGloss (`NORMALMAP_GLOSS`): **linear**; `.xy(z)` tangent-space normal, `.w` gloss. Stock's precomputed
  tangent path flips y; our current derivative path flips x (see [Decisions](#decisions)).
- Extensions (`EXTENSIONS`): sRGB; `.x` AO, `.y` emissive, `.w` coloring factor (`.z` unused by Standard).
- Alphamask (`ALPHAMASK`): linear, R8; `clip(alpha - 0.5)` for alpha-masked techniques.
- Recolor: `ColorizeGray` — `hsv = rgb_to_hsv(rgb_to_srgb(texel)); hsv.xy = 0; final = saturate(hsv + keyColorHSV);
  texel = lerp(texel, srgb_to_rgb(hsv_to_rgb(final)), coloring)` (`PixelUtilsMaterials.hlsli:15-31`).
  Stock also reduces coloring on metal (`RemoveMetalnessFromColoring`, `:60-65`) unless `METALNESS_COLORABLE`.
- Emissive = `saturate(extensions.y - 1/255) * emissivity`; base color is multiplied by the material color and the
  actor color, then lit (`LightingModel.hlsli`). We implement GGX sun + ambient in the forward pass.
- Texture path resolution: `MyMwmUtils`/`MyResourceUtils.GetTextureFullPath` — relative to the model's content
  root (the path before the `models` folder) when the file exists there, else the game `Content` root. Our loader
  adds Linux case fallback (exact case first, lower case second).
- sRGB rules come from `VRage.Render11/VRage.Render11.Resources/MyGlobalResources.cs:25-30` (CM/Extensions
  `BC7_UNorm_SRgb`, NG `BC7_UNorm`, alphamask linear).

### Characters

`CreateRenderCharacter` creates a `SKINNED` actor; `SetCharacterSkeleton` gives bone descriptions and the
skeleton index map; `SetCharacterTransforms` gives `BoneAbsoluteTransforms` every frame plus decal updates
(`MyRender11.cs:2453-2483`). Stock skinning is `matrix += bone[blendIndex] * blendWeight` for four bones
(`Content/Shaders/Geometry/VertexTemplateBase.hlsli:244-255`). Implemented with GPU skinning: compute deformation,
GPU bone-palette assembly, per-frame skeleton transforms and visible-object reporting; first-person material hiding
uses the per-material visibility flags (see Decisions).

### Our Phase 2 first slice (implemented 2026-10-17)

`CameraState` + `FrameConstants` (transposed view-projection for the shader's dot-product convention, sun and
ambient), `SceneActors` (create/update/remove/visibility/model change/material overrides/static groups),
`MwmLoader` (background parse through `MyModelImporter`, exact stock decodings, pattern scale, flat index list,
absolute texture paths) + `ModelCache` (device-local vertex/index buffers via `UploadQueue.UploadBuffer`, parts
with `TextureCache` entries), `TextureCache.TextureKind` (GUI sRGB+premultiply, CM/Extensions sRGB, NG/alphamask
linear), and `ModelPass` (forward opaque, reverse-Z, sun + ambient; persistent opaque part cache + GPU
LOD/culling for non-skinned actors, direct path for skinned/transparent, texture slots through the bindless set). `Shaders/Scene/Model.slang` implements the normal decodes,
derivative tangent basis + Toksvig gloss, colorize, alpha mask, GGX sun/IBL and ambient. Cube grids: `InstanceBuffers` owns one
device-local buffer per `CreateRenderInstanceBuffer` (updated by replacing the buffer, so frames in flight never
read swapped contents), `SetInstanceBuffer` puts the range on the actor, and the shader decodes
`MyCubeInstanceData`'s packed ortho matrix and per-instance HSV. Verified by frame capture (18 instances).
Generic (`MyInstanceData`) buffers are stored but not drawn yet.

Not implemented in that first slice (later added): generic instancing (world-environment sectors), voxels,
LOD selection, frustum culling, skinning, transparency, shadows, G-buffer/motion vectors, skybox, eye adaptation
(a fixed-exposure ACES tonemap placeholder was in `CompositePass`).

## CPU-to-GPU migration

Physics owns the CPU, so the renderer must not grow per-frame CPU work. Rule for new features: if it can run in a
compute pass or a shader, it does; CPU is for uploads, resource lifetime and gameplay messages only.

Already on the GPU: character deformation and bone-palette assembly, vertex/instance decoding, materials/IBL/
lighting, skybox, mip generation, sprites/offscreen rendering, and the persistent user-settings block; model/texture
preloads start background work during the loading screen.

Remaining per-frame CPU work and its GPU replacement:

| CPU work now | GPU replacement |
|--------------|-----------------|
| Actor/part enumeration, frustum + distance culling, per-draw submission | **Persistent opaque parts + GPU LOD/culling/submission done for non-skinned actors, including instanced cubes and static groups.** Still CPU: per-frame actor record upload, skinned actors and transparent parts. Next: upload actor state only when changed and move the remaining paths into GPU buckets. |
| Transparent back-to-front sorting | **Done**: weighted-blended OIT accumulation + revealage targets; no CPU sort. |
| Per-draw world transposes / material data packing | **Material packing moved into the persistent part cache.** Actor world transposes are still computed while writing per-frame actor records (moving camera/grids); candidate for a camera-relative GPU transform. |
| Per-frame transient uploads | Scene/world constants still upload per frame; parts, LOD ranges and user settings are persistent and update only on messages/changes. |
| UI text layout / sprite batching | **Main-target text is GPU-shaped/laid out** by `TextLayout.slang` into glyph instances; the CPU still uploads run descriptions/strings each frame (no caching yet). Remaining CPU: offscreen LCD text (`RenderFont.DrawString` fallback), diagnostics panel/graph rectangles and history sampling. |

The indirect path's required pieces are already in place: `shaderDrawParameters` is enabled, `SV_DrawIndex` is
supported by Slang, bindless + BDA is used everywhere, and compute pipelines/barriers exist from skinning.

## Phases

Each phase leaves the game launchable; unimplemented message types are disposed as no-ops.
Legend: [x] done and verified, [~] implemented but not verified in game, [ ] open.

### Phase 0 — Bootstrap (done)
- [x] Template set up for Vulkan (assembly name, IDs, author SentorX, RepoId buttheadbob/Vulkan, net10-only,
      Vortice.Vulkan, publicizer, unsafe, `<Runtimes>CoreCLR</Runtimes>`)
- [x] Hook (`RenderProxyInitializePatch`, `VulkanEarly`, from `Preloader.Finish()`)
- [x] Config: renderer choice + validation toggle; D3D11 fallback on probe failure; config switch on device failure
- [x] Runtime platform detection (`GameWindowBridge`)
- [x] Instance, validation messenger (opt-in), device pick + capability probe; surface, swapchain, frames in flight
- [x] `VulkanRender` facade + `MessageQueueProcessor`
- [x] Verified in game by the user (Pulsar 2.4.3, Wayland, RX 7900 XTX): boots, alt-tab/focus recreates the
      swapchain, window close exits
- [x] Shader build tool (`Tools/ShaderCompiler`, Slang)

Known gaps from Phase 0:
- Adapter/display-mode list still comes from `MyVRage.Platform.Render.GetRenderAdapterList()` (DXGI metadata only,
  DXVK on Linux). Replace with SDL3/Win32 display enumeration + Vulkan device list.
- Window mode forwarded only on mode transitions. LinuxCompat also has `DriveDirect` for explicit same-mode
  resolution changes from the options screen; check whether picking a new windowed resolution resizes the window.
- Exclusive fullscreen is whatever the window system gives; no `VK_EXT_full_screen_exclusive`.
- Memory budgets (`MyRenderProxy.Settings.ResourceStreamingPool` etc.) are not set; only Render11 used them.
- No VkPipelineCache yet.

### Phase 1 — Main menu (done; small leftovers listed below)
- [x] GPU resource layer (`GpuMemory`, `GpuBuffer`, `GpuImage`, `GpuContext` deferred release, `UploadQueue`,
      `BindlessDescriptors`)
- [x] DDS loader (4012/4013 game textures), PNG/JPG via `MyImage`, `TextureCache`
- [x] Sprites (`SpriteBatcher`, `SpriteRenderer`, `Sprite.slang`): batches break only on scissor/viewport changes
- [x] Fonts (`RenderFont` port)
- [x] sRGB swapchain, GUI pass, `FrameCapture`
- [x] Verified in game by frame capture: Graphics Options screen and main menu correct, including downloaded
      PNG/JPG news banners (2026-10-17)
- [x] User's interactive check: menus work (2026-10-17)
- [x] Offscreen sprite targets + generated textures: `TextureCache.CreateGenerated/ResetGenerated/DestroyGenerated`
      (RGBA → R8G8B8A8_SRGB, RGBA_Linear → UNORM, Alphamask → R8; optional full mip chain via `MipGenerator` blits),
      `GuiRenderer` keeps offscreen sprite messages per target (AddRef, newest message batch wins, like
      `MySpritesManager`), `RenderOffscreenTexture` queues a job (max 16 per frame), recorded before the main pass:
      clear to linearized background, sprites with `SpriteBlend.PremultipliedColorOnly` in `size * AspectRatio`
      coordinates, then mips; `GeneratedReady` drives `IsTextureLoaded`. `SpriteRenderer` caches pipelines per
      (format, blend) and allocates instances linearly per frame. `UploadQueue` uses dedicated staging outside an
      open frame. Verified by the cockpit LCD in the user's world (DangerZone background + text).
- [x] Preload messages: PreloadTextures, PreloadModel(s), PreloadMaterials (background start, GPU work during load)
- [x] Texture messages: UnloadTexture(s), DeprioritizeTextures (implemented; deprioritize is a no-op)
- [x] World load / save / reload on Vulkan works (user, 2026-10-17): HUD, menus and "World loaded" text show,
      no errors in the log; the game does not block on render outputs we do not send yet
- [ ] Video: stubbed (`IsVideoValid` false; the menu shows its static background). Real playback in Phase 7
      (LinuxCompat already has an FFmpeg-based `MyLinuxVideoPlayer` behind `IVideoPlayer`, but it uploads to D3D11)
- [x] "Vulkan" options screen, first version: `ClientPlugin/Gui/GraphicsOptionsPatch.cs` adds a "Vulkan Renderer"
      button to the Graphics options screen (verified by frame capture and by the user) that opens the plugin's
      settings dialog (`Plugin.OpenConfigDialog`): Status section (active renderer + fallback reason, GPU, ray tracing /
      mesh shader support via `RendererStatus` and the new read-only `[Info]` settings element) and Renderer section
      (renderer choice, validation, frame limit, diagnostics, shadows and lighting tuning). Still to do: presets,
      per-feature options as features land, greyed-out unsupported items with reasons.
- **Milestone:** full main menu and options usable on Vulkan.

### Phase 2 — World geometry (in progress)
- [x] Scene infrastructure: `SceneTargets`, `CompositePass` + `Composite.slang`, `GraphicsPipelineBuilder`,
      `TransientAllocator`, `DrawScene` handling (scene drawn only on frames with `DrawScene`)
- [x] World message spec written into this roadmap (see [World message spec](#world-message-spec))
- [x] Camera, frame constants, reverse-Z infinite projection, camera-relative world matrices
- [x] Frustum culling, `MaxViewDistance`, LOD selection (all LODs loaded, stock distances + hysteresis);
      opaque LOD selection now runs in the culling compute over persistent part ranges
- [x] Back-face culling matching D3D (clockwise front faces; `NoBackFaceCulling` uses a no-cull pipeline)
- [x] Actors/render entities, MWM meshes, cube-grid instancing, static groups
- [x] Materials (standard + alpha-masked) on bindless textures, stock decodes and colorize;
      block paints and skins verified in game (green capture, user red-paint confirmation)
- [x] Transparent models (glass, holo, shield): implemented with `MyTransparentMaterials`, weighted OIT
      (accumulation + revealage, no CPU sort), depth read-only; verified in game
- [~] Triplanar materials and dithering remain; `DECAL_CUTOUT` static decals are in, blended/projected decals are not;
      OIT is done
- [x] Skinned characters: GPU compute deformation, GPU bone palette, skeleton/transforms, visibility output;
      character appears correctly posed and animates (third-person capture). First-person head hiding uses the
      game's per-material visibility flags.
- [ ] Voxel cells (consume game-meshed clipmap data), voxel materials; `ClipmapsReady` output
- [ ] G-buffer **including motion vectors** (needed later for TAA/FSR/GI); forward sun lighting is done
- [x] Persistent opaque part/material ranges + GPU LOD selection incl. static groups (first GPU-scene slice)
- [ ] Frame graph + pipeline cache
- **Milestone:** load a world; grids, characters and planets render.

### Phase 3 — Lighting and shadows
- [~] PBR lighting (GGX, energy-conserving), sun + static skybox IBL and the dynamic scene probe are done; sparse
      auto-exposure is in (full histogram adaptation remains)
- [~] Cascaded sun shadows with soft PCF, LOD0 casters and alpha-tested casters (opt-in, 2026-10-21); LOD1+
      visual verification, contact shadows and the spot shadow pool remain
- [~] Dynamic point/spot lights (`CreateRenderLight`/`UpdateRenderLight` implemented; clustered light culling later)
- [x] Dynamic environment probe + optional SSR (camera-centred 128-512² one-face-per-frame capture with current/target blend, live receiver-distance close/far mip blend, skinned actors, thruster/glow billboards, single-sample and shadow-sample-free captures); GGX prefiltering remains; SSR is off by default
- [ ] SSAO / GTAO
- **Milestone:** lighting at least equal to stock.

### Phase 4 — Transparency and effects
- [x] Weighted OIT and transparent models (glass); [~] billboards/particles (thruster flames and standard billboards done; additive glares, screen-space decals and foliage remain)
- [ ] GPU particles (CPU billboards and the thruster flame animator are done)
- [ ] Atmosphere scattering, clouds, fog
- [ ] Screen-space decals, foliage, highlights/outlines
- **Milestone:** visually feature-complete vs. stock.

### Phase 5 — Post-processing and upscaling
- [x] First HDR bloom pass (bright pass + separable half-res blur + composite; `BloomScale` slider)
- [~] Eye adaptation, tonemapping (stock-like / ACES / AgX), CA/vignette; full stock multi-scale bloom
- [ ] TAA (jitter + motion vectors)
- [ ] FSR 3 upscaling (replaces stock dynamic resolution)
- **Milestone:** better image quality and performance than stock.

### Phase 6 — Modern features
- [ ] Acceleration structures (BLAS per mesh/LOD, voxel cell BLAS updates, TLAS per frame)
- [ ] Global illumination: SSGI (all GPUs), DDGI probe GI (ray query)
- [ ] RT shadows, RTAO, RT reflections
- [ ] Volumetric lighting and fog
- [ ] HDR10 output
- [ ] FSR 3 frame generation
- **Milestone:** "up to date graphics" goal met, all scalable from the options menu.

### Phase 7 — Windows and completeness
- [ ] Win32 surface, Windows test pass (code exists in `Win32WindowBridge`, untested)
- [ ] Video playback (main-menu background)
- [ ] Screenshots (`TakeScreenshot` → `ScreenshotTaken`; `FrameCapture` is a starting point), debug draw, profiler
- **Milestone:** same plugin release works on both platforms.

## Reference: game source map

Decompiled source: `/run/media/sentorx/767E0D0A7E0CC4B9/Development/Development/Personal/SE Decompiled/SpaceEngineersDecompiled`
(one folder per assembly; local to the user's machine). Shipped HLSL (reference only): `<game>/Content/Shaders`.
Pulsar / LinuxCompat sources for reference: download GitHub archives (SpaceGT/Pulsar tag v2.4.3,
CometWorks/linux-compat) into a scratch dir such as `/tmp/vk-research/`; never vendor them into this repo.

| What | Where |
|------|-------|
| Renderer selection | `SpaceEngineers/SpaceEngineers/MyProgram.cs:189-225` |
| Renderer interface | `VRage.Render/VRageRender/IMyRender.cs` |
| Minimal implementation | `VRage.Render/VRageRender/MyNullRender.cs` |
| DX11 facade | `VRage.Render11/VRageRender/MyDX11Render.cs` |
| Render thread | `VRage.Render/VRageRender.ExternalApp/MyRenderThread.cs` |
| Message dispatch | `VRage.Render11/VRageRender/MyRender11.cs:2429-3533` (frame processing :2263-2400) |
| Frame (stock) | `MyRender11.cs` `FullDraw` :1556, `DrawGameScene` :1906; `VRage.Render11.Render/MyRenderScheduler.cs` |
| Message enum / classes | `VRage.Render/VRageRender.Messages/` |
| Sprites (stock) | `VRage.Render11/VRage.Render11.Sprites/MySpritesManager.cs`, `MySpritesRenderer.cs`; `MyRender11.cs` `RenderMainSprites` :1684 |
| Offscreen sprites (stock) | `VRage.Render11.Render/MyOffscreenRenderer.cs`; `MyRender11.DrawSpritesOffscreen` :1730 |
| Fonts (stock) | `VRage.Render11/VRageRender/MyRenderFont.cs`, `MyRender11.LoadFont` :598; `VRage.Render/VRageRender/MyFont.cs` |
| Textures (stock) | `VRage.Render11/VRage.Render11.Resources/MyFileTextureManager.cs` (name → path :105-135), `Resources.Internal/MyFileTexture.cs` (sRGB + GUI premultiply :370-390), `MyResourceUtils.NormalizeFileTextureName` |
| Device/swapchain (stock) | `VRage.Platform.Windows/VRage.Platform.Windows.Render/MyPlatformRender.cs:635-911`, `MyWindowsRender.cs` |
| Window (Windows) | `VRage.Platform.Windows/VRage.Platform.Windows.Forms/MyWindowsWindows.cs`, `MyGameWindow.cs` (`OnModeChanged` :198) |
| G-buffer (stock) | `VRage.Render11/VRage.Render11.Resources/MyGBuffer.cs:109-135` |
| Shadows (stock) | `VRage.Render11/VRageRender/MyShadowCascades.cs` |
| Models | `VRage.Render/VRageRender.Import/MyModelImporter.cs`; `VRage.Render11/GeometryStage2.Model/MyModel.cs` |
| Graphics options UI | `SpaceEngineers.Game/SpaceEngineers.Game.GUI/MyGuiScreenOptionsGraphics.cs` |
| Game output handling | `Sandbox.Game/Sandbox/MySandboxGame.cs:3469` |
| LinuxCompat window | `ClientPlugin/Compatibility/SdlGameWindow.cs`, `Patches/PlatformGuards/CreateWindowPatch.cs`, `Patches/WindowManagement/*` (in linux-compat repo) |
| Pulsar preloader / compiler | `Shared/Preloader.cs`, `Legacy/Program.cs` (`SetupPlugins`, `SetupGame`), `Compiler/RoslynCompiler.cs` |

## Risks and open questions

- **LinuxCompat coupling** — we depend on its SDL3 window (`MyWindowsWindows.WindowHandle` = `SDL_Window*`,
  `ClientPlugin.Patches.PlatformGuards.SdlInput2Provider.Instance.OnModeChanged`, and by reflection
  `ClientPlugin.Compatibility.SdlRenderThread.Dispatch` to run SDL calls on its video thread). Changes there can
  break us; detection is defensive and falls back to D3D11. Windowed mode cannot be moved on Wayland (the
  compositor owns placement); only the fullscreen display selection is under our control. Exclusive `Fullscreen`
  still lets LinuxCompat pick the mode from the window's current display; the tested mode is `FullscreenWindow`.
- **Ray tracing on voxels** — voxel cells change constantly; BLAS rebuild budget needs design.
- **Video playback** — stock uses DirectShow; needs a cross-platform decoder.
- **Mods** — mod models/textures/LCD scripts must keep working through the same message boundary.
- **Other plugins** that patch `VRage.Render11` will not affect (or may conflict with) our renderer.
- **Validation** not yet run (layer not installed); expect fixes once someone runs with it.

## Session log

- **2026-10-24 (voxel materials, always-on, tree-path blocker)** — Voxels are no longer optional; the
  **Voxels/planets setting was removed and the path is always enabled**. Added real planet/voxel texturing:
  `VoxelPass` now loads each material's stock ColorMetal/NormalGloss/Extensions files through `TextureCache`,
  writes a 256-entry device-visible material buffer (six bindless slots, tiling, Far3Color, flags), and
  `Voxel.slang` samples them with stock-style triplanar weights (CM for X/Z/Y, NG for gloss, Ext for AO/emissive).
  Textures are cached per material and the material buffer refreshes when `TextureCache.ReadyGeneration` changes.
  Verified on EarthLike: `/tmp/vk_voxel_tex.png` shows textured snow/rock and dark ice/ground; 425→~600 cells,
  ~8,000 material draws, 42 MB cell buffers, 30+ s stable with no validation VUIDs or page faults.

  Current blocker: the **tree/foliage camera path** crashes with a GPUVM page fault at address 0. It reproduces with
  `VULKAN_NO_VOXEL=1` and `VULKAN_DEBUG_LOOKAT=Tree`, so it is in the tree MWM/model path, not the voxel renderer.
  Added null-buffer guards in `BuildGpuPart`, transparent/direct/probe draws and `Culling.slang`, but the crash still
  reproduces. GPU-AV on the tree camera is too slow at 4K and times out (no VUID); the next session must isolate a
  single tree model/part (or its textures) with core validation and a low-workload camera. No further GPU tests were
  run after this finding.

  Validation fixes retained from this session: GPU-AV support (`VULKAN_VALIDATION_GPU=1`), `drawIndirectCount`,
  single-sample probe pipelines, transfer-queue-safe barriers/mips, and the swapchain acquire wait. The GPU-AV smoke
  test is clean. No git operations.
- **2026-10-24 (reflection tiers, voxel planets, GPU safety)** — *(Historical; superseded by the 2026-10-24 voxel materials entry for default-on/textures and the tree blocker.)* Implemented the next reflection pass and the first
  voxel/planet renderer after the user asked for close/far IBL quality, live tuning, and a planet test world.
  Reflections now use **one face per frame** into a target cube, copied from the completed cube at cycle start;
  `Environment.slang` blends current/target over a live 0.05–1 s window, so the old whole-cube-every-frame cost is
  gone without the stepping of naive time slicing. Added live **Reflection close distance**, **far distance**,
  **far blur** and **minimum blur** sliders; `reflectionMipForDistance` smoothly fades receiver-distance mips so
  near surfaces keep detail and far surfaces get colour/blobs from the same live scene cube. Opaque IBL now applies
  to **all** opaque materials (stock AmbientSpecular) instead of metals only, so a ship over grass/snow picks up the
  ground colour; SSR is now **off by default** and remains an optional metal/high-gloss sharpening pass. The static
  probe capture still uses the skybox IBL, and probe voxel capture is off by default.

  Added `VoxelPass` + `Voxel.slang`. The renderer now consumes `CreateRenderVoxelMaterials`/`UpdateRenderVoxelMaterials`
  and `VoxelCreate`, binds an `IMyVoxelActor` adapter to the game clipmap (using the stock `MyMeshes` render-data
  provider through reflection because `VRage.Render11` is not publicized), uploads interleaved 24 B cell meshes to
  **device-local** buffers through `UploadQueue`, and draws them with standard vertex input + `DrawIndexed`.
  Material colours come from `MyRenderVoxelMaterialData.Far3Color` with a neutral rock fallback. Voxels are
  **default-off** in the new **Voxels/planets** setting, because bring-up exposed repeated RADV device losses.
  Safety added: 256 MB device-local budget, index/vertex/size validation, 8192 draw cap, 20 km main/3 km probe cell
  distance, `VULKAN_NO_VOXEL`, `VULKAN_VOXEL_NO_DRAW`, and a crash-loop marker (written when drawing begins and
  removed after 30 s stable; if present at launch, voxels are disabled for that session). Enabled
  `VK_EXT_device_fault` and log the reported address/vendor fault before a device-lost exception propagates.

  The GPU debugging pass (Khronos validation loaded locally from the distro package, plus `VK_EXT_device_fault`,
  `VULKAN_VALIDATION=1`, `VULKAN_VALIDATION_GPU=1`, `AMD_DEBUG=hang` and `RADV_DEBUG=zerovram`) found the actual
  causes: (1) the probe used the **4x MSAA model/skybox pipelines with 1x probe attachments**; (2) the device used
  `vkCmdDrawIndirectCount` without enabling `VkPhysicalDeviceVulkan12Features::drawIndirectCount`; (3) transfer-queue
  barriers used graphics-only `ALL_COMMANDS`/vertex/index access masks; (4) the swapchain acquire semaphore wait
  did not cover all stages before the image-layout transition; and (5) voxel cells were read from host-visible BDA
  pointers with manual indexing. Fixed by adding single-sample probe pipelines for model, skybox and per-face
  records; enabling `drawIndirectCount`; transfer-safe barriers and `MipGenerator(..., transferQueue)`; an
  `ALL_COMMANDS` acquire wait; and device-local voxel buffers with standard vertex input + `DrawIndexed`. Added
  GPU-assisted validation support (`VULKAN_VALIDATION_GPU=1`) and the `VK_EXT_validation_features` instance extension.

  Final validation: the GPU-AV smoke test is clean (no VUIDs/hazards) after the swapchain wait fix. The normal
  validation planet run (no GPU-AV overhead) reached 30+ s with EarthLike, 425 cells, 7146 material-part draws and
  32 MB voxel buffers, with **no validation VUIDs, no GPUVM page fault, and the marker cleared**. GPU-AV itself was
  too slow for the 4K planet scene and timed out without a new fault, so the normal layer was used for that run. Captures:
  `/tmp/vk_planet8.png` (snow/grass/rock patches, stable) and earlier fault-fix captures. Remaining: atmosphere/blue
  sky, stock triplanar texture arrays, foliage, voxel shadows and probe/far-planet reflections; the menu spawn-overlay
  and world-settings-panel overflow reported by the user are deferred. No git operations.
- **2026-10-23 (live, correctly-oriented reflections and probe effects)** — *(Historical; superseded by the 2026-10-24 capture-scheduling entry.)* User feedback on the reflection pass:
  orientation still wrong, every shaded block reflected, visible stutter, thrusters/emissives missing from
  reflections, and edge glitter. Root cause of the orientation bug: the probe face projection applied a 180°
  image correction (negated `M11`, `M22`, `M32`) for cube sampling. A CPU check of the face matrices against
  Vulkan's cubemap face table shows that the **unflipped stock projection maps every face pixel to the matching
  cube (u,v) exactly**; any flip mirrors or rotates the cube. `CreateProbeFace` now uses the plain stock
  projection, and the `x` axis correction was removed from `Composite.slang`/`Transparent.slang`. Added
  `VULKAN_DEBUG_VIEW=16` (left: real skybox, right: raw probe along the same view ray) and `VULKAN_DEBUG_VIEW=15`
  (red/green overlap) for orientation checks. The probe now also captures against the **static skybox IBL**, not
  the live cube, so metal reflections in the probe cannot feed back into the next capture.

  The probe is **live**: all six faces are captured every frame (no 2/4/8 Hz throttle, no six-frame cross-fade);
  default resolution is 256², with 512² still selectable (the user's config keeps 512²). Generic space-dust
  billboards are excluded from the probe, which removed the reflected star/particle glitter. Thruster flames and
  thruster/particle point glows are drawn into each probe face through a new additive `Billboard.slang`
  `fragmentProbe` pipeline, so glass and metal reflect active engines and glowing effects; verified with
  `VULKAN_THRUSTERS_FORCE=1` (`/tmp/vk_final_thrusters.png`).

  Opaque reflections are now limited to reflective materials: the model shader writes the sun-shadow factor into
  the HDR scene alpha, and the composite attenuates IBL/SSR by that factor and by a metal mask
  (`smoothstep(0.15, 0.5, max(f0))`). Opaque dielectric SSR was removed again (glass keeps its separate SSR path),
  restoring the metallic/high-gloss gate. Probe reflections use the static mip curve with a **two-mip minimum**,
  plus normal-derivative specular AA in composite/transparent, so the probe's unfiltered starfield no longer reads
  as sharp glitter; the model shader also widens direct highlights where the world normal changes quickly. The
  remaining thin bright bevel lines are **direct sun specular**, not probe/SSR: they are unchanged with
  `VULKAN_NO_REFLECTIONS=1` and `VULKAN_NO_SSR=1`. Added those two diagnostic flags.

  In-game at the user's viewpoint, 4K/4x MSAA/shadows 1000 m, 512² live probe: ~49 fps, probe ~0.5 ms CPU, no
  exceptions. Captures: `/tmp/vk_reflect_live2.png` (live, clean shaded hull), `/tmp/vk_reflect_live_noprobe.png`
  (probe disabled), `/tmp/vk_final_thrusters.png` (cyan/yellow thruster/glow reflections in the glass),
  `/tmp/vk_orient16c.png` (split orientation check). Build, shader compiler and smoke test pass. Remaining:
  camera-centred probe parallax on very close panels, GGX prefiltering of the cube, and the direct bevel specular
  (possibly a stock-forward difference). No git operations.
- **2026-10-23 (reflection quality and probe orientation)** — Reversed the 128² probe default that made the user's
  large glass panels read as a smear: **Reflection probe** now defaults to High (512²), and probe reflections use a
  sharper mip bias (`(1-gloss)^3` for probes vs `^2` for the static skybox). The screen-space reflection gate was
  wrong for dielectrics: it required `f0 >= 0.3`, so painted/glass-like surfaces with `f0 ~= 0.04` never traced SSR
  and fell back to the camera-centred cube. SSR now gates on gloss, uses Fresnel/metallic only for the blend
  weight, marches 0.25 m steps with a distance-scaled ~0.12 m tolerance and quality-scaled step count, and fades at
  the screen edge. Glass SSR got the same tighter march plus an edge fade, and transparent shading now flips the
  normal toward the camera for two-sided panels so back faces cannot reflect the hemisphere behind them.

  The bigger correctness bug was **probe orientation**: the probe was sampled with a z correction, but an in-game
  A/B debug view (`VULKAN_DEBUG_VIEW=14`, raw / x-flip / z-flip / both quadrants) showed only the x-flip variant
  matched the real starfield/nebula in the same screen directions. Composite and transparent now apply the x
  correction, so the probe samples the hemisphere the surface actually faces. Captures: normal `/tmp/vk_reflect_test5.png`; disabled-probe comparison `/tmp/vk_reflect_noprobe.png`;
  debug `/tmp/vk_probe_view12.png`, `/tmp/vk_probe_view13.png`, `/tmp/vk_probe_view14.png`; orientation comparison
  `/tmp/probe_crop_compare.png`. In-game at 4K: ~50 fps, CPU frame ~4.0 ms, `probe 0.03 ms`; the probe is no longer
  a CPU cost, but the camera-centred cube still has no parallax correction, so very close flat panels rely on SSR
  and remain approximate off-screen. Next: validate the user's specific panels, then planar/parallax-corrected
  reflections if needed, plus GGX prefiltering instead of the box mip chain. **Superseded by the live,
  correctly-oriented entry above: the x-flip was also wrong; probe faces now use the unflipped projection.**

- **2026-10-23 (reflection probe performance)** — *(Historical; superseded by the 2026-10-24 one-face blend.)* Reworked the dynamic probe after two failed extremes: all six
  faces every frame (1 camera + 6 scene renders) dropped the user's 120 fps to ~20, while one-face-per-frame time
  slicing kept the frame cost flat but made reflections **visibly step/drift** because the six faces came from
  different moments (the user rejected that). The resolution is to keep the whole cube temporally coherent but make
  each update cheap: `ProbePass` now captures **all six faces in one frame**, and updates are throttled by the new
  **Reflection probe updates** setting (Low 2/s, Medium 4/s, High 8/s; default Medium) with a 1.5 m camera-move
  trigger and a 1 s stationary heartbeat. A completed cube cross-fades over six frames using new
  `FrameConstants.IblCubePrev`/`IblBlend` fields sampled in `Environment.slang` (`iblColorRaw`). Added
  **Reflection probe** resolution (Low 128² / Medium 256² / High 512², restart required, default Low), made probe
  captures single-sample (no MSAA) and disabled shadow-map sampling for probe faces (`ShadowEnabled = 0`). Together
  these cut the old six-face capture cost by roughly an order of magnitude while avoiding the time-sliced
  stepping; the old 512²/10-mip constants became instance properties so the resolution controls cube and mip count.
  Whole-cube capture also required moving the probe bucket-counter reset to the GPU (`vkCmdFillBuffer` plus
  draw→transfer→compute barriers): the old CPU reset ran per face during recording, i.e. before submission, so a
  multi-face command buffer would have drawn the earlier faces' ranges from later faces. SSR already covers
  screen-space ship/block reflections, so planar reflections are still unnecessary. Build,
  `ShaderCompiler --check` and `VulkanSmokeTest` pass (185 frames at ~61 fps). Remaining: in-game validation at
  120 fps on a lit surface, compare 128/256/512 quality against stock, and consider GGX prefiltering if box mips
  read too smooth; if the 4/s default still hitches, the update-rate setting is the first knob. **Superseded by
  the live entry above: the throttle and cross-fade were removed.**

  GPU follow-up: cull-plane extraction moved from the CPU `Frustum` helper into `Culling.slang` for **both** the
  main and probe dispatches. `CullPushConstants` now carries the four transposed view-projection columns
  (104 bytes, down from 120) and the compute shader derives/normalizes the five planes itself; the CPU no longer
  builds `Frustum` objects or writes plane arrays in `GpuSceneDraws`. The probe actor-state array is uploaded once
  per whole-cube update and shared by all six face dispatches (previously copied six times), and the six cubemap
  face matrices are cached until the camera near plane changes instead of rebuilding and inverting them every
  update. Added a `probe` CPU timer (overlay and frame-hitch log) that reports the cost of the most recent
  whole-cube recording, so the direct/skinned-actor share can be measured in game rather than guessed.

  Skinned actors now use the same GPU cull buckets as static actors, for both the main view and the probe.
  `GpuSceneActor` grew a `SkinnedVertices` address and a `ForcedLod`; the culling shader overrides the part's
  vertex pointer with the skinning-pass output and uses the LOD that pass actually deformed, so index ranges and
  the deformed buffer stay in sync. `UpdateSkinnedActorStates` refreshes that state once per frame, and the probe
  uses the main view's LOD state (`LodUpdate = 0`). The probe also disables shadow-caster appends
  (`ShadowCulling = 0`); this now also fixes the old bug where probe buckets appended LOD0 shadow parts next to
  the selected-LOD forward parts. The first-person self actor is kept out of the probe by a GPU-side
  bounds-surrounds-camera test and by refreshed visibility flags. Skinned transparent parts and skinned shadow
  casters are still deliberately skipped, as before. Remaining CPU-side for the probe: actors that have no model
  or are not loaded yet, which still go through `RecordProbeActors`. Deferred until in-game validation: rendering
  color directly into cube layer views (removes six `vkCmdCopyImage` calls), a color-only probe fragment/pipeline
  (drops the two unused
  G-buffer attachments), and a multiview probe pass (one pass instead of six; per-view culling makes it nontrivial).

  In-game automated capture (`VULKAN_AUTOLOAD=20`, `VULKAN_DEBUG_LOOKAT=astronaut`, 4K, shadows 1000 m, 4x MSAA)
  rendered the animated astronaut correctly (the skinned GPU bucket path works) and reported `probe 0.09 ms` CPU
  in the overlay, with no exceptions or frame-hitch logs. FPS was 39.5 at 4K with those settings, so this was a
  correctness/CPU check, not a 120 fps benchmark; reflection quality and probe orientation on a lit surface still
  need the dedicated visual pass. Capture: `/tmp/vk_ingame.png`; the game was stopped by the test timeout after
  the screenshot.

- **2026-10-23 (dynamic reflection probe)** — Added a camera-centred dynamic environment probe so glossy surfaces
  can reflect ship/block geometry that is off-screen. `ProbePass.cs` renders six 512² faces into a 10-mip cubemap
  (one face per frame, two-frame cooldown, all six faces sharing the captured position), box-filters the mips
  with the existing `MipGenerator`, and swaps a pending probe to active only when complete. New and previous
  probes cross-fade over six frames (`ProbeBlend`) so camera movement does not pop the reflection, and the probe
  faces use a 180° image correction to match Vulkan cube-face sampling. Captures are skipped until the camera
  moves 0.5 m, avoiding constant probe churn; skinned/direct actors (astronauts) are included, with a
  near-camera self actor skipped in first person so the helmet cannot fill the cube. `GpuSceneDraws`/`Culling.slang` gained probe buckets 4/5 and a
  `drawAll` path (no frustum or shadow culling); `ModelPass.RecordProbePrepare`/`RecordProbe` reuse the lean draw
  path without republishing visibility. `FrameConstants.IblProbe` selects the probe cube in `Environment.slang` and
  `Composite.slang`/`Transparent.slang` sample it raw; the probe uses stock-style reflection orientation
  (`+X` mirror for the static skybox, `+Z` correction for the probe cube) so reflections contain scene geometry
  rather than only the background. (Both the `+Z` and later `+X` probe corrections were wrong; the live entry above
  uses the unflipped projection and no axis swizzle.) Verified with the saved viewpoint: the probe contains the ship (debug captures
  `/tmp/vk_probe_px.png`, `/tmp/vk_probe_view.png`), final capture `/tmp/vk_final.png`, and frame rate stays around
  57-65 fps after the cooldown. Build, `ShaderCompiler --check` and `VulkanSmokeTest --shadow` pass. Remaining:
  validate probe orientation/parallax on other surfaces, replace the box-filtered mips with GGX prefiltering if it
  reads too smooth, and add a configurable resolution/quality control if 512² is still too coarse at close range.
  Follow-up: the saved-view comparison showed the probe image upside down, so the probe projection now flips both
  clip axes (180° face rotation) to match cube sampling; the reflection is upright. Movement popping was addressed
  by capturing all faces at one position, skipping captures until the camera moves 0.5 m, and cross-fading the
  previous/active cubes over six frames. Probe resolution was raised to 512² with 10 mips, reflection mip
  selection was biased sharper (`(1-gloss)^2`), and skinned/direct actors are drawn into the probe for astronaut
  reflections (the first-person self actor is skipped when its bounds surround the probe). Verified with
  `/tmp/vk_rot180fix.png`, `/tmp/vk_rot180_mip0.png`, `/tmp/vk_stutterfix.png`, `/tmp/vk_player2b.png` (skinned
  actor captured) and `/tmp/vk_reflect_final2.png`; build/shader/smoke checks pass. Remaining: validate
  third-person astronaut reflection and probe quality against stock on a lit surface. (The one-face-per-frame
  cadence described here was later replaced by whole-cube updates; the 180° image correction described here was
  wrong and is removed in the live entry above.)

- **2026-10-23 (shadow quality distance)** — Replaced the three 2048² cascades with four 4096² cascades and
  stopped using the fixed `1.35 * split` extents: each cascade's light-space extent is now computed from the actual
  camera frustum slice, cutting the default 250 m far extent from ~340 m to the true footprint. The two mid
  cascade ends are capped at 70/300 m so high slider values cannot coarsen the near ship. Added **Shadow distance**
  (50-1000 m, default 250) under Shadows; **Light distance** now only controls dynamic lights. Verified 50 m,
  250 m and 1000 m settings in `/tmp/vk_shadow50.png`, `/tmp/vk_shadows250_final.png` and `/tmp/vk_shadow1000.png`
  with no exceptions. Build, shader check and `VulkanSmokeTest --shadow` pass. Remaining: verify LOD1+ casters,
  contact shadows/SSAO and the dark-side probe.

- **2026-10-22 (reflections, MSAA, glow and independence)** — Moved specular reflection out of the model pass into a
  deferred G-buffer path. The scene now writes a second HDR target (`GBuffer2`: F0 rgb, AO alpha) from
  `Model.slang`, cleared by the skybox; `Composite.slang` reconstructs the position, computes split-sum IBL and
  marches a configurable screen-space reflection (Low/Medium/High = 16/32/48 steps, explicit `hit` output) so
  ship/block geometry replaces the skybox wherever the ray hits. Reflections were the source of the earlier
  white scribbles only in appearance: the real cause was our GGX direct specular. Direct sun and dynamic-light
  specular now use the stock `SpecularPhong`/`FresnelPhong` formulas, and normal maps were restored with the stock
  tangent convention (`B = cross(T, N) * tangent.w`). Added 2x/4x/8x MSAA (restart required, default 4x) for the
  opaque scene: multisampled color/GBuffer/GBuffer2/depth resolve into the existing sampled targets, while OIT stays
  single-sample on the resolved depth. This fixes the jagged small thruster rings and other round block silhouettes
  without the SSAA cost; Supersampling and ModelDetail remain separate quality controls. Bloom now includes the OIT
  color in its bright pass (thruster glow actually blooms), the default bloom is 0.6, and ion point glows get a
  subtle desaturated core (hydrogen stays yellow) to match the stock white-cored blue idle glows. Plugin settings were decoupled from
  the vanilla ones: exposure/eye adaptation, white point and emissive scale use `MyPostprocessSettings.Default`
  (not the current game settings), LOD selection uses `MyGlobalLoddingSettings.Default` (not the live vanilla LOD
  settings), and VSync is a plugin-owned setting; only monitor/output and resolution still come from the game.
  `ReflectionScale` now scales both IBL and SSR. Verified with `/tmp/vk_msaa.png` (4x), `/tmp/vk_bloom.png` /
  `/tmp/vk_bloom1.png`, `/tmp/vk_core.png`, `/tmp/vk_final_idle.png`, `/tmp/vk_line_core.png` (forced plumes) and
  `/tmp/vk_independent.png`. Build, `ShaderCompiler --check` and `VulkanSmokeTest --shadow` pass. Remaining: verify
  glass SSR in a scene with glass and tune the point-glow radius/halo against the stock reference at identical FOV.
  Follow-up in the same session:
  fixed the GPU LOD path to use `MyGlobalLoddingSettings.Default` as well (the culling shader still read the live
  vanilla LOD settings), weighted the SSR hit by reflection strength so low-gloss surfaces keep their IBL instead
  of turning dark, and added a subtle hydrogen core (yellow stays yellow; only ion glows get the stronger white
  core). Final idle capture: `/tmp/vk_refl_final.png`.

  **Reflection regression fix (same session):** the user reported massive white reflection-like structure on
  non-reflective blocks. Isolation captures proved SSR was not involved (SSR hit view was nearly all red; disabling
  SSR, IBL scaling, MSAA, OIT and specular left the ghosting). The actual source was the screen-space
  `emissionBounce` pass: it sampled the bright scene color, so sunlit white hull, trusses and window frames were
  smeared across nearby surfaces as a sharp ghost. It was invisible in the albedo/normal/emissive/dynamic-light
  debug views because those views still receive the composite bounce. The pass and its slider were removed (the
  push-constant slot is now zero) until a correct emissive-only and deliberately blurred implementation exists.
  Verified with `/tmp/vk_fixed.png` (transparent and SSR both active): clean hull and windows, no ghost structure.
  Build is clean.

- **2026-10-17** — Reviewed template; set author/RepoId/FriendlyName/LICENSE; switched to
  net10-only + Vortice.Vulkan; build succeeds. Surveyed game renderer (startup, message pipeline,
  Render11 frame/resources/shaders). Wrote this roadmap.
- **2026-10-17** — Phase 0 implementation. Verified in Pulsar source that `Preloader.Finish()` runs before the game
  starts and that Pulsar's compiler runs no source generators (→ function-pointer interop rule). Studied LinuxCompat:
  SDL3 window stored in `MyWindowsWindows.WindowHandle`, mode changes via `SdlGameWindow.OnModeChanged`, window
  resizes come back through `MySandboxGame.SwitchSettings`. Wrote the hook, config, window bridge, Vulkan
  instance/device/swapchain/frame loop, message processor and `VulkanRender`. Removed the template example patches.
  Added `Tools/VulkanSmokeTest`; it passes on the RX 7900 XTX (RADV).
- **2026-10-17** — Phase 0 verified in game by the user (blue screen, alt-tab, exit). Installed and switched the dev
  setup to the official Pulsar v2.4.3 (`~/.local/share/PulsarRelease`, data in `Legacy/`); fixed
  `<Runtimes>CoreCLR</Runtimes>` and the last publicizer template block. Shader toolchain: tried DXC (Vortice.Dxc broken
  on Linux, then official dxc), switched to Slang at the user's request. Phase 1: GPU resource layer, DDS loader +
  texture cache, bindless sprites, font port, sRGB swapchain, frame capture. The Graphics Options screen renders
  correctly in game (frame capture). Roadmap restructured for hand-off (Start here, how-to, code map, invariants).
  Next: user's interactive menu test; offscreen sprite targets; Vulkan options screen.
- **2026-10-17** — User confirmed menus and the Vulkan settings dialog work. Implemented offscreen sprite targets
  and generated textures (see Phase 1), mip generation, per-format sprite pipelines, safe out-of-frame uploads.
  Main menu verified by capture. Next: user's world load test, then Phase 2.
- **2026-10-17** — User verified the Vulkan settings dialog and a full world load/save/reload cycle (HUD and text
  correct, clean log). Started Phase 2: `SceneTargets` (HDR + reverse-Z depth), `CompositePass`
  (`Post/Composite.slang`), `GraphicsPipelineBuilder`, `TransientAllocator`, `DrawScene` handling. Builds clean, not
  checked in game. The background research on world messages/MWM/materials did not finish before the session ended;
  redo it first (Start here, task 1).
- **2026-10-17** — Docs/status review: roadmap matches the code; `dotnet build` is clean (0 warnings/errors) and the
  shader bytecode check passes. README was still template boilerplate at that point (now project-specific). Recorded the no-subagents / no-guessing /
  in-game-logging rules above.
- **2026-10-17** — Phase 2 first slice. Researched the world message boundary and MWM/material formats from the
  decompiled source and shipped shaders; wrote the [World message spec](#world-message-spec). Implemented
  `CameraState`/`FrameConstants` (camera-relative, Vulkan Y flip, stock infinite reverse-Z projection),
  `SceneActors`, `MwmLoader`/`ModelCache` (background parse via `MyModelImporter`, stock vertex decodes,
  device-local buffers through `UploadQueue.UploadBuffer`), texture kinds, `ModelPass` + `Shaders/Scene/Model.slang`
  (stock material channels/colorize, GGX sun, ambient floor), and cube-grid instancing (`InstanceBuffers`,
  `MyCubeInstanceData` decode in the vertex shader). Verified by frame capture: main menu clean; empty world with
  astronaut and armor blocks; a placed grid captured with 2 instance buffers / 18 instances and no errors. Fixed
  a wrong skinned-model skip (MESH parts of models with blend arrays still render). Known visual gap: sun intensity
  50 has no tonemap/eye adaptation yet, so lit faces clip and unlit faces use only the 0.02 ambient floor; IBL comes
  with Phase 3. Added an explicit ambient floor (game probe `AmbientMinClamp` 0.02) and a placeholder ACES tonemap
  with fixed exposure 0.03 in `CompositePass`; verified by capture (cockpit details and yellow accents read
  correctly, HUD unaffected, log clean). Next: culling/LOD, then G-buffer/motion vectors.
- **2026-10-17** — Fixed monitor selection. The game's `VideoAdapter` (adapter+output index) was only forwarded on
  window-mode changes and LinuxCompat's SDL mode change always used the window's current display, so fullscreen
  opened on the active monitor. `VulkanRender` now forwards adapter changes too; `SdlWindowBridge` finds the SDL
  display matching `MyAdapterInfo.DesktopBounds`/`OutputName` and sets `SDL_WINDOWPOS_CENTERED_DISPLAY` on
  LinuxCompat's SDL thread before the mode change, which makes SDL's pending fullscreen output the selected
  monitor (Wayland refuses to move toplevel windows, but records the pending display). Diagnostic `[Vulkan]`
  lines added. User verified across restarts: the game now remembers and loads only on the selected monitor.
- **2026-10-17** — World correctness pass. `MwmLoader` reads the `LODs` descriptors and resolves them through
  `MyFileSystem` (raw `File.Exists` is case-sensitive on Linux; LOD files were being dropped), `ModelCache` loads
  every LOD into its own GPU buffers, and `ModelPass` selects LODs with the stock `distance/scale *
  LODCoefficient + Add` formula and 10% hysteresis (`MyLodStrategyInfo`). Added infinite-far-plane frustum culling
  (five planes from the stock unflipped view-projection), `MaxViewDistance` culling, `SetInstanceBuffer.LocalAabb`
  as the culling bounds for grids, and a 5-second log summary with draw/cull counts and a LOD histogram. Fixed the
  back-face winding: D3D's default front face is clockwise, and the Y-flipped projection preserves that in final
  screen space, so the model pipelines now use `FrontFace.Clockwise` with back culling; the wrong setting had been
  drawing back faces (holes and dark inverted normals). Also made asset path normalization platform-aware
  (`Path.DirectorySeparatorChar`) instead of assuming `/`. Verified in game: 2-4 LOD models load with no warnings,
  solid blocks, view-dependent culling, clean log. Remaining darkness on unlit faces is the missing IBL/probe.
  During the flight the LOD histogram moved through LOD0..LOD3, confirming LOD switching. A later "everything
  vanished" report was the game's trash collector deleting the unowned grids after the player left
  (`Trash collector removed grid ...` in the game log, physics included), not a renderer fault; the renderer
  removed the actors and instance buffers only after the game's `RemoveRenderObject` messages. Next: G-buffer +
  motion vectors.
- **2026-10-17** — Static IBL and skybox background. Added a cube bindless array (set 0 binding 2, black default
  cube, slot allocation/free), `TextureKind.Skybox`/`Data`, cubemap loading with mips, `Shaders/Common/Environment.slang`
  (shared `FrameConstants`, stock `ApplySkyboxOrientation`, skybox/IBL lookups), `Scene/Skybox.slang` +
  `SkyboxPass` (depth-less background inside the HDR pass), and replaced the model shader's placeholder ambient
  with split-sum IBL: diffuse irradiance from `BackgroundCubeIndirect.dds` (last mip), roughness-mip sky
  reflections with the `ambient_brdf.dds` LUT, and the game's 0.02 probe floor as a minimum. The indirect cubemap is
  resolved next to the game's skybox name (`*Indirect.dds`) with the direct skybox as a fallback. Diagnostics:
  environment texture states and slots are logged when they change. Verified in game: IBL 10 mips + BRDF ready
  within 0.3 s; stars/nebula background and cockpit reflections visible; build clean. Known remaining gaps: glass/
  holo/shield transparency, dynamic lights, dynamic scene probe, eye adaptation. Next: transparency, lights.
- **2026-10-17** — Transparent models (glass/holo/shield). `ModelCache` resolves the MWM part's material through
  the game's `VRageRender.MyTransparentMaterials` (with the `GlassCCW` fallback) and loads the material texture and
  gloss texture. Added `Shaders/Scene/Transparent.slang` (stock glass formula: premultiplied material color,
  texture/gloss, color add, light/ambient factors incl. the x4 glass ambient, IBL reflection + fresnel/reflectivity)
  plus transparent pipelines (premultiplied alpha, depth test read-only, no depth write, front-face clockwise,
  back/no-cull per `TriangleFaceCulling`). `ModelPass` collects transparent parts after the opaque pass and sorts
  them back-to-front by camera-relative distance. Builds clean. In-game log confirms the cockpit glass parts
  resolve and draw (`GlassInside -> GlassInside`, `CockpitGlassOutside -> CockpitGlassOutside`, draw count 35 -> 45,
  no missing-material warnings); **user visual confirmation of the glass itself is still pending** (the capture angle
  makes it hard to judge). Next: verify glass from inside/outside, then dynamic lights.
- **2026-10-17** — GPU skinning, visibility output, cockpit texture changes, automatic tests. Fixed the character
  staying at spawn: the game parents characters to manual cull objects and only updates the parent, so
  `CreateManualCullObject`/`SetParentCullObject` are now handled with a draw-time world-matrix hierarchy. Added
  per-material visibility flags (head materials hidden in first person without hiding the whole actor) and
  `ChangeMaterialTexture` (with generated/offscreen texture lookup) so cockpit/LCD screen materials can swap to
  their render targets. Implemented **GPU skinning**: ModelCache stores a 64-byte skinned vertex stream, ModelPass
  runs a compute pre-pass with a per-actor bone palette (bind-inverse × absolute; 81 matrices) writing the normal
  32-byte static layout, and the existing forward pass draws it. The missing link was visibility output: the game
  calls `Render.Draw()` only for IDs in `MyRenderProxy.VisibleObjectsWrite`, so visible actors with
  `SetVisibilityUpdates` are now published; transforms then arrive. Fixed the compute position transform to add
  the bone translations from row 3 (XNA row-vector). Auto-load for tests: `VULKAN_AUTOLOAD=<seconds>` presses
  Continue Game via a Harmony postfix. Verified: auto-load + frame capture fully automatic; character appears and
  animates in third person; build and shaders clean. Remaining: final pose/head verification, cockpit screen
  verification, dynamic probe/lights, G-buffer.
- **2026-10-17** — More work moved to the GPU. Split the skinning shaders into `Common/SkinningCommon.slang`,
  `Scene/Skinning.slang` and `Scene/BonePalette.slang`: the model-bone palette (bind-inverse × absolute) is now
  assembled by a compute pass instead of the CPU; only skin transforms, per-frame absolute transforms and the
  model→skeleton index map are uploaded. Added the stock `RemoveMetalnessFromColoring` rule unless the effective
  material flags include `RenderFlags.MetalnessColorable` (asset-modifier skins). Verified with an automated
  run: character correctly posed next to the cockpit, 1 skinned actor, no errors, build and shaders clean.
  Remaining GPU moves: GPU-driven culling/LOD with indirect draws and OIT (next, architectural).
- **2026-10-18** — LCD fix (root cause found). `MySessionComponentPanels.IsInRange` needs a panel to stay in the
  texture-memory budget for several ticks; the Linux platform's `GetMemoryBudgetForGeneratedTextures()` returns 0,
  so the budget was empty and every working LCD stayed on the default "Online" texture (no `CreateGeneratedTexture`
  was ever sent). Added `Patches/LcdMemoryBudgetPatch.cs` to set the component's `m_memoryBudget` to 1 GiB. Verified
  in the user's saved cockpit: the large LCD now renders its `DrawSpriteAtlas` grid and "This Is A Test" text, the
  cockpit `ScreenArea`s create 256/512 px generated textures, and `Offscreen` jobs report the expected sprite counts.
  Also removed the temporary out-of-range diagnostic. Build/shaders clean.
- **2026-10-18** — Proper emissive exposure. The user reported battery LEDs visible only in sunlight. Stock's
  `PixelStage.hlsli` adds `albedo * emissive * Post.BloomEmissiveness`; the test world sets `BloomEmissiveness=120`
  with eye adaptation on, `LuminanceExposure=0`, `WhitePoint=11.2`. `Model.slang` now multiplies emissive by that
  world setting (and `(1-metalness)`), and `Post/Exposure.slang` implements the shipped `CalculateExposure` over a
  sparse log-luminance average into a 1x1 R32F texture; the composite applies it with Hable and the world white
  point. A calibration factor (0.025, clamped) compensates for the LDR skybox until full histogram adaptation/bloom.
  Verified with the user's battery camera and `VULKAN_DEBUG_LOOKAT=BatteryLarge`: both faces' green bars stay bright
  on the unlit side; scene remains dark. Also made font loading synchronous and offscreen targets retry when a
  generated texture is not yet created. LCD repro remains blocked: the saved world's cockpit screens log
  `Screen texture ... -> offline` and produce no generated textures. Build/shaders clean.
- **2026-10-17** — OIT, dynamic lights, runtime models, texture unloading. Transparent parts now render into
  RGBA16F accumulation + R16F revealage targets through a dedicated pass (depth load/read-only, no writes) using
  weighted-blended OIT; the composite resolves them over the HDR scene, so the CPU back-to-front sort is gone.
  Added `CreateRenderLight`/`UpdateRenderLight`: a per-frame camera-relative `GpuLight` buffer (max 64) in
  `FrameConstants`, stock falloff/attenuation/spot-cone math, and point/spot diffuse+GGX specular in `Model.slang`;
  the cockpit light housing now shows a clear highlight. Implemented `AddRuntimeModel` (builds a one-LOD
  `GpuModel` from `MyModelData`, packs normals/tangents, resolves material names through a model-material table,
  supports the `ReplacedModel` alias) and `UnloadTextures`/`DeprioritizeTextures` (free slots/images, discard
  in-flight decodes). Verified by automated capture: scene still renders, OIT glass path clean, 12 draw calls,
  36 GPU parts, new light highlight visible, no errors. Build and shaders clean. Next: persistent scene data +
  GPU LOD.
- **2026-10-17** — GPU-driven opaque culling and indirect draws. Added `Scene/Culling.slang`,
  `Scene/GpuSceneDraws.cs` and indirect support to `Scene/Model.slang`: simple actors (no instances, no static
  groups, not skinned) are written to a per-frame host-visible `CullInput` buffer, a compute pass frustum- and
  view-distance-culls them and atomically appends commands + 184 B draw data into two buckets (cull/no-cull), and
  the CPU submits two `vkCmdDrawIndirectCount` calls. The command/input carry the instance count and the draw data the instance buffer address/range, so cube instances share the same path. Draws are non-indexed (`vertexCount = indexCount`) and the
  vertex shader fetches the real index through the draw data's buffer device address, so no global index buffer is
  needed; `SV_DrawIndex` + a flat varying select the draw data in both stages, and `push.drawData == 0` keeps the
  direct path for skinned/static-group actors. Actors without static-group matrices (including instanced
  cubes) are now fully recorded in `RecordPrepare`
  (opaque -> GPU buckets, transparent -> OIT list) and skipped by `Record`, removing the duplicate per-frame walk;
  the scene log now prints the GPU part count. Instanced cube actors were added to the same path (commands carry
  instance counts, draw data carries the instance pointer/range); the cockpit scene has no visible instanced
  opaque parts to exercise it. Verified by automated capture: cockpit renders identically, 36 GPU parts, 12 total
  draw calls (2 indirect), transparent cockpit glass still correct, no errors. LOD selection and material packing
  remain CPU-side; next is persistent scene data + GPU LOD.
- **2026-10-17** — Asset preloading and buffer-strategy review. `PreloadTextures` (mapped to the correct sampling
  kind via `TextureType`), `PreloadModel(s)` and `PreloadMaterials` now start background decodes/parses during the
  loading screen, so GPU buffers/images are created before the world becomes interactive; model texture
  dependencies already start when a model is parsed. Verified: the test world requested one preload (skybox),
  which loaded; no errors. Reviewed GPU buffer use: no UBOs by design (push constants + host-visible transient
  buffers read via BDAs), persistent device-local model/index/instance/texture buffers, and compute BDA buffers
  for skinning/palette. Storage images/RW buffers and indirect/acceleration structures remain for GPU-driven
  culling, OIT and GI/RT. Remaining: unload/deprioritize messages, `AddRuntimeModel` (mod runtime meshes).
- **2026-10-18** — LCD offscreen content and live lighting tuning. Root cause of the missing background sprite: the
  game's offscreen job was rendered in the same frame its `DrawSpriteAtlas` texture was first decoded, but our transfer
  uploads are recorded before `RecordOffscreen`; the sprite sampled the not-yet-uploaded image once and the target was
  never redrawn. Offscreen jobs now warm every sampled texture and defer rendering by one frame
  (`GuiRenderer.WarmJobTextures`), so DangerZone/text/font pages are uploaded before the target is rendered. Also fixed
  the `Screen` material flag mismatch (C# 512 vs shader 256): generated LCD targets now force metalness 0 and use the
  ScreenArea/CockpitScreen add-map emissive. Debug dump/target/quads removed. Auto-exposure no longer uses the 0.025
  calibration (it made the whole world ~40x too dark); it uses the shipped `CalculateExposure` result with a
  user-tunable multiplier. Added "World lighting tuning" sliders to the plugin settings (Options > Graphics > Vulkan
  Renderer): Exposure, Sun, Ambient, Specular, Reflections, Metalness, Emissive, Screen and Dynamic lights, applied
  live through `FrameConstants.tuning0/tuning1` and the exposure push constants (since superseded by the persistent
  GPU settings buffer). Initial defaults: Exposure 0.25, all others 1. Verified LCD shows the DangerZone background + text, battery LEDs stay bright, and the world is no longer crushed
  to black. Next: collect the user's slider values to correct the stock shader math instead of compensating globally.
- **2026-10-18** — Block paints, skins and exposure. First fixed the wash-out: our full-strength GGX sun
  specular and skybox IBL specular were added on top of stock's forward-style Lambert shading
  (`MainDirectionalLightEnv` has neither), so specular/reflection default to 0 (still tunable).
  Exposure was then found to make the remaining albedo saturate; the old 0.025 calibration was removed
  in favour of the shipped `CalculateExposure` plus a user multiplier. The user's battery still looked
  grey, but the save's `ColorMaskHSV` was not "default grey": it is a delta. Stock `ColorizeGray` adds
  `TextureDebugMultipliers.ColorizeHSV` default `(0, 0.8, -0.1)`; we had omitted it, so the applied
  green collapsed to grey. Added the stock conversion. Also added pending pre-create state for
  `UpdateRenderEntity` and `ChangeMaterialTexture`, so paints/skins arriving before
  `CreateRenderEntity` are not dropped. Verified by capture (saved green battery/cockpit/grid with the
  CarbonFibre skin pattern) and by the user changing a battery to red in game. Temporary texture/model
  change logging removed. Next: persistent scene/part data + GPU LOD.
- **2026-10-18** — Persistent GPU scene data and GPU LOD. Opaque non-skinned actors are now cached as
  persistent `GpuScenePart` records (material, vertex/index BDAs, index range) with one `GpuLodRange` per
  LOD and a persistent per-actor LOD-state buffer; the cache is rebuilt only when `SceneActors.Revision`
  or `ModelCache.Version` changes. Per frame the CPU uploads only actor world/colour/bounds/instance
  state. `Scene/Culling.slang` now runs one thread per actor, selects the LOD with the stock distance
  formula and 10% hysteresis on the GPU, culls, and appends one draw per part. Static groups are expanded
  into one actor record per local matrix sharing the cached ranges; instanced cubes use the same path.
  Wire-format structs and the persistent buffers are documented in `GpuSceneDraws.cs`. Verified by
  automated capture in the user's world: 297 actors, 80 instanced, 3152 cached parts, 13 total draw calls
  (2 indirect), red/green paints and CarbonFibre skin still correct, no log errors. Remaining: per-frame
  actor-state dirty tracking and GPU buckets for the skinned/transparent paths (bloom followed in the next entry).
- **2026-10-18** — First HDR bloom pass. Added `Post/Bloom.slang` (bright pass with soft knee,
  separable 9-tap Gaussian) and `BloomPass` (half-resolution RGBA16F ping-pong targets with bindless slots,
  resize handling, render-graph barriers). `Composite.slang` adds the blurred bright pass before exposure/
  tonemapping, exposed as the new `BloomScale` slider. Verified by capture in the user's world: indicator
  LEDs, cockpit emissive bars and the LCD now have a soft halo; red/green paints and skin remain correct,
  build/shaders clean, no log errors. Full stock multi-scale bloom filtering remains a refinement.
- **2026-10-18** — Settings UI polish. `BloomScale` now defaults to 0.2. Float sliders display four decimal places
  (the old one-decimal label rounded small steps to the same text), and every slider row has a **Reset** button that
  restores that property's value from a pristine `Config.Default`. `ConfigStorage` no longer returns the
  `Config.Default` singleton for missing/invalid files, so edits/resets cannot mutate the baseline. Reset buttons use
  the `RectangularBorderLess` style and are sized to the measured width of "Reset" plus padding (explicit centre
  alignment), so the default button minimum width does not apply. Verified in the settings screen by the user; live
  config values are unchanged until the user applies/saves.
- **2026-10-18** — User settings moved to GPU-resident storage. The exposure/bloom and all eight lighting sliders
  now live in a persistent one-element `GpuRenderSettings` storage buffer (set 0 binding 3) created once per
  device. `Config.PropertyChanged` sets a dirty flag; the render thread uploads the 48-byte block only when it
  changes, and `Model.slang`, `Exposure.slang` and `Composite.slang` read `renderSettings[0]` directly. Removed the
  per-frame `Config.Current` reads and the tuning fields/push constants from `FrameConstants`, exposure and
  composite. Verified by capture: red/green paints, skin, LCD and bloom still render; no log errors.
- **2026-10-18** — Final validation before hand-off: removed the temporary settings-screen test hook that caused
  Pulsar's compiler to fail, confirmed the reset buttons use the measured `RectangularBorderLess` style, and ran
  `dotnet build` plus `ShaderCompiler --check` (both clean). No git operations performed.
- **2026-10-18** — Cockpit normals/decals/lighting investigation (hand-off). The user supplied a stock
  DirectX11 reference (`/tmp/ai-chat-custom-attachment-temp-file-581decc5-d05d-4d2c-92d6-2de99da928b3-4454473671927966600.png`)
  showing the Respawn Pod/cockpit with uniform carbon shading, strong weave normals, crisp decals and brighter
  specular/reflection. The cockpit front still has a hard vertical seam down the centre. Confirmed by debug
  captures that albedo and base normals are uniform across the front; the seam is in normal-map application.
  Removed the temporary `CockpitSmall` `CarbonFibre_ng` override (wrong texture; stock uses
  `Skins/<name>/cockpit_small_ng.DDS`) and all `Pad0`/`Pad1`/UV*4 debug code. `ResolveTextures` now uses the real
  `ChangeMaterialTexture` entries. `Model.slang` now uses stock derivative tangents
  (`pixelTangentSpace`/`NormalBuildTangent`, x flip) and `ToksvigGloss`, plus stock
  `SurfaceAlbedo = baseColor * (1 - metalness)` for diffuse/ambient/emissive. Defaults changed to
  `ExposureScale=0.5`, `SpecularScale=1`, `ReflectionScale=1` in both `Config.cs` and the live
  `~/.config/SpaceEngineers/Storage/Vulkan.cfg`. Build and shaders are clean. **Not verified**: the last
  derivative capture (`/tmp/vk_deriv.png`) loaded a different 9-actor scene with the front in shadow; previous
  captures (`/tmp/vk_quality1.png`, `/tmp/vk_quality2.png`) still show the seam and are overbright/washed out
  versus the stock reference. Re-verify with the user's full scene/camera before judging the derivative path.
  Also discovered that `ModelPass.IsOpaque` skips `DECAL_CUTOUT`, so cockpit `Paneling*`/`Decals` parts do not
  render. Next session: verify/fix the derivative tangent seam first, then
  render `DECAL_CUTOUT` alpha-masked parts, then calibrate exposure/specular/reflection against the stock image.
  Final state: `dotnet build Vulkan.sln -c Debug` 0 warnings/0 errors and `ShaderCompiler --check` reports
  up to date. No git operations performed.
- **2026-10-18** — `DECAL_CUTOUT` decals and seam check. Added `MyMeshDrawTechnique.DECAL_CUTOUT` to
  `ModelPass.IsOpaque`, marked it `AlphaMasked` and added `MaterialFlags.Decal`; `Model.slang` now applies the
  stock `+0.0001` clip-space depth nudge for decal parts, so they win the depth test against the surface instead
  of z-fighting. Because they are ordinary cached `GpuScenePart`s, the GPU-driven LOD/culling/indirect path
  renders them with no special-case draw. Verified by automated capture in the user's world
  (`/tmp/vk_decals.png`): battery front panel outline/screws, respawn pod light strips/vents, and the battery
  cylinder's `DANGER HIGH VOLTAGE` decal and grille are now visible; 9 actors, 162 GPU parts, 12 draws, no
  errors. The derivative-tangent centre seam is no longer visible on the battery front (pod front still in
  shadow). Noted the user's new stock reference; red battery is intentional. Shader bytecode regenerated;
  `dotnet build Vulkan.sln -c Debug` 0 warnings/0 errors. Next: exposure/specular/reflection calibration and
  the dynamic scene probe. No git operations performed.
- **2026-10-18** — Blueprint paste crash, cube instance transforms, armor plates, character animation, decals.
  Fixed a segfault when pasting a large blueprint: `InstanceBuffers.Replace` disposed the previous buffer while
  its upload was still queued, so `vkCmdCopyBuffer` received a null destination. `GpuBuffer`/`GpuImage` now
  expose `IsDisposed`, and `UploadQueue` drops queued copies to replaced destinations (logged). Root cause found
  from a .NET crash dump (`vkCmdCopyBuffer` in `UploadQueue.RecordBuffers`; first pending upload's destination
  had `Handle = 0`). Verified: the blueprint paste completes; 10 intermediate instance uploads are dropped; no
  crash.
  Fixed the cube-instance **translation** in `Shaders/Scene/Model.slang`: the packed `translationAndRot.xyz` was
  never added after the rotation, so every block instance rendered at the actor/grid origin (the cockpit).
  Blocks now stay at their cell. Fixed heavy-armor/panels that only carry blend data: `CreateLod` now always
  builds the static 32 B stream and the 64 B skinned stream when needed; MESH-only skinned models without a
  skeleton use the static stream through the GPU path, while characters deform all their parts with the skinned
  output. Fixed the character animation regression caused by the per-part `MESH` classification (the astronaut's
  parts are `MESH` too); `SetCharacterSkeleton` now bumps the cache revision. `DECAL_CUTOUT` and stock derivative
  tangents are in from the previous entry.
  Fixed transform ordering for large pastes: parent/local/world updates and `SetInstanceBuffer` ranges arriving
  before `CreateRenderEntity` are kept pending and applied at creation; subparts use `LocalMatrix * parent`
  again (world-matrix updates drive the cull-object root/character). Verbose diagnostics (`Transform:`,
  `Create:`, `Parent:`, `SetInstanceBuffer:`, `Cube instances:`, `Skipping instanced actor`) and the
  `VULKAN_NO_CULL_INSTANCES` debug env var were temporary and are now removed. **The two “missing” heavy-armor
  sides were a transposed instance rotation**: `Model.slang` applied the packed cube matrix with
  `dot(position, row)` (matrix transpose) while normals used `mul(normal, matrix)`, so some panels were placed
  on the wrong sides/inside-out and read as transparent holes. Fixed to `mul(position.xyz, instanceRotation) +
  translation`; an automated capture (`VULKAN_AUTOLOAD=15 VULKAN_SCREENSHOT=... VULKAN_SCREENSHOT_DELAY=50`,
  saved camera on the block) now shows a closed six-sided cube (`eff=6 bufCount=6`). Bucket/instance data was
  correct all along (`start 0`, counts 6/10/18 as the grid changes). All temporary `Transform:`/
  `SetInstanceBuffer:`/`Create:`/`Cube instances:` diagnostics and the no-cull env var are removed; build and
  shaders clean. Next: lighting calibration and the dynamic environment probe. No git operations performed.
- **2026-10-18** — GPU text verified, diagnostics overlay, test notification. Fixed `GpuTextRenderer.CreateFont`
  throwing `KeyNotFoundException` on the second font creation with the same id (`fonts[id]?.Dispose()` used the
  dictionary indexer); it now uses `TryGetValue`. With that fixed, fonts upload their glyph tables and the GPU
  text path is live: the automated capture overlay reports `GPU text 48 runs 246 glyphs`, and the long-duration
  notification renders correctly. Added `DiagnosticsOverlay` (top-left panel: FPS, frame time, GPU
  triangle/part/instance/cull counters, direct triangles, GPU text run/glyph counts, and an initially
  120-frame frame-time graph; later extended to 60 s) drawn into the main sprite batch with the debug font. `VulkanRender` now keeps a smoothed frame
  time/FPS and passes the GPU counters to `GuiRenderer`; `Config.Diagnostics` defaults to `true` for testing.
  Added `Testing/TestNotification.cs`: `VULKAN_NOTIFICATION=<text>` (optional
  `VULKAN_NOTIFICATION_MS`, default 60000) shows a notification via
  `Sandbox.ModAPI.MyAPIGateway.Utilities.ShowNotification` on the game thread once a scene is drawn, for
  screenshot tests. Verified with `VULKAN_AUTOLOAD=15 VULKAN_NOTIFICATION=... VULKAN_NOTIFICATION_MS=120000`
  and `VULKAN_SCREENSHOT=/tmp/vk_final.png`; build clean, no font-load errors. No git operations performed.
- **2026-10-18** — Diagnostics history extended to 60 seconds. The overlay now accumulates raw frame time into
  0.5 s buckets (120 bars) instead of 120 individual frames, shows the 60 s average and max, and draws the
  bucket maximum so short hitches remain visible. Verified with a 75 s automated capture
  (`/tmp/vk_60s.png`): graph covers the full minute; build clean.
- **2026-10-18** — Diagnostics max is a true rolling 60 s max. History entries now carry timestamps;
  `AverageWindow`/`MaxWindow` ignore buckets older than 60 s, so an old startup/loading hitch expires even if
  no new frames were drawn. Label now reads `60s avg ... 60s max ...`. Verified at 130 s
  (`/tmp/vk_120s.png`): startup 228 ms spike gone, max 10.19 ms. Build clean.
- **2026-10-18** — Diagnostics now shows device-local GPU memory: `GpuMemory` tracks
  `DeviceLocalAllocatedBytes`/`DeviceLocalTotalBytes` by memory heap, and the overlay prints
  `GPU mem X / Y  Z%` (e.g. `2.1 GB / 24.0 GB  9%`) using the same allocation blocks that back buffers and
  images. Verified in `/tmp/vk_mem.png`. At this point the overlay still drew its own text through the CPU
  `RenderFont` path; the next entry moves it onto the GPU text path. HUD/chat/notification text on the main
  target already used `GpuTextRenderer` (GPU layout + glyph instances), while offscreen LCD text keeps the
  CPU fallback. Build clean.
- **2026-10-18** — Diagnostics text moved onto the GPU text path. `DiagnosticsOverlay` now queues its lines
  through `GpuTextRenderer.AddString` (debug font, CPU glyph fallback only if the GPU font is unavailable);
  panel/graph stay sprites. `SetDiagnostics` runs before `RecordTextLayout` so overlay runs are included in the
  same compute dispatch. Verified: the overlay now reports `GPU text 55 runs 469 glyphs` and renders through the
  GPU path. Added `FrameLimitKind` (`Unlimited`, `Fps30`, `Fps60`, `Fps120`, `Fps240`) to `Config`, applied by
  `VulkanRender.LimitFrameRate` (sleep + short spin before frame acquisition). Verified `Fps60` gives
  `60.0 fps / 16.67 ms`. The observed 120 fps cap is the game's VSync/FIFO at the 120 Hz monitor refresh; the
  limiter can only cap lower, so 240/unlimited needs VSync disabled in the game's video settings. Build clean.
- **2026-10-18** — Frame time in diagnostics now measures actual frame generation work, not the capped frame
  interval. `VulkanRender.Draw` starts the timer after `TryBeginFrame` (and therefore after the limiter sleep
  and fence wait) and stops after `EndFrame`; `FrameMilliseconds`/`RawFrameMilliseconds` and the 60 s graph use
  this work time. Verified at `Fps30`: `30.0 fps  frame 0.69 ms`, graph mostly green, while the old metric
  reported the expected `33.33 ms` interval and mis-colored it red. The loading hitches still appear as red
  60 s-max spikes until they age out. Build clean.
- **2026-10-19** — Async asset uploads and load-stall fixes. The UI freeze during large grid loads was a render-thread
  stall (the game kept simulating). Added `VulkanDevice` selection of a dedicated transfer queue family (RADV family 1;
  falls back to the graphics queue), `TransferSystem` with a timeline semaphore, per-frame staging rings, one submit per
  frame and `FrameLoop.EndFrame` waiting on the batch value; resources use concurrent sharing when the families differ.
  Model GPU building (vertex/skinned/index packing, buffer creation, worker staging) moved to up to three worker threads;
  the render thread publishes results and submits their copies. `TextureCache.ProcessDecoded` is budgeted at 4 ms/frame.
  `InstanceBuffers` now coalesces updates (`AddRef` + newest message) and uploads once per buffer per frame, and
  `SetInstanceBuffer` only bumps the cache revision when the `HasInstances` state changes. `ModelPass.EnsureCache` waits
  for model uploads to settle and enforces a 0.75 s minimum interval (1.5 s max deferral), removing the rebuild storm.
  Removed the per-`ChangeMaterialTexture` logging that wrote hundreds of thousands of lines on a large paste.
  Verified with the user's world via `VULKAN_AUTOLOAD`: 9,728 actors / 130,779 GPU parts / 72,032 instances render
  correctly; dedicated transfer queue logged, no errors, 60 s frame max ~50 ms (load rebuilds are now 4 instead of a
  storm). Build and `ShaderCompiler --check` clean. Remaining hiccup: the full part-cache rebuild (~50-123 ms); next is
  incremental append/grow. No git operations performed.
- **2026-10-19** — Culling-diagnostics clarification. The overlay's `Drawn ... culled ...` line reports only the **CPU
  direct path** (`lastCulledActors`), which is 0 when nearly everything goes through the GPU-driven buckets; it did not
  mean culling was broken. Added `GpuVisible` to the stats and relabelled the overlay (`Direct draws ...` / `GPU culled
  ... visible ...`), and the scene log now prints `gpu culled/visible` next to the direct-path counts. Verified with the
  debug camera: 192 actors GPU-culled, 6 visible; in the large-grid scene 5,601 of ~9,700 were GPU-culled. Temporary
  shader debug writes were removed and the bytecode regenerated. Build and shader check clean. No git operations.
- **2026-10-19** — CPU frame-time cut by moving per-actor state to a cached/GPU-friendly path. Profiling showed `prepare`
  at ~5.4 ms and two full `RecordSkinning` scans at ~0.8 ms for a 9.1k-actor grid (frame ~8.9 ms). Added persistent
  absolute `GpuSceneActor` records built once per cache rebuild; `SceneActors` now tracks dirty actors (world/local AABB,
  visibility, instance range), marks a parent's whole subtree via a children map, caches resolved world matrices by
  `transformRevision`, and keeps small `Skinned`/`VisibilityUpdates` lists. `GpuSceneDraws`/`Culling.slang` receive the
  camera position and subtract it for distance/frustum/draw data, so camera movement no longer rebuilds anything. The
  per-frame path now only refreshes dirty records, processes transparent parts, and copies the ~1.8 MB actor array;
  `RecordSkinning` and the direct path iterate small lists instead of all actors. Removed the misleading stale LOD
  histogram from the scene log. Measured in the same large scene: frame ~1.8 ms (overlay), prepare 0.2-1.0 ms, skin
  ~0.01 ms, GPU culling 3.2k culled / 5.8k visible; visuals verified by capture. Build and shaders clean. Next, if moving
  grids still mark many actors dirty, move local/parent transform composition into the culling shader and refresh only
  instance-buffer users. No git operations performed.
- **2026-10-19** — Incremental cache and texture-readiness fix (regression after the per-actor state change). `ChangeModel` no
  longer triggered any cache update, so submodels (turret guns, doors, hangar doors) disappeared; removals also left stale
  records. Added `CreatedActors`/`RemovedActorIds`/`RecreatedActors`: new actors append their parts/ranges/state, removals mark
  cached records dead (O(1)), and model/skeleton changes re-append the actor. Material properties (colour/emissive/flags/
  texture changes) now refresh only the affected actor's part records; part/range/LOD-state data lives in per-frame-slot
  buffers and only the current slot is written. Work is budgeted (create 4 ms, recreate 3 ms, material 2 ms, state 2 ms).
  The missing skins/textures were caused by part records storing bindless slot indices built before async textures finished:
  with no more full rebuilds they kept the placeholder. `TextureCache.ReadyGeneration` now triggers a budgeted all-material
  refresh whenever new textures become ready (single pass at a time; new arrivals start another pass after it completes).
  Verified in the user's world: skins, decals, doors/turrets and other submodels render; frame ~1.5-2.2 ms with prepare
  0.15-2.2 ms, and no recurring post-load hitches (the 60 s window only contains the load spike until it ages out). Build and
  shaders clean. Follow-up: compact dead cached part records, and budget/throttle message bursts during loads. No git.
- **2026-10-19** — Two-sided per-block lighting fix. Diagnosed the hangar-door symptom with per-actor normals,
  camera/sun dot products and direct/emissive/albedo debug views: the door has no emissive contribution and its
  direct term was strong because the visible back face used the authored front normal. A CCW front-face experiment
  made the door dark but inverted every other door face and was reverted to the D3D-matching `Clockwise`. The shader
  now flips only normals facing away from the camera after normal mapping, so the visible side of open panels gets
  the sun/ambient from the side the player sees; correctly authored geometry is unchanged. An IBL diffuse ambient
  attempt was tested and reverted (washed the scene white); the 0.02 forward ambient floor stays. Verified by
  `/tmp/vk_fixed_dark_direct.png` (direct-only shadow-side door black), `/tmp/vk_fixed_sun_side.png` (sun-side door
  red and lit) and `/tmp/vk_lighting_final.png` (player view, ~2.9 ms CPU / 4.7 ms GPU). Removed the temporary
  `DominantNormal`/`LightingDiagnostics` instrumentation and `VULKAN_FLIP_MODEL_NORMALS`/`VULKAN_FLIP_SUN` flags.
  Build and `ShaderCompiler --check` clean. No shadow work, no git operations.
- **2026-10-20** — Shadow validation started. Added `VulkanSmokeTest --shadow`, which builds the exact
  depth-only model pipeline used by the shadow pass; it passes on RADV (the Khronos validation layer is not
  installed on this machine). The in-game pass now runs with `VULKAN_SHADOWS=1` without the earlier GPU reset
  (the standalone module/color-attachment defects are fixed). Corrected the reverse-Z comparison from
  `stored >= ndc.z - bias` (opposite direction) to `ndc.z >= stored - bias`. Frame constants, the bindless
  slot and depth samples reach the shader, but cast shadows were not visually confirmed on the turret/hangar
  captures, so the pass remains gated and off by default. Removed the temporary debug views and frame logs.
  Build and shader check clean. Next: read back/dump the shadow map, then verify the light-space projection
  and caster depths. No git operations.
- **2026-10-20 (continued)** — Sun shadow validation complete on the user's test platform. Added `Model.slang`
  `shadowFragment` (alpha-masked casters), back-face culling for the shadow pass, a 100 m map extent, 12-tap
  Poisson soft PCF (3-texel radius), slope-scaled bias and `ShadowPassRecorded` gating; exposed an opt-in
  **Shadows** checkbox (restart required) with the `VULKAN_SHADOWS=1` test override. The shadow-factor view shows
  the platform shadow at the blue pillar's foot is correct but the pillar's visible face is outside the red
  pillar's cast-shadow volume; the vertical junction needs contact shadows/SSAO, not a bias change. Verified
  on/off (`/tmp/vk_test_shadow_on.png`, `/tmp/vk_test_shadow_off.png`), soft
  PCF (`/tmp/vk_shadow_soft2.png`), shadow factor (`/tmp/vk_shadow_factor2.png`) and a 60 m sun-side view
  (`/tmp/vk_shadow_side_sun3.png`); added raw-map debug view 9 and `VULKAN_DEBUG_DISTANCE`/`TOP` camera options.
  Removed the `CacheDiagnostics` dump, the skin-ready log and the cockpit-part dump. Build, shader check and
  `VulkanSmokeTest --shadow` pass. Remaining: cascades/PCSS, contact shadows/SSAO for vertical junctions, texel
  snapping, skinned/transparent casters and a quality slider; RT shadows later. No git operations.

- **2026-10-21** — Shadows reworked after the user's close-up feedback (blocky, LOD pop, dither, false-looking
  edges). Replaced the single 100 m map with **three cascaded 2048² maps** (15/40/150 m splits; initially 15/40/110 m
  light-space extents, widened to 23/55/200 m in the cascade-coverage fix below) and texel-snapped cascade centres. Added **separate LOD0 shadow caster buckets** to
  `GpuSceneDraws`/`Culling.slang`, so the forward pass can switch to LOD1 without losing the shadow silhouette.
  Replaced the 12-tap fixed PCF with a **16-tap Poisson PCF using bilinear depth**, cascade-scaled width
  (4/3.2/2.4 texels) and a stable per-cascade rotation; the earlier PCSS blocker search and per-pixel rotation
  were removed because without TAA they produced speckled/static edges. Shadow offset/bias use the
  **camera-oriented face normal**, not the normal-mapped or vertex normal. Verified the close-up shadow factor is smooth
  (`/tmp/vk_geomnormal_factor.png`) and captured cascaded close-up (`/tmp/vk_twosided_geom.png`) and 60 m views
  (`/tmp/vk_lod60*.png`). The noisy dark edge on the large red panel was initially the **two-sided front/back decision** using the
  normal-mapped normal, which flickered at grazing angles; that side decision now uses the face normal. The
  remaining diagonal across the long blocks was cascade coverage (see the cascade-coverage entry below). Also
  fixed `VULKAN_SHADOWS=0` so it actually forces shadows off for tests (previously only `1` overrode the setting),
  and added `VULKAN_NO_TRANSPARENT=1` for isolating transparent geometry. The dark side still needs the dynamic
  probe. Investigated the remaining diagonal line on the user's panel: it survives shadows-off, transparent-off,
  no-cull forward, LOD0 and per-vertex tangents, and an ambient floor turns it dark red (so it is diffuse). Root
  cause: the model's vertex normals point away across the panel bevel. Diffuse now uses the triangle face normal
  (camera-oriented for two-sided panels); the normal map was later reduced to gloss only. The sunlit diamond is
  gone in `/tmp/vk_fixed.png`. Reverted the no-cull/ambient/LOD experiments. Build, shader check and
  `VulkanSmokeTest --shadow` pass. Remaining: verify LOD1+ on the user's ship, contact shadows/SSAO, near-cascade
  tuning, the terminator/ambient probe, skinned/transparent casters and a quality slider. No git operations.

- **2026-10-21 (continued)** — Finished the red-panel diamond. The face-normal diffuse fix removed the sunlit
  diamond, but the user still saw a diagonal on flat panels and lines inside shadows. Two remaining paths used the
  bad model normal: **specular/ambient-specular** through the normal map, and the **shadow normal offset/bias**.
  The shader now sets `n = faceNormal` for all lighting (the normal map only provides Toksvig gloss), and the
  shadow lookup offsets/biases along the camera-oriented face normal instead of the vertex normal. The old
  two-sided `geometricNormal` flip is gone because the face normal is already camera-oriented. Added an
  `albedo * 0.25 * AmbientScale` ambient floor as a stand-in for the missing dynamic probe, which also makes the
  **Ambient** slider effective (it previously multiplied a zero ambient). Verified at the user's camera:
  `/tmp/vk_faceall.png`, `/tmp/vk_ambient25.png`, `/tmp/vk_shadowface.png` (shadows on) and
  `/tmp/vk_shadowface_off.png` (shadows off). Build, shader check and `VulkanSmokeTest --shadow` pass. Remaining:
  replace the floor with the real dynamic probe, contact shadows/SSAO, LOD1+ verification and a shadow quality
  slider. No git operations.

- **2026-10-21 (logging cleanup)** — Removed routine log spam: the 5 s `Scene:` and `CPU ms` summaries, per-model
  `Model loaded` / `Runtime model added`, per-generated-texture lines, per-actor skinning and no-skeleton lines,
  material-refresh and missing-ColorMetal lines, character skeleton/transform lines and preload counts. Also
  removed the per-run `UnloadTextures`, unimplemented voxel-message, LCD-budget, first camera/environment/
  postprocess/environment-texture and swapchain lines. Warnings and errors remain, including frame-hitch, slow
  part-cache rebuild, slow texture upload and failed-load reports; the diagnostics overlay carries the frame/CPU/
  LOD data that the removed periodic lines duplicated. Build is clean and autoload runs produced no
  removed-category lines. The `ntsync`/`LinuxCompat` lines in the game log come from the platform, not the renderer.

- **2026-10-21 (distance, emissive and quality settings)** — Added user controls for the two remaining visual limits:
  **Light/shadow distance** (150-1000 m, default 150) keeps the near shadow cascade fixed and scales the mid/far
  cascades and dynamic-light culling distance; optional **Supersampling** (1.0-2.0, default 1.0) renders the 3D scene
  at a higher internal resolution. Renamed the existing emissive slider to **Emissive strength** and raised its range
  to 8. Verified 1.5x supersampling and a 1000 m light/shadow distance in `/tmp/vk_quality_test.png` with no errors;
  config was restored afterward. Build and shader check pass.

- **2026-10-22 (thruster/particle billboards)** — Added the missing effects path for thrusters and particles. The
  game's `MyThrustFlameAnimator` normally runs inside the stock actor scene; the renderer now consumes its
  `UpdateRenderComponent` `FlameData`/`FloatData` messages and rebuilds the stock line/point flame billboards each
  frame using the actor world matrix, grid scale, flame radii and interpolated strength. A new billboard pass consumes
  `SharedData.Billboards` and draws standard billboards into the existing OIT targets, with file textures, single-
  channel BC4/R8 particle textures, material UVs, colour/intensity and alpha cutout. The flame animator now matches
  the stock idle behaviour too: at strength 0 the line plume is omitted but the point glow remains, and the line
  plume appears only above zero strength. Verified with idle hydrogen/ion thrusters in `/tmp/vk_effects_idle2.png`
  and forced-active plumes in `/tmp/vk_effects_force.png`. Additive billboards/glares, GPU particles and thruster
  point-light emission remain. Build, shader check and `VulkanSmokeTest --shadow` pass.
- **2026-10-21 (FXAA and anti-aliasing)** — Stock Space Engineers only offers **FXAA**, not MSAA or supersampling, and
  native 4K still aliases geometric edges. Implemented FXAA 3.11 after tonemapping, matching the stock
  `Postprocess/Fxaa.hlsl` chain, and made it follow the game's Graphics > Anti-aliasing setting. The scene is
  tonemapped into an sRGB LDR target, FXAA resolves that to the backbuffer, and the GUI draws afterward. Also fixed
  the optional supersampling path: the composite had been point-sampling the high-resolution scene, so values above
  1.0 produced no averaging; it now uses linear sampling. Renamed the control to **Supersampling** and moved it under
  Advanced. Added a Low/Medium/High/Very high FXAA quality slider: Medium is the stock preset 29, High is preset 39,
  and Very high extends the search to 20 steps with lower edge thresholds. Medium is the default so the default
  matches stock. Verified native 4K with `/tmp/vk_fxaa_native.png` against FXAA-off `/tmp/vk_noaa_native.png`; game
  config was restored afterward. Build and shader check pass.

- **2026-10-21 (dynamic lights and colors)** — Fixed two user-visible gaps against vanilla. Gray armor was rendering
  red because `colorizeGray` was a non-stock HSV variant; it now uses the shipped `ColorizeGray` path (sRGB round-
  trip, hue zeroed, mask added, then lerped by the extensions coloring factor), so gray/yellow/red match stock.
  Dynamic lights were using the message's block-local `Position`/direction directly and therefore landed far from
  the camera. `SetParentCullObject` is now recorded for light IDs, the parent cull object's world matrix transforms
  positions and directions, `PointLightOffset` is applied along the light direction, and the nearest 64 point/spot
  lights are uploaded. The shader now honors each light's color/intensity, range, falloff, gloss/diffuse factors and
  spot aperture (hard stock cone clip), uses the gloss factor in roughness, applies AO, and keeps diffuse attenuation
  separate from specular. Verified the room is lit with correct colors in `/tmp/vk_lights_parent.png` (stock colors
  in `/tmp/vk_color_stockfix.png`); build is clean. Remaining: stock tiled light culling, spot shadows/glare and
  per-light shadow support.

- **2026-10-21 (cascade coverage)** — The remaining diagonal/diamond that cut across the long red blocks was the
  **shadow cascade coverage**, not the hull shadow: the 15/40/110 m light-space extents were smaller than the camera
  frustum slice at the 15/40/150 m splits, so the shadow map's UV border returned lit on part of the receiver.
  Widened the extents to **23/55/200 m** (near ~2.2 cm/texel, mid ~5.4 cm, far ~19.5 cm). The shadow now continues
  across each split and the diagonal/diamond is gone in `/tmp/vk_wide.png` and `/tmp/vk_wide_restored.png` (same
  camera as `/tmp/vk_new.png`). Build, shader check and `VulkanSmokeTest --shadow` pass. Remaining unchanged.
