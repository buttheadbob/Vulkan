# Vulkan Renderer — Roadmap

> **Living document.** Every session working on this project must read this file first and
> update it before finishing: tick completed items, adjust the plan, keep [Start here](#start-here)
> current, and add an entry to the [Session log](#session-log). If a decision changes, update the
> [Decisions](#decisions) table rather than leaving contradictory notes.

## Start here

**Current state (2026-10-18 session):** Phase 0 and Phase 1 done (menus, HUD, text, Vulkan settings dialog all
verified by the user; world load/save/reload works). Phase 2 first slice is implemented, builds clean and is
**verified in game by frame capture**: camera + environment state, actor registry, background MWM loader (LOD0),
texture kinds (model CM/Extensions sRGB, normal maps and alphamasks linear), forward opaque model pass with sun +
ambient, cube-grid instancing (2 instance buffers, 18 instances captured; astronaut, armor blocks, cockpit and grid
geometry all render, log clean), and a placeholder ACES tonemap with fixed exposure 0.03 in `CompositePass` (the
cockpit capture shows correct detail and colors). Visual limitations are expected at this stage: no IBL/eye
adaptation (ambient is the game's 0.02 probe floor), no shadows, transparency, voxels, skinning, culling or LOD
selection. **Monitor selection fixed and verified by the user across restarts** (2026-10-17): the adapter (game
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
gone. **Dynamic point/spot lights are implemented**: `CreateRenderLight`/`UpdateRenderLight` feed a bounded (64)
camera-relative light buffer; the forward shader applies the stock attenuation/cone formulas and gloss/diffuse
factors. `AddRuntimeModel` (mod runtime meshes, material lookup by name, replacement alias), `UnloadTextures` and
`DeprioritizeTextures` are handled too. **Exposure now follows the game's postprocess settings**: the constant/eye-
adaptation formula from the shipped `CalculateExposure`, a sparse scene-luminance pass into a 1x1 exposure texture,
Hable tonemapping with the world's white point, and world `BloomEmissiveness` (120 in the test world) multiplying
emissive radiance, so battery/LED indicators stay bright in shadow. The test world's eye adaptation is enabled; a
user `ExposureScale` multiplier remains until full histogram adaptation and bloom are implemented. **LCD apps are fixed and verified**: the Linux platform's
`GetMemoryBudgetForGeneratedTextures()` reports 0, so `MySessionComponentPanels` never counted any panel as visible
and every working screen stayed on the Online texture. A Harmony patch sets the session component's budget to
1 GiB; the game then creates the generated textures, renders sprites/text offscreen, and swaps `ScreenArea` to them
(verified: "This Is A Test" text and the cockpit script screens). Fonts now load synchronously and offscreen jobs
retry while a generated target is still being created. The persistent opaque part cache and GPU LOD selection are in
place; skinned actors and transparent parts keep the direct/OIT paths, and the per-frame actor state is still
uploaded from the CPU. **Automated tests** now work via
`VULKAN_AUTOLOAD=<seconds>`; `VULKAN_DEBUG_LOOKAT=<model substring>` (+ `VULKAN_DEBUG_DARK=1`) frames the first
matching actor for visual checks. Still missing by design: dynamic scene probe (the hard sun terminator on the dark
side stays dark until it exists), shadows, and bloom (world bloom-emissiveness is applied to the material radiance,
not as a screen-space glow yet). Never run git; the user commits.

**Block paints and skins are verified (2026-10-18).** Space Engineers stores a block's paint as a
`ColorMaskHSV` **delta**, not a final colour; stock `ColorizeGray` adds
`TextureDebugMultipliers.ColorizeHSV = (0, 0.8, -0.1)` before converting HSV back to RGB. Our shader
had omitted that conversion, so the user's applied colours collapsed to grey. With the stock conversion
restored, the saved green world renders green (capture verified), and the user changed a battery to red
in game and confirmed it displays correctly. `ChangeMaterialTexture` and `UpdateRenderEntity` updates
that arrive before `CreateRenderEntity` are now held in pending state and applied at creation (skins
included); the CarbonFibre skin pattern is visible. Specular/reflection remain user-tunable sliders and
default to 0 to match stock's forward-opaque shading.

**Next tasks, in order:**
1. **Finish the GPU-driven scene path**: persistent opaque parts, GPU LOD selection (stock hysteresis) and
   static-group buckets are in. Remaining: upload actor state only when it changes (cache the camera-relative world
   per actor instead of rebuilding every visible actor record), move skinned actors and transparent parts into GPU
   buckets, and keep the direct path only for unsupported techniques. Goal: no per-frame actor/part walk.
2. **Bloom pass** (stock prefilter from `BloomEmissiveness`, cascaded blur, composite add) so bright indicators glow
   instead of only reading as hot pixels.
3. **Dynamic environment probe** (scene cubemap capture + prefilter, close/far blend) to soften the hard sunlight
   terminator and light interiors; then shadows.
4. **G-buffer + motion vectors** (frame constants already carry the camera; add previous-frame matrices and the
   MRT pass) so TAA/FSR/GI can build on it.
5. Remaining Phase 2: voxel cells, static/merge groups, world sprites/particles; remaining Phase 1 odds: options
   presets.
6. When flying away, watch the `LOD histogram` in the 5-second `Scene:` log line to confirm LOD switching.

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
| Vulkan smoke test (no game) | `dotnet run --project Tools/VulkanSmokeTest -- [--validation] [--seconds N]` — opens an SDL3 window, runs the plugin's device layer |
| DDS loader survey | `dotnet run --project Tools/VulkanSmokeTest -- --dds ~/.local/share/Steam/steamapps/common/SpaceEngineers/Content/Textures` (expect 1 failure: a PSD misnamed `.dds`) |
| Run the game | `cd ~/.local/share/PulsarRelease && ./Interim.bin -sources` (Pulsar **v2.4.3** official release). Steam launch option alternative: `/home/sentorx/.local/share/PulsarRelease/Interim.bin %command% -sources` with Proton disabled. Pulsar recompiles the plugin from `~/Development/Vulkan` when files change (debug build). |
| Frame capture | Run with `VULKAN_SCREENSHOT=/tmp/x.png VULKAN_SCREENSHOT_DELAY=25` (env vars); the renderer writes one PNG after the delay. View it with the Read tool. This is how to verify rendering without the user. |
| Automatic test | Add `VULKAN_AUTOLOAD=<seconds>` to press **Continue Game** on the main menu automatically (last session), then combine with the frame capture env vars. Fully headless of user input: no clicking needed. |
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
| Image-based lighting | Static first pass: `BackgroundCube.dds` is the depth-less background; `BackgroundCubeIndirect.dds` (10 prefiltered mips) gives diffuse irradiance (last mip) and roughness-based reflections; `ambient_brdf.dds` (R16G16F, linear) gives the split-sum BRDF. Cubemaps use their own bindless array (set 0, binding 2); sampling follows stock `ApplySkyboxOrientation`. The dynamic scene probe (stock `MyEnvironmentProbe`), far/near probe blending and full histogram eye adaptation come later; a simple 1x1 exposure pass and Hable tonemapping are in place. |
| Transparent models | Glass/holo/shield parts look up `VRageRender.MyTransparentMaterials` by material name (fallback `GlassCCW`), use the stock material color (linear), color add, light multiplier, reflectivity/fresnel/gloss and texture/gloss texture. **Weighted-blended OIT**: a second pass writes RGBA16F accumulation (`color * alpha * weight`, additive) and R16F revealage (`dst *= 1 - alpha` via `OneMinusSrcColor`) with depth test read-only/no depth write, per-material `TriangleFaceCulling`; composite resolves `accum.rgb/accum.a` over the scene with `1 - reveal`. No CPU sort. |
| Skinning | **All GPU**: the skinned MWM buffer (64 B/vertex with bone indices/weights) is deformed by a compute pass; a second compute pass (`BonePalette.slang`) assembles the model-bone palette (bind-inverse × absolute) on the GPU. The CPU only uploads the skeleton skin transforms, the per-frame absolute transforms and the model→skeleton index map. Output is the normal 32 B static vertex layout, so the forward pass is unchanged. The game calls `Render.Draw` only for IDs reported in `MyRenderProxy.VisibleObjectsWrite`, so visible IDs of actors with `SetVisibilityUpdates` are published each frame; without this the game never sends `SetCharacterTransforms`. |
| Material coloring | `ColorMaskHSV` is a **delta from the grey/magenta paint base**, not a final colour. Stock `ColorizeGray` adds `TextureDebugMultipliers.ColorizeHSV` (default `(0, 0.8, -0.1)`) after zeroing the texture's HSV; we do the same. `RemoveMetalnessFromColoring` is applied unless the effective material flags include `RenderFlags.MetalnessColorable` (asset-modifier skins). Pre-create colour/skin messages are kept on a pending actor and applied at creation. |
| GPU buffers | No UBOs by design: per-frame/per-draw constants go through push constants plus host-visible transient buffers read via buffer device addresses (the modern SSBO style). Persistent device-local buffers hold model vertices/indices, instance data, textures and the skinned streams. Compute writes/reads (skinning, palette, culling) use the same BDA path. Indirect buffers are host-visible storage buffers with `Indirect` usage. Storage images/RW buffers and acceleration structures are still reserved for OIT, GI/RT. |
| GPU-driven opaque draws | Opaque, non-skinned actors (including instanced cubes and **static groups**) use a **persistent** `GpuScenePart` buffer (material + vertex/index BDAs + index range, built on actor/model/material changes), persistent per-LOD ranges, and a persistent per-actor LOD-state buffer. Per frame the CPU uploads only actor world/colour/bounds/instance state; `Scene/Culling.slang` runs one thread per actor, selects the LOD with the stock distance formula + 10% hysteresis, sphere-tests 5 normalized planes + `MaxViewDistance`, then appends one command + 184-byte draw data per part to a cull/no-cull bucket: `[counter u32][commands @64, 16 B]` followed by draw data. Static groups are expanded into one actor record per local matrix sharing the same ranges. The indirect command is **non-indexed** (`vertexCount=indexCount`); the vertex shader fetches the model index from the draw data's index buffer BDA and then the vertex. `SV_DrawIndex` selects the draw data; the fragment gets the same material through a flat draw-index varying. `push.drawData == 0` selects the direct push path, so one `Model.slang`/pipeline serves both. |
| Asset preload | `PreloadTextures`/`PreloadModel(s)`/`PreloadMaterials` start background decodes/parses immediately (mapping TextureType to the sampling kind), so GPU buffers/images are created during the loading screen rather than on first draw. Model texture dependencies also start when each model is parsed. `UnloadTextures` frees matching entries/slots (in-flight decodes are discarded) and `DeprioritizeTextures` is a no-op (no streaming priorities). `AddRuntimeModel` builds a one-LOD GPU mesh from `MyModelData`, resolves section material names through the model-material table, and registers the name (plus a `ReplacedModel` alias). |
| Exposure / emissive | `UpdatePostprocessSettings` is cached. `Post/Exposure.slang` samples the HDR scene on a sparse grid, averages log-luminance, applies the shipped `CalculateExposure` (`LuminanceKeyValueCendos`) and writes a 1x1 R32F exposure texture; the composite samples it and applies Hable with the world's `WhitePoint`. The 0.025 calibration was removed (it made the world ~40x too dark); a user `ExposureScale` multiplier is applied and clamped to [0.02, 4]. Emissive radiance is `albedo * (1 - metalness) * extras.y * Emissivity * BloomEmissiveness`; the test world uses `BloomEmissiveness=120`, which is what makes battery LEDs read in shadow. Full histogram eye adaptation, temporal smoothing and bloom are still missing. |
| LCD budget | Linux's platform renderer reports no generated-texture memory budget. `LcdMemoryBudgetPatch` sets `MySessionComponentPanels.m_memoryBudget` to 1 GiB after init; otherwise the panel manager's visible counter never reaches its threshold, `IsInRange` stays false, and every powered LCD keeps the default "Online" texture. |
| Dynamic lights | `CreateRenderLight`/`UpdateRenderLight` are cached per ID. Each frame up to 64 point/spot lights are uploaded camera-relative (`GpuLight`, 64 B) through the transient allocator; `FrameConstants` carries the pointer/count. The game already multiplies intensity into the linear color; `MyLightComponent`'s falloff conversion and the stock hard/diffuse attenuation and spot cone are applied in `Model.slang`. Shadows, glare/flares and light culling (stock uses tiled forward) are not implemented. |
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
| `Render/RendererStatus.cs` | Which renderer runs, fallback reason, selected GPU capabilities (for the settings dialog). |
| `Render/RenderProxyInitializePatch.cs` | Prefix on `MyRenderProxy.Initialize(ref IMyRender)`: swaps `MyDX11Render` for `VulkanRender` if selected and the probe passes. |
| `Render/VulkanRender.cs` | `IMyRender` implementation: device lifetime, settings/resize, per-frame flow, message switch (non-GUI messages). |
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
| `Render/Vulkan/GpuMemory.cs` | Block sub-allocator (64 MB blocks, dedicated ≥ 32 MB, buffers and images in separate blocks, host-visible blocks persistently mapped, every block has DEVICE_ADDRESS flag). |
| `Render/Vulkan/GpuBuffer.cs`, `GpuImage.cs` | Resources; `Dispose` defers destruction through `GpuContext.Release`. |
| `Render/Vulkan/UploadQueue.cs` | Per-frame 32 MB staging rings; copies recorded at the start of the frame's command buffer; images end in ShaderReadOnlyOptimal. Render thread only. |
| `Render/Vulkan/BindlessDescriptors.cs` | Set 0 (binding 0: up to 65536 sampled images, binding 1: 4 immutable samplers, binding 2: 16 cubemaps with a black default), the shared pipeline layout (128 B push constants, all stages), fixed texture slots White/Black/Missing/Transparent. |
| `Render/Vulkan/MipGenerator.cs` | Blit-chain mip generation (uploads with `generateMips`, offscreen targets). |
| `Render/Vulkan/GraphicsPipelineBuilder.cs` | Init-property description → graphics pipeline (dynamic rendering, shared bindless layout, dynamic viewport/scissor, no vertex input, reverse-Z depth compare by default). Use it for every new pipeline. |
| `Render/Vulkan/TransientAllocator.cs` | `GpuContext.Transient`: per-frame linear host-visible scratch with device addresses (frame constants, per-draw data, instances). Reset in `GpuContext.BeginFrame`. |
| `Render/Scene/SceneTargets.cs` | HDR color (R16G16B16A16_SFLOAT) + depth (D32_SFLOAT, reverse Z, cleared to 0) at the game resolution, bindless slots; recreated on resize. |
| `Render/Scene/SkyboxPass.cs` | Depth-less fullscreen background sampling the environment cubemap along the view ray. |
| `Render/Scene/CameraState.cs` | Camera matrices incl. the stock unflipped culling matrix, `FrameConstants` (view-projection, inverse for rays, skybox orientation, sun, ambient, IBL slots/params, paint conversion `ColorizeHsv`, tuning), LOD coefficient. |
| `Render/Scene/SceneActors.cs` | Render-object registry: create/update/remove, visibility, model change, per-material overrides/flags, `ChangeMaterialTexture` changes, static groups, manual cull-object hierarchy, instance ranges/bounds, max view distance, per-actor LOD; `Revision` drives persistent part-cache rebuilds and pending pre-create colour/skin state. |
| `Render/Scene/Frustum.cs` | Five-plane infinite-far-plane culling from the stock view-projection. |
| `Render/Scene/ModelPass.cs` | Forward opaque MWM pass: persistent part cache + GPU LOD/culling for opaque non-skinned actors (incl. static groups/cubes), direct path for skinned/transparent, cull-back/no-cull pipelines, bindless materials, IBL + sun shading; GPU skinning compute pre-pass; visible-object reporting (`VisibleObjectsWrite`). |
| `Render/Scene/GpuSceneDraws.cs` | Wire-format structs for persistent parts/LOD ranges/actor state, the cull compute dispatch, and the two indirect cull/no-cull buckets. |
| `Render/Scene/InstanceBuffers.cs` | GPU instance buffers for cube/generic ranges (replaced per update for frames-in-flight safety). |
| `Render/Models/MwmLoader.cs`, `MwmMeshData.cs` | Background MWM parsing through `MyModelImporter`, all LODs, stock vertex decodes, texture path resolution. |
| `Render/Models/ModelCache.cs` | Model path → parsed LOD data → device-local GPU buffers, parts and texture entries; LOD selection helper; `Version` bumps when a model becomes GPU-ready so `ModelPass` rebuilds its part cache. |
| `Render/Scene/CompositePass.cs` | Fullscreen triangle: HDR scene + OIT resolve → ACES/Hable backbuffer using the exposure texture. |
| `Render/Vulkan/ShaderModule.cs`, `VkNames.cs`, `DeviceCapabilities.cs` | Helpers. |
| `Render/Textures/DdsLoader.cs`, `TextureData.cs` | DDS → upload layout (DX10, FourCC, masks, palette; conversions to RGBA8 where Vulkan has no format). |
| `Render/Textures/TextureCache.cs` | Name → texture; async decode, render-thread finish/upload, sRGB + GUI premultiply like stock, size/loaded queries. |
| `Render/Sprites/GuiRenderer.cs` | GUI messages (sprites, text, scissor, fonts, viewport scale) for the main target; records the GUI pass. |
| `Render/Sprites/SpriteBatcher.cs`, `SpriteRenderer.cs`, `SpriteInstance.cs` | CPU batching (stock geometry rules) and GPU drawing (instances by device address, bindless textures). |
| `Render/Sprites/RenderFont.cs` | Port of stock `MyRenderFont` text layout on the game's `MyFont`. |
| `Render/Shaders/ShaderBytecode.g.cs` | Generated SPIR-V. Do not edit. |
| `Shaders/` | Slang sources: `Common/Bindless.slang` (2D + cube arrays, must match `BindlessDescriptors`), `Common/Color.slang`, `Common/Environment.slang` (shared `FrameConstants` + skybox orientation/IBL lookups), `Sprites/Sprite.slang`, `Post/Composite.slang`, `Scene/Model.slang`, `Scene/Skybox.slang`, `Scene/Transparent.slang`, `Scene/Culling.slang` (compute), `Scene/Skinning.slang` (compute), `Scene/BonePalette.slang` (compute), `Common/SkinningCommon.slang` (shared layouts); `shaders.json` manifest. |
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
1. Recreate the swapchain if outdated; `FrameLoop.TryBeginFrame()` (waits the slot fence, acquires).
2. `GpuContext.BeginFrame(slot)` (runs deferred releases, resets the slot's staging ring), `GuiRenderer.BeginFrame()`.
3. `messages.Process(frameStarted)` — **after** the fence wait, because uploads issued while processing (e.g.
   `WaitTillLoaded` textures) write into this slot's staging ring. Without a frame, only state changes run.
4. `TextureCache.ProcessDecoded()` → `UploadQueue.Record(cmd)` → `GuiRenderer.RecordOffscreen` (LCD/generated
   targets) → `RecordScene` (only if a `DrawScene` message arrived this frame: build frame constants, skybox
   background, then opaque geometry into the HDR + depth pass) → `RecordFrame` (backbuffer: clear, composite scene
   if drawn, GUI) → `FrameLoop.EndFrame()`.
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
| Anti-aliasing | Off / FXAA / TAA (FSR 3 implies its own AA) | — | 5 |
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
  `:77-87` (y is flipped for normal maps in the pixel shader, `Content/Shaders/PixelUtils.hlsli:47-70`).
- UV `HalfVector2`. Skinned models add `BlendIndices` (4 x int) and `BlendWeights` (4 x float).

Material channels (`Content/Shaders/Geometry/Materials/Standard/Pixel.hlsl`,
`Content/Shaders/Geometry/Materials/PixelUtilsMaterials.hlsli:67-103`,
`Content/Shaders/GBuffer/GBufferWrite.hlsli:18-33`):

- ColorMetal (`MyFileTextureEnum.COLOR_METAL`): sRGB; `.rgb` albedo, `.a` metalness.
- NormalGloss (`NORMALMAP_GLOSS`): **linear**; `.xy(z)` tangent-space normal (y flipped in the shader), `.w` gloss.
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
(`Content/Shaders/Geometry/VertexTemplateBase.hlsli:244-255`). Not implemented yet: skinned models are loaded but
skipped with one log line.

### Our Phase 2 first slice (implemented 2026-10-17)

`CameraState` + `FrameConstants` (transposed view-projection for the shader's dot-product convention, sun and
ambient), `SceneActors` (create/update/remove/visibility/model change/material overrides/static groups),
`MwmLoader` (background parse through `MyModelImporter`, exact stock decodings, pattern scale, flat index list,
absolute texture paths) + `ModelCache` (device-local vertex/index buffers via `UploadQueue.UploadBuffer`, parts
with `TextureCache` entries), `TextureCache.TextureKind` (GUI sRGB+premultiply, CM/Extensions sRGB, NG/alphamask
linear), and `ModelPass` (forward opaque, reverse-Z, sun + ambient, per-material draw data in the transient
allocator, texture slots through the bindless set). `Shaders/Scene/Model.slang` implements the stock normal and
tangent decodes, colorize, alpha mask, GGX sun and ambient. Cube grids: `InstanceBuffers` owns one
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
lighting, skybox, mip generation, sprites/offscreen rendering; model/texture preloads start background work during
the loading screen.

Remaining per-frame CPU work and its GPU replacement:

| CPU work now | GPU replacement |
|--------------|-----------------|
| Actor/part enumeration, frustum + distance culling, per-draw submission | **Persistent opaque parts + GPU LOD/culling/submission done for non-skinned actors, including instanced cubes and static groups.** Still CPU: per-frame actor record upload, skinned actors and transparent parts. Next: upload actor state only when changed and move the remaining paths into GPU buckets. |
| Transparent back-to-front sorting | **Done**: weighted-blended OIT accumulation + revealage targets; no CPU sort. |
| Per-draw world transposes / material data packing | Still CPU: computed while writing the per-frame cull inputs; the culling compute copies them into draw data. Move with the persistent scene buffers. |
| Per-frame transient uploads | Persistent buffers updated only on messages (create/update/remove/texture change). |
| UI text layout / sprite batching | CPU by nature (font shaping), but glyphs and quads stay GPU; can be cached and uploaded on change. |

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
- [ ] Texture messages: UnloadTexture(s), DeprioritizeTextures
- [x] World load / save / reload on Vulkan works (user, 2026-10-17): HUD, menus and "World loaded" text show,
      no errors in the log; the game does not block on render outputs we do not send yet
- [ ] Video: stubbed (`IsVideoValid` false; the menu shows its static background). Real playback in Phase 7
      (LinuxCompat already has an FFmpeg-based `MyLinuxVideoPlayer` behind `IVideoPlayer`, but it uploads to D3D11)
- [~] "Vulkan" options screen, first version: `ClientPlugin/Gui/GraphicsOptionsPatch.cs` adds a "Vulkan Renderer"
      button to the Graphics options screen (verified by frame capture) that opens the plugin's settings dialog
      (`Plugin.OpenConfigDialog`): Status section (active renderer + fallback reason, GPU, ray tracing / mesh shader
      support via `RendererStatus` and the new read-only `[Info]` settings element) and Renderer section (renderer
      choice, validation). Dialog contents not yet seen by the user. Still to do: presets, per-feature options as
      features land, greyed-out unsupported items with reasons.
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
- [~] Transparent models (glass, holo, shield): implemented with `MyTransparentMaterials`, premultiplied blend,
      depth read-only, back-to-front sorting; visual verification pending
- [ ] Triplanar materials, decals and OIT/dithering
- [x] Skinned characters: GPU compute deformation, GPU bone palette, skeleton/transforms, visibility output;
      character appears correctly posed and animates (third-person capture). First-person head hiding uses the
      game's per-material visibility flags.
- [ ] Voxel cells (consume game-meshed clipmap data), voxel materials; `ClipmapsReady` output
- [ ] G-buffer **including motion vectors** (needed later for TAA/FSR/GI); forward sun lighting is done
- [x] Persistent opaque part/material ranges + GPU LOD selection incl. static groups (first GPU-scene slice)
- [ ] Frame graph + pipeline cache
- **Milestone:** load a world; grids, characters and planets render.

### Phase 3 — Lighting and shadows
- [~] PBR lighting (GGX, energy-conserving), sun + static skybox IBL (done); dynamic scene probe and eye
      adaptation still open
- [ ] Cascaded shadow maps (+ PCSS option, contact shadows), spot shadow pool
- [~] Dynamic point/spot lights (`CreateRenderLight`/`UpdateRenderLight` implemented; clustered light culling later)
- [ ] Dynamic environment probe + prefiltering (scene capture, close/far blend), SSR
- [ ] SSAO / GTAO
- **Milestone:** lighting at least equal to stock.

### Phase 4 — Transparency and effects
- [x] Weighted OIT and transparent models (glass); [ ] billboards, screen-space decals, foliage
- [ ] GPU particles
- [ ] Atmosphere scattering, clouds, fog
- [ ] Screen-space decals, foliage, highlights/outlines
- **Milestone:** visually feature-complete vs. stock.

### Phase 5 — Post-processing and upscaling
- [ ] Eye adaptation, bloom, tonemapping (stock-like / ACES / AgX), CA/vignette
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
  shader bytecode check passes. README is still template boilerplate. Recorded the no-subagents / no-guessing /
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
  live through `FrameConstants.tuning0/tuning1` and the exposure push constants. Defaults: Exposure 0.25, all others
  1. Verified LCD shows the DangerZone background + text, battery LEDs stay bright, and the world is no longer crushed
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
  actor-state dirty tracking, GPU buckets for skinned/transparent paths, bloom.
