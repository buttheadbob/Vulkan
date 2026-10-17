# Vulkan Renderer — Roadmap

> **Living document.** Every session working on this project must read this file first and
> update it before finishing: tick completed items, adjust the plan, keep [Start here](#start-here)
> current, and add an entry to the [Session log](#session-log). If a decision changes, update the
> [Decisions](#decisions) table rather than leaving contradictory notes.

## Start here

**Current state (2026-10-17 session):** Phase 0 and Phase 1 done (menus, HUD, text, Vulkan settings dialog all
verified by the user; world load/save/reload works). Phase 2 first slice is implemented, builds clean and is
**verified in game by frame capture**: camera + environment state, actor registry, background MWM loader (LOD0),
texture kinds (model CM/Extensions sRGB, normal maps and alphamasks linear), forward opaque model pass with sun +
ambient, cube-grid instancing (2 instance buffers, 18 instances captured; astronaut, armor blocks, cockpit and grid
geometry all render, log clean), and a placeholder ACES tonemap with fixed exposure 0.03 in `CompositePass` (the
cockpit capture shows correct detail and colors). Visual limitations are expected at this stage: no IBL/eye
adaptation (ambient is the game's 0.02 probe floor), no shadows, transparency, voxels, skinning, culling or LOD
selection. Never run git; the user commits.

**Next tasks, in order:**
1. **World correctness pass**: per-actor frustum culling (bounds are loaded already), LOD selection
   (`MyLODDescriptor`), back-face culling, depth bias check, non-uniform scale normal matrix.
2. **G-buffer + motion vectors** (frame constants already carry the camera; add previous-frame matrices and the
   MRT pass) so TAA/FSR/GI can build on it, then Phase 3 lighting/shadows (environment probe/IBL and eye
   adaptation replace the ambient and fixed-exposure placeholders).
3. Remaining Phase 2: voxel cells, skinned characters, transparent materials, static/merge groups.
4. Remaining Phase 1 odds: texture messages (Preload/Unload/Deprioritize), verify LCD offscreen targets in-world,
   options presets as features land.

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
- `ColorMaskHSV` is passed to the shader **as HSV delta**, unchanged (`ColorFromMask` at `MyRender11.cs:2398`
  returns its argument). The shader adds it to the texture HSV (`ColorizeGray`).
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

Not implemented yet: generic instancing (world-environment sectors), voxels, LOD selection, frustum culling,
skinning, transparency, shadows, G-buffer/motion vectors, skybox, eye adaptation (a fixed-exposure ACES tonemap
placeholder is in `CompositePass`).

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

### Phase 2 — World geometry (in progress)
- [x] Scene infrastructure: `SceneTargets`, `CompositePass` + `Composite.slang`, `GraphicsPipelineBuilder`,
      `TransientAllocator`, `DrawScene` handling (scene drawn only on frames with `DrawScene`)
- [x] World message spec written into this roadmap (see [World message spec](#world-message-spec))
- [x] Camera, frame constants, reverse-Z infinite projection, camera-relative world matrices
- [ ] Frustum culling, LOD selection (bounds and LOD descriptors are loaded, only LOD0 is used)
- [x] Actors/render entities, MWM meshes, cube-grid instancing, static groups
- [x] Materials (standard + alpha-masked) on bindless textures, stock decodes and colorize
- [ ] Remaining materials (triplanar, glass, holo, shield) and transparency
- [ ] Skinned characters
- [ ] Voxel cells (consume game-meshed clipmap data), voxel materials; `ClipmapsReady` output
- [ ] G-buffer **including motion vectors** (needed later for TAA/FSR/GI); forward sun lighting is done
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
