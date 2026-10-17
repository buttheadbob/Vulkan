# Vulkan Renderer — Roadmap

> **Living document.** Every session working on this project must read this file first and
> update it before finishing: tick completed items, adjust the plan, keep [Start here](#start-here)
> current, and add an entry to the [Session log](#session-log). If a decision changes, update the
> [Decisions](#decisions) table rather than leaving contradictory notes.

## Start here

**Current state (end of 2026-10-17 session):** Phase 0 and Phase 1 done (menus, HUD, text, Vulkan settings
dialog all verified by the user; world load/save/reload works with the HUD over an empty scene). Phase 2 started:
scene infrastructure only (HDR + depth targets, composite pass, pipeline builder, transient allocator) — builds
clean, **not yet checked in game** (expected: very dark space color behind the HUD in a world instead of black).
Never run git; the user commits.

**Next tasks, in order:**
1. **Research spec for world messages** (was running in a background agent when the session ended; its result was
   lost — redo it). Produce, with file:line refs into the decompiled source: (a) camera: `SetCameraViewMatrix`
   fields, stock projection (`MyRender11.cs` ~:1863 `CreatePerspectiveFovRhInfiniteComplementary`), camera-relative
   precision handling, `UpdateRenderEnvironment` (sun, skybox, ambient); (b) entities: CreateRenderEntity,
   UpdateRenderObject, RemoveRenderObject, UpdateRenderObjectVisibility, ChangeModel, UpdateModelProperties,
   UpdateColorEmissivity, UpdateRenderEntity, SetParentCullObject, CreateManualCullObject, RenderFlags, world
   matrices; (c) cube grid instancing: CreateRenderInstanceBuffer, UpdateRenderCubeInstanceBuffer
   (`MyCubeInstanceData` byte layout + how stock shaders decode it), UpdateRenderInstanceBufferRange,
   SetInstanceBuffer, CreateStaticGroup; (d) MWM import (`VRage.Render/VRageRender.Import/MyModelImporter.cs`):
   vertex encodings and exact decode formulas (see `GeometryStage2.Model`, legacy `MyMeshes`), LODs, parts,
   materials; (e) materials: techniques, texture slots (ColorMetal/NormalGloss/Extensions/Alphamask; normal maps are
   NOT sRGB), channel meanings, keen color-mask/HSV recolor formula (`Content/Shaders/Geometry/Materials/Standard`,
   `GBuffer/GBufferWrite.hlsli`); (f) characters: CreateRenderCharacter, SetCharacterSkeleton,
   SetCharacterTransforms. Write the result into this roadmap (new "World message spec" section) so it survives.
2. **Quick in-game check** of the scene infrastructure (user loads a world, or use `VULKAN_SCREENSHOT` after the
   user is in a world): dark space color behind the HUD, no errors.
3. **Phase 2 implementation**: frame constants (camera-relative matrices, previous-frame matrices for motion
   vectors) in the transient allocator → actor registry (create/update/remove by render object ID) → MWM model
   cache (GPU vertex/index buffers, LODs) → material textures via `TextureCache` (needs non-sRGB normal maps:
   `TextureCache` currently applies GUI rules, add a texture "kind") → first opaque pass with a simple sun light
   into `SceneTargets` → cube grid instancing → G-buffer + motion vectors.
4. Remaining Phase 1 odds: texture messages (Preload/Unload/Deprioritize), options presets as features land.

**User preferences / rules (all sessions):**
- Never run git commands. The user commits and pushes.
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
| Stop the game | `kill -TERM $(pgrep Interim)`. **Never `pkill -f <pattern>`** — it matches the invoking shell's own command line and kills the tool shell instead. |
| Logs | Game log (has `[Vulkan]` lines): newest `~/.config/SpaceEngineers/SpaceEngineers_*.log`; console: `~/.config/SpaceEngineers/Console_*.log` or redirect stdout. Pulsar log: `~/.local/share/PulsarRelease/Legacy/info.log` (plugin compile errors appear here). |
| Pulsar dev folder config | `~/.local/share/PulsarRelease/Legacy/Sources/sources.xml` (`<LocalPlugin>` Folder=repo, File=`Vulkan.xml`) and `Legacy/Profiles/Current.xml` (`<DevFolder><LocalFolderConfig><Id>7C5A605C-1041-4C94-988A-5DF95F3145F8</Id>`). Already set up. |
| Plugin config file | `~/.config/SpaceEngineers/Storage/Vulkan.cfg` (`Config.Renderer`, `Validation`; XML). Only exists once saved (config dialog, or the automatic switch to DirectX11 after a device creation failure); defaults apply otherwise (Renderer=Vulkan). |

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
| Git | The user does all git operations. Sessions never run git commands. |

## Our code map

| File | Purpose |
|------|---------|
| `ClientPlugin/Preloader.cs` | Global-namespace `Preloader` (Pulsar finds it by name). `Finish()` applies the `VulkanEarly` Harmony category. No Cecil patches. |
| `ClientPlugin/Plugin.cs` | `IPlugin`; `PatchAllUncategorized` only (renderer patches are early). Config dialog. |
| `ClientPlugin/Config.cs` | `RendererKind Renderer` (Vulkan/DirectX11), `bool Validation`, read-only `[Info]` status properties (getter-only, so `XmlSerializer` skips them). Template `SettingsGenerator` UI. |
| `ClientPlugin/Gui/GraphicsOptionsPatch.cs` | Postfix on `MyGuiScreenOptionsGraphics.RecreateControls`: "Vulkan Renderer" button (found by control names `GraphicsPreset` / `EnableDamageEffects`). |
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
| `Render/Vulkan/BindlessDescriptors.cs` | Set 0 (binding 0: up to 65536 sampled images, binding 1: 4 immutable samplers), the shared pipeline layout (128 B push constants, all stages), fixed texture slots White/Black/Missing/Transparent. |
| `Render/Vulkan/MipGenerator.cs` | Blit-chain mip generation (uploads with `generateMips`, offscreen targets). |
| `Render/Vulkan/GraphicsPipelineBuilder.cs` | Init-property description → graphics pipeline (dynamic rendering, shared bindless layout, dynamic viewport/scissor, no vertex input, reverse-Z depth compare by default). Use it for every new pipeline. |
| `Render/Vulkan/TransientAllocator.cs` | `GpuContext.Transient`: per-frame linear host-visible scratch with device addresses (frame constants, per-draw data, instances). Reset in `GpuContext.BeginFrame`. |
| `Render/Scene/SceneTargets.cs` | HDR color (R16G16B16A16_SFLOAT) + depth (D32_SFLOAT, reverse Z, cleared to 0) at the game resolution, bindless slots; recreated on resize. |
| `Render/Scene/CompositePass.cs` | Fullscreen triangle: scene color → backbuffer (clamp placeholder for tonemapping). |
| `Render/Vulkan/ShaderModule.cs`, `VkNames.cs`, `DeviceCapabilities.cs` | Helpers. |
| `Render/Textures/DdsLoader.cs`, `TextureData.cs` | DDS → upload layout (DX10, FourCC, masks, palette; conversions to RGBA8 where Vulkan has no format). |
| `Render/Textures/TextureCache.cs` | Name → texture; async decode, render-thread finish/upload, sRGB + GUI premultiply like stock, size/loaded queries. |
| `Render/Sprites/GuiRenderer.cs` | GUI messages (sprites, text, scissor, fonts, viewport scale) for the main target; records the GUI pass. |
| `Render/Sprites/SpriteBatcher.cs`, `SpriteRenderer.cs`, `SpriteInstance.cs` | CPU batching (stock geometry rules) and GPU drawing (instances by device address, bindless textures). |
| `Render/Sprites/RenderFont.cs` | Port of stock `MyRenderFont` text layout on the game's `MyFont`. |
| `Render/Shaders/ShaderBytecode.g.cs` | Generated SPIR-V. Do not edit. |
| `Shaders/` | Slang sources: `Common/Bindless.slang` (must match `BindlessDescriptors`), `Common/Color.slang`, `Sprites/Sprite.slang`, `Post/Composite.slang`; `shaders.json` manifest. |
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
   targets) → `RecordScene` (only if a `DrawScene` message arrived this frame: HDR + depth pass, geometry goes here)
   → `RecordFrame` (backbuffer: clear, composite scene if drawn, GUI) → `FrameLoop.EndFrame()`.
5. `Present()` → `FrameLoop.Present()` (+ frame capture completion).

### Invariants (easy to break)
- Bindless texture slots are written **once** (when the texture is ready) and freed only via deferred release:
  descriptors use `UPDATE_AFTER_BIND | PARTIALLY_BOUND | UPDATE_UNUSED_WHILE_PENDING`; never overwrite a slot a frame
  in flight may read. Textures still loading sample the Transparent slot; failed ones Missing.
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

(Both tasks below are implemented — offscreen targets are marked [~] until an LCD is seen working in a world. Kept as
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
- [~] Offscreen sprite targets + generated textures: `TextureCache.CreateGenerated/ResetGenerated/DestroyGenerated`
      (RGBA → R8G8B8A8_SRGB, RGBA_Linear → UNORM, Alphamask → R8; optional full mip chain via `MipGenerator` blits),
      `GuiRenderer` keeps offscreen sprite messages per target (AddRef, newest message batch wins, like
      `MySpritesManager`), `RenderOffscreenTexture` queues a job (max 16 per frame), recorded before the main pass:
      clear to linearized background, sprites with `SpriteBlend.PremultipliedColorOnly` in `size * AspectRatio`
      coordinates, then mips; `GeneratedReady` drives `IsTextureLoaded`. `SpriteRenderer` now caches pipelines per
      (format, blend) and allocates instances linearly per frame. `UploadQueue` uses dedicated staging outside an
      open frame. Builds and the menu still renders; not exercised yet (LCDs need a loaded world).
- [ ] Texture messages: PreloadTextures, UnloadTexture(s), DeprioritizeTextures
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

### Phase 2 — World geometry (started)
- [~] Scene infrastructure: `SceneTargets`, `CompositePass` + `Composite.slang`, `GraphicsPipelineBuilder`,
      `TransientAllocator`, `DrawScene` handling (scene drawn only on frames with `DrawScene`). Builds; not seen in game.
- [ ] World message spec written into this roadmap (see Start here, task 1)
- [ ] Camera, frame constants, reverse-Z infinite projection, culling (frustum, later GPU-driven)
- [ ] Actors/render entities, MWM meshes + LODs, instancing (cube grids, merge groups), static groups
- [ ] Materials (standard, alpha-masked, triplanar, glass, holo, shield) on bindless textures
- [ ] Skinned characters
- [ ] Voxel cells (consume game-meshed clipmap data), voxel materials; `ClipmapsReady` output
- [ ] G-buffer **including motion vectors** (needed later for TAA/FSR/GI), basic sun lighting
- [ ] Frame graph + pipeline cache
- **Milestone:** load a world; grids, characters and planets render.

### Phase 3 — Lighting and shadows
- [ ] PBR lighting (GGX, energy-conserving), sun + IBL
- [ ] Cascaded shadow maps (+ PCSS option, contact shadows), spot shadow pool
- [ ] Clustered point/spot lights
- [ ] Environment probe + prefiltering, SSR
- [ ] SSAO / GTAO
- **Milestone:** lighting at least equal to stock.

### Phase 4 — Transparency and effects
- [ ] Billboards, weighted OIT, transparent models (+ decals on glass)
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
  `ClientPlugin.Patches.PlatformGuards.SdlInput2Provider.Instance.OnModeChanged`). Changes there can break us;
  detection is defensive and falls back to D3D11.
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
