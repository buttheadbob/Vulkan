using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using ClientPlugin.Render.Models;
using ClientPlugin.Render.Platform;
using ClientPlugin.Render.Scene;
using ClientPlugin.Render.Sprites;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using ClientPlugin.Testing;
using ClientPlugin.Settings;
using Vortice.Vulkan;
using VRage;
using VRage.FileSystem;
using VRage.Library.Utils;
using VRage.Profiler;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;
using VRageRender.Profiler;

namespace ClientPlugin.Render;

// The game's renderer interface, implemented on Vulkan. Replaces MyDX11Render.
// The game's own render thread (MyRenderThread) drives it:
// CreateDevice -> { ApplySettings on changes, Draw, Present } -> DisposeDevice.
public sealed class VulkanRender : IMyRender
{
    private readonly GameWindowBridge window;
    private readonly MySharedData sharedData = new();
    private readonly MyNullRenderProfiler profiler = new();
    private readonly MessageQueueProcessor messages;

    private VulkanInstance instance;
    private VkSurfaceKHR surface;
    private VulkanDevice device;
    private VulkanSwapchain swapchain;
    private FrameLoop frameLoop;
    private GpuContext gpu;
    private TextureCache textures;
    private GuiRenderer gui;
    private FrameCapture capture;
    private SceneTargets sceneTargets;
    private ExposurePass exposurePass;
    private BloomPass bloomPass;
    private SkyboxPass skyboxPass;
    private ulong lastFrameConstants;
    private BillboardPass billboards;
    private readonly ThrusterEffects thrusterEffects = new();
    private CompositePass composite;
    private FxaaPass fxaa;
    private VkFormat compositeFormat;
    private GpuBuffer renderSettingsBuffer;
    private int renderSettingsDirty;
    private ModelCache models;
    private InstanceBuffers instanceBuffers;
    private ModelPass modelPass;
    private VoxelPass voxelPass;

    private readonly CameraState camera = new();
    private readonly SceneActors actors = new();
    private FrameConstants lastFrameConstantsData;
    private uint lastStaticIblSlot;
    private uint lastStaticIblMips;
    private ProbePass probePass;
    private readonly SceneLights sceneLights = new();
    private MyPostprocessSettings postprocessSettings = MyPostprocessSettings.Default;
    private readonly string debugLookAtModel = Environment.GetEnvironmentVariable("VULKAN_DEBUG_LOOKAT");
    private readonly bool debugVoxel = Environment.GetEnvironmentVariable("VULKAN_DEBUG_VOXEL") == "1";
    private readonly bool debugLookAtDark = Environment.GetEnvironmentVariable("VULKAN_DEBUG_DARK") == "1";
    private readonly bool debugLookAtTop = Environment.GetEnvironmentVariable("VULKAN_DEBUG_TOP") == "1";
    private readonly float debugLookAtDistance = ParseDebugFloat("VULKAN_DEBUG_DISTANCE", 9f);
    private readonly bool debugShadowMap = Environment.GetEnvironmentVariable("VULKAN_DEBUG_VIEW") == "9";
    private readonly bool debugSsr = Environment.GetEnvironmentVariable("VULKAN_DEBUG_VIEW") == "10";
    private readonly bool debugSsrColor = Environment.GetEnvironmentVariable("VULKAN_DEBUG_VIEW") == "11";
    private readonly bool probeDisabled = Environment.GetEnvironmentVariable("VULKAN_NO_PROBE") == "1";
    private readonly VkSampleCountFlags sceneSamples = ToSamples(Config.Current.MsaaLevel);
    private bool reportedDebugLookAt;
    private SceneActor debugTarget;
    private Vector3D debugTargetCenter;
    private bool hasDebugVoxel;
    private Vector3D debugVoxelCenter;
    private float debugVoxelRadius;
    private MyEnvironmentData environment;
    private bool hasEnvironment;
    private int lastDrawnActors;
    private int reportedModelChanges;
    private int lastCulledActors;

    private double lastMessagesMs;
    private double lastUploadsMs;
    private double lastGuiMs;
    private long frameAllocStart;
    private MyNewLoddingSettings loddingSettings = new();
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly Stopwatch frameLimitClock = Stopwatch.StartNew();
    private double nextFrameTime;
    private double lastFrameTime;
    private double frameMilliseconds;
    private double rawFrameMilliseconds;
    private double framesPerSecond;

    // Set by the DrawScene message of the frame being drawn (MyRender11.m_drawScene): without it only the GUI is shown
    private bool drawScene;

    private MyAdapterInfo[] adapters = [];
    private MyRenderDeviceSettings settings = new() { AdapterOrdinal = -1 };
    private MyWindowModeEnum? lastForwardedWindowMode;
    private int lastForwardedAdapter = -1;
    private MyRenderSettings renderSettings;
    private MyRenderDebugOverrides debugOverrides = new();

    private VulkanRender(GameWindowBridge window, MyRenderSettings initialSettings)
    {
        this.window = window;
        renderSettings = initialSettings;
        messages = new MessageQueueProcessor(sharedData, ProcessMessage);
        MyRenderProfiler.SetAutocommit(false);
    }

    // Checks whether Vulkan can run here at all, before the game commits to this renderer.
    // Device creation proper happens later on the render thread, once the window exists.
    public static VulkanRender TryCreate(MyRenderSettings initialSettings, out string reason)
    {
        var window = GameWindowBridge.Detect(out reason);
        if (window == null)
            return null;

        if (!VulkanInstance.TryInitializeLoader(out reason))
            return null;

        try
        {
            using var probe = VulkanInstance.Create([], validation: false);
            var devices = probe.EnumerateDevices();
            if (!devices.Any(d => d.Caps.MeetsRequirements(out _)))
            {
                reason = "No GPU meets the requirements: " + string.Join("; ", devices.Select(d =>
                    d.Caps.MeetsRequirements(out var missing) ? d.Caps.Name : $"{d.Caps.Name} lacks {missing}"));
                return null;
            }
        }
        catch (Exception e)
        {
            reason = "Vulkan probe failed: " + e.Message;
            return null;
        }

        RenderLog.Info($"Using window system: {window.Name}");
        reason = null;
        return new VulkanRender(window, initialSettings);
    }

    #region Device

    public MyRenderDeviceSettings CreateDevice(MyRenderDeviceSettings? settingsToTry, out MyAdapterInfo[] adaptersList)
    {
        try
        {
            // Display modes, desktop bounds and VRAM come from the platform's adapter enumeration.
            // TODO: enumerate displays through SDL / Win32 instead (see Docs/Roadmap.md)
            adapters = (MyAdapterInfo[])MyVRage.Platform.Render.GetRenderAdapterList().Clone();
            adaptersList = adapters;

            var requested = settingsToTry ?? DefaultSettings();
            if (requested.AdapterOrdinal < 0 || requested.AdapterOrdinal >= adapters.Length)
                requested.AdapterOrdinal = 0;

            instance = VulkanInstance.Create(window.GetSurfaceExtensions(), Config.Current.Validation);
            surface = window.CreateSurface(instance.Handle, instance.Api);
            device = VulkanDevice.Create(instance, surface, adapters.Length != 0 ? adapters[requested.AdapterOrdinal] : null);
            RendererStatus.SetDevice(device.Caps);

            swapchain = new VulkanSwapchain(instance, device, surface);
            swapchain.Recreate(new Vector2I(requested.BackBufferWidth, requested.BackBufferHeight), Config.Current.VSync);
            frameLoop = new FrameLoop(device, swapchain);

            gpu = new GpuContext(instance, device);
            renderSettingsBuffer = GpuBuffer.Create(gpu, (ulong)GpuRenderSettings.Size,
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            gpu.Bindless.SetSettingsBuffer(renderSettingsBuffer);
            UploadRenderSettings();
            Config.Current.PropertyChanged += OnConfigChanged;
            textures = new TextureCache(gpu);
            models = new ModelCache(gpu, textures);
            instanceBuffers = new InstanceBuffers(gpu);
            modelPass = new ModelPass(gpu, models, instanceBuffers, textures, sceneSamples);
            voxelPass = new VoxelPass(gpu, textures, sceneSamples);
            gui = new GuiRenderer(gpu, textures, () => messages.FrameId);
            capture = FrameCapture.FromEnvironment(gpu);
            sceneTargets = new SceneTargets(gpu);
            exposurePass = new ExposurePass(gpu);
            bloomPass = new BloomPass(gpu);
            skyboxPass = new SkyboxPass(gpu, sceneSamples);
            billboards = new BillboardPass(gpu, textures);
            probePass = new ProbePass(gpu, modelPass, skyboxPass, billboards, voxelPass,
                ProbeResolution(Config.Current.ReflectionProbeQuality));
            // A cubemap capture is sampled through blurred mips; MSAA would multiply the probe's
            // memory and bandwidth six times for no visible gain.
            probePass.SetSamples(VkSampleCountFlags.Count1);

            settings = requested;
            ForwardWindowMode(force: true);

            RenderLog.Info($"Device created: {settings}");
            return settings;
        }
        catch (Exception e)
        {
            RenderLog.Error("Vulkan device creation failed", e);
            DisposeDevice();

            // Make sure the next start works: switch the configured renderer back to DirectX 11
            Config.Current.Renderer = RendererKind.DirectX11;
            ConfigStorage.Save(Config.Current);

            throw new MyRenderException(
                "The Vulkan renderer failed to start and has been switched back to DirectX 11 for the next start.\n" +
                "Please restart the game. Details are in the game log.\n\n" + e.Message,
                MyRenderExceptionEnum.GpuNotSupported);
        }
    }

    private MyRenderDeviceSettings DefaultSettings()
    {
        var adapter = adapters.Length != 0 ? adapters[0] : default;
        var size = adapter.DesktopBounds.Width > 0
            ? new Vector2I(adapter.DesktopBounds.Width, adapter.DesktopBounds.Height)
            : new Vector2I(1280, 720);
        return new MyRenderDeviceSettings(0, MyWindowModeEnum.FullscreenWindow, size.X, size.Y, 0, 1, false, false,
            [new MyDRSSettings(), new MyDRSSettings(), new MyDRSSettings()]);
    }

    public void DisposeDevice()
    {
        Config.Current.PropertyChanged -= OnConfigChanged;
        device?.WaitIdle();

        renderSettingsBuffer?.Dispose();
        renderSettingsBuffer = null;

        gui?.Dispose();
        gui = null;

        capture?.Dispose();
        capture = null;

        composite?.Dispose();
        composite = null;

        fxaa?.Dispose();
        fxaa = null;

        bloomPass?.Dispose();
        bloomPass = null;

        billboards?.Dispose();
        billboards = null;

        probePass?.Dispose();
        probePass = null;

        skyboxPass?.Dispose();
        skyboxPass = null;

        sceneTargets?.Dispose();
        sceneTargets = null;

        voxelPass?.Dispose();
        voxelPass = null;

        modelPass?.Dispose();
        modelPass = null;

        instanceBuffers?.Dispose();
        instanceBuffers = null;

        models?.Dispose();
        models = null;

        actors.Clear();
        textures?.Dispose();
        textures = null;

        gpu?.Dispose();
        gpu = null;

        frameLoop?.Dispose();
        frameLoop = null;

        swapchain?.Dispose();
        swapchain = null;

        device?.Dispose();
        device = null;

        if (surface.IsNotNull)
        {
            unsafe { instance.Api.vkDestroySurfaceKHR(surface, null); }
            surface = VkSurfaceKHR.Null;
        }

        instance?.Dispose();
        instance = null;
    }

    public bool SettingsChanged(MyRenderDeviceSettings newSettings) => !settings.Equals(ref newSettings);

    public void ApplySettings(MyRenderDeviceSettings newSettings)
    {
        RenderLog.Info($"ApplySettings: {newSettings}");

        var resize = newSettings.BackBufferWidth != settings.BackBufferWidth
                     || newSettings.BackBufferHeight != settings.BackBufferHeight;

        settings = newSettings;
        ForwardWindowMode(force: false);

        if (resize)
            RecreateSwapchain();
    }

    // Same-mode size changes flow from the window to the backbuffer only (LinuxCompat reports SDL window
    // resizes through the game's SwitchSettings). Forwarding them back to the window would create a
    // HiDPI resize feedback loop, so only window mode transitions are forwarded.
    private void ForwardWindowMode(bool force)
    {
        // The adapter is the game's monitor/output choice: changing it must retarget the window
        // even when the window mode and resolution stay the same.
        if (!force && lastForwardedWindowMode == settings.WindowMode && lastForwardedAdapter == settings.AdapterOrdinal)
            return;

        if (settings.AdapterOrdinal < 0 || settings.AdapterOrdinal >= adapters.Length)
            return;

        lastForwardedWindowMode = settings.WindowMode;
        lastForwardedAdapter = settings.AdapterOrdinal;
        try
        {
            RenderLog.Info($"Forwarding window mode {settings.WindowMode} on adapter {settings.AdapterOrdinal}: " +
                           $"{adapters[settings.AdapterOrdinal].Name}, desktop {adapters[settings.AdapterOrdinal].DesktopBounds}");
            window.OnModeChanged(settings, adapters[settings.AdapterOrdinal]);
        }
        catch (Exception e)
        {
            RenderLog.Error("Forwarding the window mode change failed", e);
        }
    }

    private void RecreateSwapchain()
    {
        if (swapchain == null)
            return;

        swapchain.Recreate(new Vector2I(settings.BackBufferWidth, settings.BackBufferHeight), Config.Current.VSync);
        frameLoop.OnSwapchainRecreated();
    }

    public long GetAvailableTextureMemory() =>
        settings.AdapterOrdinal >= 0 && settings.AdapterOrdinal < adapters.Length
            ? (long)adapters[settings.AdapterOrdinal].VRAM
            : 0;

    #endregion

    private static float ParseDebugFloat(string name, float fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return float.TryParse(value, out var result) && result > 0f ? result : fallback;
    }

    // Development/test helper for visual verification without user input: VULKAN_DEBUG_LOOKAT=<model
    // substring> frames the matching actor nearest to the current camera from the sunlit (or
    // VULKAN_DEBUG_DARK=1 shadowed) side. VULKAN_DEBUG_DISTANCE scales the framing distance and
    // VULKAN_DEBUG_TOP=1 looks straight down instead.
    private void ApplyDebugCamera()
    {
        if (!hasEnvironment)
            return;

        if (debugVoxel)
        {
            if (!hasDebugVoxel)
            {
                if (voxelPass == null || !voxelPass.TryGetNearestActor(camera.Position,
                        out debugVoxelCenter, out debugVoxelRadius))
                    return;
                hasDebugVoxel = true;
            }
            var distance = Math.Max(40f, debugVoxelRadius * 2.5f * debugLookAtDistance);
            var voxelEye = debugVoxelCenter + Vector3D.Up * distance +
                           new Vector3D(0, 0, -1) * distance * 0.25;
            camera.SetDebugLookAt(voxelEye, debugVoxelCenter, BackBufferResolution);
            if (!reportedDebugLookAt)
            {
                reportedDebugLookAt = true;
                RenderLog.Info($"Debug voxel camera: eye {voxelEye}, target {debugVoxelCenter}, radius {debugVoxelRadius}");
            }
            return;
        }

        if (string.IsNullOrEmpty(debugLookAtModel))
            return;

        // Choose the target once, using the game's saved camera position; later frames run with the
        // debug camera and would otherwise pick a different block every frame.
        if (debugTarget == null)
        {
            var bestDistance = double.MaxValue;
            foreach (var actor in actors.All)
            {
                if (actor.Model == null)
                    continue;
                if (debugLookAtModel == "*")
                {
                    // Wildcard picks the nearest non-character mesh, normally the platform block
                    // under the player; characters would otherwise win and fill the frame.
                    if (actor.SkeletonBones != null)
                        continue;
                }
                else if (!actor.Model.Contains(debugLookAtModel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var model = models.Get(actor.Model);
                if (model == null)
                    continue;

                var world = actors.GetWorldMatrix(actor);
                var localCenter = (model.Bounds.Min + model.Bounds.Max) * 0.5f;
                var center = (Vector3D)Vector3.Transform(localCenter, (Matrix)world);
                var distance = (center - camera.Position).LengthSquared();
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                debugTarget = actor;
                debugTargetCenter = center;
            }
        }

        if (debugTarget == null)
            return;

        var target = debugTarget;
        var targetCenter = debugTargetCenter;
        var sun = (Vector3)environment.EnvironmentLight.SunLightDirection;
        if (sun.LengthSquared() < 1e-6f)
            sun = new Vector3(0.4f, 0.8f, 0.2f);
        sun.Normalize();
        var direction = debugLookAtDark ? sun : -sun;
        var eye = debugLookAtTop
            ? targetCenter + Vector3D.Up * debugLookAtDistance
            : targetCenter + (Vector3D)direction * debugLookAtDistance + Vector3D.Up * 1.5;
        camera.SetDebugLookAt(eye, targetCenter, BackBufferResolution);

        if (!reportedDebugLookAt)
        {
            reportedDebugLookAt = true;
            RenderLog.Info($"Debug camera: model '{target.Model}' actor {target.Id}, eye {eye}, target {targetCenter}, " +
                           $"{(debugLookAtTop ? "top" : debugLookAtDark ? "dark" : "sun")} side, distance {debugLookAtDistance}");
        }
    }

    #region Frame

    public void Draw(bool draw = true)
    {
        // The frame slot must be free (fence waited) before messages run: texture uploads issued while
        // processing them are written into this slot's staging memory.
        var frameStarted = false;
        var workStart = 0L;
        if (draw && frameLoop != null)
        {
            LimitFrameRate();
            if (frameLoop.SwapchainOutdated)
                RecreateSwapchain();

            frameStarted = frameLoop.TryBeginFrame();
            if (frameStarted)
            {
                var now = frameClock.Elapsed.TotalSeconds;
                var delta = now - lastFrameTime;
                lastFrameTime = now;
                if (delta > 0 && delta < 1)
                    framesPerSecond = framesPerSecond * 0.9 + (1 / delta) * 0.1;

                workStart = Stopwatch.GetTimestamp();
                frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
                gpu.BeginFrame(frameLoop.FrameSlot);
                gui.BeginFrame(frameLoop.FrameSlot);
                drawScene = false;
            }
        }

        // Without a frame to draw into, only state changes are applied; the draw messages of this
        // update frame are processed with the next frame that is drawn.
        var stageStart = Stopwatch.GetTimestamp();
        messages.Process(frameStarted);

        if (!frameStarted)
            return;

        if (drawScene)
            TestNotification.ShowIfRequested();

        textures.ProcessDecoded();
        models.ProcessReady();
        instanceBuffers.ProcessPending();
        ApplyDebugCamera();
        // Voxel clipmaps must keep loading even while the game shows the loading/spawn GUI and
        // drawScene is false. Run before Uploads.Record so this frame's cell buffers are uploaded
        // before the opaque pass consumes them.
        if (camera.HasValue)
            voxelPass?.Update(camera);
        lastMessagesMs = Ms(stageStart);

        stageStart = Stopwatch.GetTimestamp();

        // Slider changes only mark a flag; the GPU buffer is refreshed on the render thread and the
        // shaders read it directly. No per-frame Config reads or tuning uploads.
        if (Interlocked.Exchange(ref renderSettingsDirty, 0) != 0)
            UploadRenderSettings();

        var commands = frameLoop.Commands;
        var transferValue = gpu.Transfer.Submit(commands);
        gpu.Uploads.Record(commands);
        lastUploadsMs = Ms(stageStart);
        stageStart = Stopwatch.GetTimestamp();
        gui.RecordOffscreen(commands);
        if (drawScene)
            RecordScene(commands);
        if (drawScene)
        {
            exposurePass.Record(commands, sceneTargets.ColorSlot, MyPostprocessSettings.Default);
            bloomPass.Record(commands, sceneTargets.ColorSlot, sceneTargets.OitAccumSlot,
                sceneTargets.OitRevealSlot, sceneTargets.Extent);
        }
        var counters = modelPass?.Diagnostics ?? default;
        gui.SetDiagnostics(new DiagnosticsStats
        {
            Visible = Config.Current.Diagnostics,
            FrameMilliseconds = frameMilliseconds,
            RawFrameMilliseconds = rawFrameMilliseconds,
            ProbeCpuMs = probePass?.LastCpuMs ?? 0,
            Fps = framesPerSecond,
            Triangles = counters.Triangles,
            Parts = counters.Parts,
            Instances = counters.Instances,
            Culled = counters.CulledActors,
            GpuVisible = counters.VisibleActors,
            DrawnActors = lastDrawnActors,
            CulledActors = lastCulledActors,
            GpuParts = modelPass?.GpuPartCount ?? 0,
            DirectTriangles = modelPass?.LastDirectTriangles ?? 0,
            TextRuns = gui.GpuTextRuns,
            TextGlyphs = gui.GpuTextGlyphs,
            GpuMemoryBytes = gpu.Memory.DeviceLocalAllocatedBytes,
            GpuMemoryTotalBytes = gpu.Memory.DeviceLocalTotalBytes,
        });
        // GPU text layout must run outside the backbuffer rendering pass, after all runs are queued.
        gui.RecordTextLayout(commands);
        RecordFrame(commands);
        lastGuiMs = Ms(stageStart);
        frameLoop.EndFrame(transferValue, gpu.Transfer.Timeline);
        var workMs = (Stopwatch.GetTimestamp() - workStart) * 1000.0 / Stopwatch.Frequency;
        rawFrameMilliseconds = workMs;
        frameMilliseconds = frameMilliseconds * 0.9 + workMs * 0.1;

        var allocBytes = GC.GetAllocatedBytesForCurrentThread() - frameAllocStart;
        if (workMs > 20)
        {
            RenderLog.Warning($"Frame hitch {workMs:0.0} ms: msg {lastMessagesMs:0.0}, upload {lastUploadsMs:0.0}, " +
                              $"skin {modelPass?.LastSkinningMs ?? 0:0.0}, prepare {modelPass?.LastPrepareMs ?? 0:0.0} " +
                              $"(cache {modelPass?.LastEnsureMs ?? 0:0.0}), " +
                              $"record {modelPass?.LastRecordMs ?? 0:0.0}, transparent {modelPass?.LastTransparentMs ?? 0:0.0}, " +
                              $"probe {probePass?.LastCpuMs ?? 0:0.0}, " +
                              $"gui {lastGuiMs:0.0}; alloc {allocBytes / 1024.0:0.0} KB, " +
                              $"gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
        }
    }

    private static double Ms(long start) =>
        (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    private void LimitFrameRate()
    {
        var fps = Config.Current.FrameLimit switch
        {
            FrameLimitKind.Fps30 => 30.0,
            FrameLimitKind.Fps60 => 60.0,
            FrameLimitKind.Fps120 => 120.0,
            FrameLimitKind.Fps240 => 240.0,
            _ => 0.0,
        };
        if (fps <= 0)
        {
            nextFrameTime = 0;
            return;
        }

        var now = frameLimitClock.Elapsed.TotalSeconds;
        if (nextFrameTime == 0 || nextFrameTime < now - 1)
            nextFrameTime = now;
        nextFrameTime += 1.0 / fps;

        if (nextFrameTime <= now)
        {
            nextFrameTime = now + 1.0 / fps;
            return;
        }

        var wait = nextFrameTime - now;
        if (wait > 0.0015)
            Thread.Sleep((int)((wait - 0.001) * 1000));
        while (frameLimitClock.Elapsed.TotalSeconds < nextFrameTime)
            Thread.SpinWait(100);
    }

    // The 3D scene into the HDR targets. No geometry yet: clears to the space background.
    private static VkSampleCountFlags ToSamples(int level) => level switch
    {
        <= 0 => VkSampleCountFlags.Count1,
        1 => VkSampleCountFlags.Count2,
        2 => VkSampleCountFlags.Count4,
        _ => VkSampleCountFlags.Count8,
    };

    private static uint ProbeResolution(int quality) => quality switch
    {
        <= 0 => 128,
        1 => 256,
        _ => 512,
    };

    private unsafe void RecordScene(VkCommandBuffer commands)
    {
        var api = device.Api;
        var resolution = BackBufferResolution;
        var renderScale = Math.Clamp(Config.Current.RenderScale, 1f, 2f);
        sceneTargets.EnsureSize((uint)Math.Max(1, resolution.X * renderScale),
            (uint)Math.Max(1, resolution.Y * renderScale), sceneSamples);
        var extent = sceneTargets.Extent;

        frameLoop.Barrier(sceneTargets.Color.Handle, sceneTargets.Color.FullRange,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead, sceneTargets.ColorLayout,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
        frameLoop.Barrier(sceneTargets.GBuffer.Handle, sceneTargets.GBuffer.FullRange,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, sceneTargets.GBufferLayout,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
        frameLoop.Barrier(sceneTargets.GBuffer2.Handle, sceneTargets.GBuffer2.FullRange,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, sceneTargets.GBuffer2Layout,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
        if (sceneSamples != VkSampleCountFlags.Count1)
        {
            frameLoop.Barrier(sceneTargets.ColorMs.Handle, sceneTargets.ColorMs.FullRange,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkImageLayout.Undefined,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
            frameLoop.Barrier(sceneTargets.GBufferMs.Handle, sceneTargets.GBufferMs.FullRange,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkImageLayout.Undefined,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
            frameLoop.Barrier(sceneTargets.GBuffer2Ms.Handle, sceneTargets.GBuffer2Ms.FullRange,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkImageLayout.Undefined,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
            frameLoop.Barrier(sceneTargets.DepthMs.Handle, sceneTargets.DepthMs.FullRange,
                VkPipelineStageFlags2.FragmentShader | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.None, VkImageLayout.Undefined,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.DepthStencilAttachmentWrite, VkImageLayout.DepthAttachmentOptimal);
        }
        frameLoop.Barrier(sceneTargets.Depth.Handle, sceneTargets.Depth.FullRange,
            VkPipelineStageFlags2.FragmentShader | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.None, VkImageLayout.Undefined,
            VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.DepthStencilAttachmentWrite, VkImageLayout.DepthAttachmentOptimal);

        var colorAttachments = stackalloc VkRenderingAttachmentInfo[3];
        var multisampled = sceneSamples != VkSampleCountFlags.Count1;
        colorAttachments[0] = new VkRenderingAttachmentInfo
        {
            imageView = multisampled ? sceneTargets.ColorMs.View : sceneTargets.Color.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            resolveMode = multisampled ? VkResolveModeFlags.Average : VkResolveModeFlags.None,
            resolveImageView = multisampled ? sceneTargets.Color.View : default,
            resolveImageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0.004f, 0.005f, 0.009f, 1f) },
        };
        colorAttachments[1] = new VkRenderingAttachmentInfo
        {
            imageView = multisampled ? sceneTargets.GBufferMs.View : sceneTargets.GBuffer.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            resolveMode = multisampled ? VkResolveModeFlags.Average : VkResolveModeFlags.None,
            resolveImageView = multisampled ? sceneTargets.GBuffer.View : default,
            resolveImageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0f, 0f, 0f, 0f) },
        };
        colorAttachments[2] = new VkRenderingAttachmentInfo
        {
            imageView = multisampled ? sceneTargets.GBuffer2Ms.View : sceneTargets.GBuffer2.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            resolveMode = multisampled ? VkResolveModeFlags.Average : VkResolveModeFlags.None,
            resolveImageView = multisampled ? sceneTargets.GBuffer2.View : default,
            resolveImageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0f, 0f, 0f, 0f) },
        };
        sceneTargets.ColorLayout = VkImageLayout.ColorAttachmentOptimal;
        sceneTargets.GBufferLayout = VkImageLayout.ColorAttachmentOptimal;
        sceneTargets.GBuffer2Layout = VkImageLayout.ColorAttachmentOptimal;
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            imageView = multisampled ? sceneTargets.DepthMs.View : sceneTargets.Depth.View,
            imageLayout = VkImageLayout.DepthAttachmentOptimal,
            resolveMode = multisampled ? VkResolveModeFlags.SampleZero : VkResolveModeFlags.None,
            resolveImageView = multisampled ? sceneTargets.Depth.View : default,
            resolveImageLayout = VkImageLayout.DepthAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(0f, 0) }, // Reverse Z: far = 0
        };
        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = 3,
            pColorAttachments = colorAttachments,
            pDepthAttachment = &depthAttachment,
        };

        lastCulledActors = 0;
        lastDrawnActors = 0;
        var sceneReady = camera.HasValue && hasEnvironment;
        var frameConstants = 0ul;
        if (sceneReady)
        {
            // Compute skinning, GPU culling and the shadow depth pass must run outside dynamic rendering.
            modelPass.RecordSkinning(commands, camera, loddingSettings, actors);
            modelPass.RecordPrepare(commands, frameLoop.FrameSlot, camera, loddingSettings, actors,
                environment.EnvironmentLight.SunLightDirection);
            modelPass.RecordShadow(commands, camera);
            frameConstants = PrepareFrameConstants(environment);
            lastFrameConstants = frameConstants;
        }

        api.vkCmdBeginRendering(commands, &renderingInfo);
        if (sceneReady)
        {
            skyboxPass.Record(commands, sceneTargets, frameConstants);
            lastDrawnActors = modelPass.Record(commands, sceneTargets, camera, loddingSettings, actors,
                frameConstants, out lastCulledActors);
            voxelPass?.Record(commands, sceneTargets, camera, frameConstants);
        }

        api.vkCmdEndRendering(commands);

        // The opaque scene is the source for screen-space reflections in transparent materials and
        // in the composite pass; move it to a sampled layout before the OIT pass.
        if (sceneReady)
        {
            frameLoop.Barrier(sceneTargets.Color.Handle, sceneTargets.Color.FullRange,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                VkImageLayout.ShaderReadOnlyOptimal);
            frameLoop.Barrier(sceneTargets.GBuffer.Handle, sceneTargets.GBuffer.FullRange,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                VkImageLayout.ShaderReadOnlyOptimal);
            frameLoop.Barrier(sceneTargets.GBuffer2.Handle, sceneTargets.GBuffer2.FullRange,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                VkImageLayout.ShaderReadOnlyOptimal);
            sceneTargets.ColorLayout = VkImageLayout.ShaderReadOnlyOptimal;
            sceneTargets.GBufferLayout = VkImageLayout.ShaderReadOnlyOptimal;
            sceneTargets.GBuffer2Layout = VkImageLayout.ShaderReadOnlyOptimal;

            var depthBarrier = new VkImageMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.LateFragmentTests | VkPipelineStageFlags2.EarlyFragmentTests,
                srcAccessMask = VkAccessFlags2.DepthStencilAttachmentWrite,
                dstStageMask = VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                dstAccessMask = VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite,
                oldLayout = VkImageLayout.DepthAttachmentOptimal,
                newLayout = VkImageLayout.DepthAttachmentOptimal,
                image = sceneTargets.Depth.Handle,
                subresourceRange = sceneTargets.Depth.FullRange,
                srcQueueFamilyIndex = uint.MaxValue,
                dstQueueFamilyIndex = uint.MaxValue,
            };
            var depthDependency = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &depthBarrier };
            api.vkCmdPipelineBarrier2(commands, &depthDependency);

            frameLoop.Barrier(sceneTargets.OitAccum.Handle, sceneTargets.OitAccum.FullRange,
                sceneTargets.OitAccumLayout == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.FragmentShader,
                sceneTargets.OitAccumLayout == VkImageLayout.Undefined ? VkAccessFlags2.None : VkAccessFlags2.ShaderSampledRead,
                sceneTargets.OitAccumLayout,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal);
            frameLoop.Barrier(sceneTargets.OitReveal.Handle, sceneTargets.OitReveal.FullRange,
                sceneTargets.OitRevealLayout == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.FragmentShader,
                sceneTargets.OitRevealLayout == VkImageLayout.Undefined ? VkAccessFlags2.None : VkAccessFlags2.ShaderSampledRead,
                sceneTargets.OitRevealLayout,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal);
            sceneTargets.OitAccumLayout = VkImageLayout.ColorAttachmentOptimal;
            sceneTargets.OitRevealLayout = VkImageLayout.ColorAttachmentOptimal;

            var oitAttachments = stackalloc VkRenderingAttachmentInfo[2];
            oitAttachments[0] = new VkRenderingAttachmentInfo
            {
                imageView = sceneTargets.OitAccum.View,
                imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue { color = new VkClearColorValue(0f, 0f, 0f, 0f) },
            };
            oitAttachments[1] = new VkRenderingAttachmentInfo
            {
                imageView = sceneTargets.OitReveal.View,
                imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue { color = new VkClearColorValue(1f, 0f, 0f, 0f) },
            };
            var oitDepth = new VkRenderingAttachmentInfo
            {
                imageView = sceneTargets.Depth.View,
                imageLayout = VkImageLayout.DepthAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Load,
                storeOp = VkAttachmentStoreOp.Store,
            };
            var oitInfo = new VkRenderingInfo
            {
                renderArea = new VkRect2D(0, 0, extent.width, extent.height),
                layerCount = 1,
                colorAttachmentCount = 2,
                pColorAttachments = oitAttachments,
                pDepthAttachment = &oitDepth,
            };

            api.vkCmdBeginRendering(commands, &oitInfo);
            if (Environment.GetEnvironmentVariable("VULKAN_NO_TRANSPARENT") != "1")
            {
                lastDrawnActors += modelPass.RecordTransparent(commands, sceneTargets, frameConstants);
                billboards.Record(commands, sceneTargets, camera, actors, frameConstants, sharedData, thrusterEffects);
            }
            api.vkCmdEndRendering(commands);

            frameLoop.Barrier(sceneTargets.OitAccum.Handle, sceneTargets.OitAccum.FullRange,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                VkImageLayout.ShaderReadOnlyOptimal);
            frameLoop.Barrier(sceneTargets.OitReveal.Handle, sceneTargets.OitReveal.FullRange,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
                VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
                VkImageLayout.ShaderReadOnlyOptimal);
            sceneTargets.OitAccumLayout = VkImageLayout.ShaderReadOnlyOptimal;
            sceneTargets.OitRevealLayout = VkImageLayout.ShaderReadOnlyOptimal;
        }

    }

    private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        if (renderSettingsBuffer != null)
            Interlocked.Exchange(ref renderSettingsDirty, 1);
    }

    private unsafe void UploadRenderSettings()
    {
        if (renderSettingsBuffer == null)
            return;

        var noReflections = Environment.GetEnvironmentVariable("VULKAN_NO_REFLECTIONS") == "1";
        var noSsr = Environment.GetEnvironmentVariable("VULKAN_NO_SSR") == "1" ||
                    !Config.Current.ScreenSpaceReflections;
        var settings = new GpuRenderSettings
        {
            Tuning0 = new Vector4(Config.Current.SunScale, Config.Current.AmbientScale,
                Config.Current.SpecularScale, Config.Current.MetalnessScale),
            Tuning1 = new Vector4(Config.Current.EmissiveScale, Config.Current.LightScale,
                noReflections ? 0f : Config.Current.ReflectionScale, Config.Current.ScreenScale),
            Post = new Vector4(Config.Current.ExposureScale, Config.Current.BloomScale,
                noSsr ? -1f : 0f, Config.Current.ReflectionQuality),
        };
        *(GpuRenderSettings*)renderSettingsBuffer.Mapped = settings;
    }

    // Resolves the environment cubemaps and writes this frame's constants. The indirect (prefiltered)
    // skybox is the IBL source; until it is ready the direct skybox's mips are used as a fallback.
    private ulong PrepareFrameConstants(MyEnvironmentData environment)
    {
        var skyboxName = string.IsNullOrEmpty(environment.Skybox) ? null : NormalizeAssetPath(environment.Skybox);
        var skyboxEntry = skyboxName != null
            ? textures.Get(skyboxName, waitTillLoaded: false, TextureCache.TextureKind.Skybox)
            : null;

        TextureCache.Entry iblEntry = null;
        if (skyboxName != null)
        {
            var indirectName = Path.Combine(MyFileSystem.ContentPath, IndirectSkyboxPath(skyboxName));
            if (MyFileSystem.FileExists(indirectName))
                iblEntry = textures.Get(indirectName, waitTillLoaded: false, TextureCache.TextureKind.Skybox);
            if (iblEntry is not { Status: TextureCache.State.Ready })
                iblEntry = skyboxEntry;
        }

        var brdf = textures.Get("Textures/Miscellaneous/ambient_brdf.dds", waitTillLoaded: false,
            TextureCache.TextureKind.Data);

        var skyboxSlot = TextureCache.CubeSlotOf(skyboxEntry);
        var iblSlot = TextureCache.CubeSlotOf(iblEntry);
        var iblMips = TextureCache.MipLevelsOf(iblEntry);
        var brdfSlot = TextureCache.SlotOf(brdf);
        lastStaticIblSlot = iblSlot;
        lastStaticIblMips = iblMips;

        var constants = camera.BuildFrameConstants(environment, skyboxSlot, iblSlot, brdfSlot, iblMips);
        // Live reflection quality blend. The probe stores a full close cube; distant receivers
        // sample progressively blurrier mips instead of switching to a separate sky-only cube.
        var blendStart = Math.Max(1f, Config.Current.ReflectionBlendStart);
        var blendEnd = Math.Max(blendStart + 1f, Config.Current.ReflectionBlendEnd);
        constants.ReflectionParams = new Vector4(blendStart, blendEnd,
            Math.Clamp(Config.Current.ReflectionFarMipBias, 0f, 4f),
            Math.Clamp(Config.Current.ReflectionMinMip, 0f, 4f));
        constants.EmissiveScale = MyPostprocessSettings.Default.Data.BloomEmissiveness;
        var thrusterLights = thrusterEffects.BuildLights(camera.Position, actors);
        if (Environment.GetEnvironmentVariable("VULKAN_DEBUG_LIGHTS") == "1" && thrusterLights.Count > 0)
        {
            var l = thrusterLights[0];
            RenderLog.Info($"Thruster lights: {thrusterLights.Count}, first pos={l.PositionRange.X:F1},{l.PositionRange.Y:F1},{l.PositionRange.Z:F1} range={l.PositionRange.W:F2} color={l.ColorFalloff.X:F2},{l.ColorFalloff.Y:F2},{l.ColorFalloff.Z:F2} falloff={l.ColorFalloff.W:F2}");
        }
        var lightData = sceneLights.Build(gpu, camera.Position, actors, Config.Current.EffectsDistance,
            thrusterLights);
        constants.Lights = lightData.Address;
        constants.LightCount = lightData.Count;
        constants.ShadowParams = new Vector4(1f, 0.00005f, 1f, 4f);
        constants.SceneColor = sceneTargets.ColorSlot;
        constants.SceneDepth = sceneTargets.DepthSlot;
        constants.SceneGBuffer = sceneTargets.GBufferSlot;

        if (!probeDisabled && probePass is { HasActive: true })
        {
            constants.IblCube = probePass.ActiveSlot;
            constants.IblCubePrev = probePass.PreviousSlot;
            constants.IblBlend = probePass.Blend;
            constants.IblProbe = 1;
            constants.SkyboxParams = new Vector4(constants.SkyboxParams.X, probePass.MipLevels,
                constants.SkyboxParams.Z, constants.SkyboxParams.W);
        }

        if (modelPass.ShadowsReady && modelPass.ShadowPassRecorded)
        {
            var shadow0 = Matrix.Transpose(modelPass.ShadowViewProjectionAt(0));
            var shadow1 = Matrix.Transpose(modelPass.ShadowViewProjectionAt(1));
            var shadow2 = Matrix.Transpose(modelPass.ShadowViewProjectionAt(2));
            var shadow3 = Matrix.Transpose(modelPass.ShadowViewProjectionAt(3));
            constants.ShadowMatrix0 = new Vector4(shadow0.M11, shadow0.M12, shadow0.M13, shadow0.M14);
            constants.ShadowMatrix1 = new Vector4(shadow0.M21, shadow0.M22, shadow0.M23, shadow0.M24);
            constants.ShadowMatrix2 = new Vector4(shadow0.M31, shadow0.M32, shadow0.M33, shadow0.M34);
            constants.ShadowMatrix3 = new Vector4(shadow0.M41, shadow0.M42, shadow0.M43, shadow0.M44);
            constants.ShadowMatrix1_0 = new Vector4(shadow1.M11, shadow1.M12, shadow1.M13, shadow1.M14);
            constants.ShadowMatrix1_1 = new Vector4(shadow1.M21, shadow1.M22, shadow1.M23, shadow1.M24);
            constants.ShadowMatrix1_2 = new Vector4(shadow1.M31, shadow1.M32, shadow1.M33, shadow1.M34);
            constants.ShadowMatrix1_3 = new Vector4(shadow1.M41, shadow1.M42, shadow1.M43, shadow1.M44);
            constants.ShadowMatrix2_0 = new Vector4(shadow2.M11, shadow2.M12, shadow2.M13, shadow2.M14);
            constants.ShadowMatrix2_1 = new Vector4(shadow2.M21, shadow2.M22, shadow2.M23, shadow2.M24);
            constants.ShadowMatrix2_2 = new Vector4(shadow2.M31, shadow2.M32, shadow2.M33, shadow2.M34);
            constants.ShadowMatrix2_3 = new Vector4(shadow2.M41, shadow2.M42, shadow2.M43, shadow2.M44);
            constants.ShadowMatrix3_0 = new Vector4(shadow3.M11, shadow3.M12, shadow3.M13, shadow3.M14);
            constants.ShadowMatrix3_1 = new Vector4(shadow3.M21, shadow3.M22, shadow3.M23, shadow3.M24);
            constants.ShadowMatrix3_2 = new Vector4(shadow3.M31, shadow3.M32, shadow3.M33, shadow3.M34);
            constants.ShadowMatrix3_3 = new Vector4(shadow3.M41, shadow3.M42, shadow3.M43, shadow3.M44);
            constants.ShadowMap = modelPass.ShadowSlotAt(0);
            constants.ShadowMap1 = modelPass.ShadowSlotAt(1);
            constants.ShadowMap2 = modelPass.ShadowSlotAt(2);
            constants.ShadowMap3 = modelPass.ShadowSlotAt(3);
            constants.ShadowSize = ShadowMap.Size;
            constants.ShadowEnabled = 1;
            var splits = ModelPass.ShadowSplitEnds;
            constants.ShadowSplits = new Vector4(splits[0], splits[1], splits[2], splits[3]);
            constants.ShadowCascadeParams = new Vector4(
                modelPass.ShadowExtentAt(0), modelPass.ShadowExtentAt(1), modelPass.ShadowExtentAt(2),
                modelPass.ShadowExtentAt(3));
        }

        lastFrameConstantsData = constants;
        var allocation = gpu.Transient.Allocate<FrameConstants>();
        allocation.As<FrameConstants>() = constants;

        return allocation.Address;
    }

    // Stock Extensions.ToFileTextureEnum priority: Alphamask, ColorMetal, Extensions, NormalGloss,
    // then the GUI/particle kinds. Particles and GUI-without-premultiply are plain sRGB here.
    private static TextureCache.TextureKind TextureKindOf(TextureType type)
    {
        if ((type & TextureType.AlphaMask) != 0) return TextureCache.TextureKind.Alphamask;
        if ((type & TextureType.ColorMetal) != 0) return TextureCache.TextureKind.ColorMetal;
        if ((type & TextureType.Extensions) != 0) return TextureCache.TextureKind.Extensions;
        if ((type & TextureType.NormalGloss) != 0) return TextureCache.TextureKind.NormalGloss;
        if ((type & TextureType.CubeMap) != 0) return TextureCache.TextureKind.Skybox;
        if ((type & TextureType.GUI) != 0) return TextureCache.TextureKind.Gui;
        return TextureCache.TextureKind.ColorMetal;
    }

    private static string NormalizeAssetPath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    // MyRender11: the prefiltered companion of BackgroundCube.dds is BackgroundCubeIndirect.dds
    private static string IndirectSkyboxPath(string skybox)
    {
        var path = NormalizeAssetPath(skybox);
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path) + "Indirect" + Path.GetExtension(path);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    private static bool IsSrgb(VkFormat format) =>
        format is VkFormat.B8G8R8A8Srgb or VkFormat.R8G8B8A8Srgb;

    private unsafe void RecordFrame(VkCommandBuffer commands)
    {
        probePass?.BeginFrame();
        var api = device.Api;
        var image = frameLoop.BackbufferImage;
        var extent = frameLoop.BackbufferExtent;
        var range = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);

        // The acquired swapchain image was read by vkAcquireNextImageKHR; the layout transition
        // is a write, so it must be ordered after both the graphics and transfer stages included
        // in the acquire semaphore wait.
        frameLoop.Barrier(image, range,
            VkPipelineStageFlags2.AllCommands, VkAccessFlags2.None, VkImageLayout.Undefined,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);

        // No world rendering yet: the GUI goes over a black screen, like the stock renderer without a scene
        var colorAttachment = new VkRenderingAttachmentInfo
        {
            imageView = frameLoop.BackbufferView,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0f, 0f, 0f, 1f) },
        };
        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
        };

        if (drawScene)
        {
            composite ??= new CompositePass(gpu);
            composite.Record(commands, sceneTargets, extent, lastFrameConstants, exposurePass.Slot, bloomPass.Slot,
                modelPass.ShadowSlot, debugShadowMap ? 1u : (debugSsr ? 2u : (debugSsrColor ? 3u : 0u)),
                MyPostprocessSettings.Default.Data.WhitePoint);

            if (fxaa == null || compositeFormat != swapchain.Format)
            {
                fxaa?.Dispose();
                fxaa = new FxaaPass(gpu, swapchain.Format);
                compositeFormat = swapchain.Format;
            }
        }

        api.vkCmdBeginRendering(commands, &renderingInfo);
        if (drawScene)
        {
            var antialiasing = Config.Current.Fxaa;
            var fxaaQuality = Math.Clamp(Config.Current.FxaaQuality, 1, 4) - 1;
            fxaa.Record(commands, composite.SourceSlot, extent, antialiasing, IsSrgb(swapchain.Format), fxaaQuality);
        }

        gui.RecordMain(commands, swapchain.Format, BackBufferResolution, extent);
        api.vkCmdEndRendering(commands);

        if (drawScene && lastFrameConstants != 0)
        {
            // Whole-cube updates every frame keep reflections live (moving ships, thrusters, lights)
            // without the old 2/4/8 Hz stepping. The lower default resolution and single-sample,
            // shadow-less capture keep the per-frame cost bounded.
            probePass?.RecordCube(commands, frameLoop.FrameSlot, camera, loddingSettings, actors,
                lastFrameConstantsData, sharedData, thrusterEffects, lastStaticIblSlot, lastStaticIblMips);
        }

        if (capture != null && capture.ShouldCapture)
            capture.Record(commands, frameLoop, swapchain.Format);

        frameLoop.Barrier(image, range,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.None, VkAccessFlags2.None, VkImageLayout.PresentSrcKHR);
    }

    public void Present()
    {
        frameLoop?.Present();
        capture?.Complete();
    }

    #endregion

    #region Messages

    public void EnqueueMessage(MyRenderMessageBase message, bool limitMaxQueueSize)
    {
        if (System.Threading.Thread.CurrentThread == MyRenderProxy.RenderSystemThread)
            sharedData.MessagesForNextFrame.Enqueue(message);
        else
            sharedData.CurrentUpdateFrame.Enqueue(message);
    }

    public void EnqueueOutputMessage(MyRenderMessageBase message)
    {
        sharedData.RenderOutputMessageQueue.Enqueue(message);
    }

    private void ProcessMessage(MyRenderMessageBase message)
    {
        if (gui != null && gui.ProcessMessage(message))
            return;

        switch (message.MessageType)
        {
            // The game preloads block/mod assets during the loading screen. Starting the background
            // decode/parse here lets the GPU-side creation happen before the world becomes interactive.
            case MyRenderMessageEnum.PreloadTextures:
            {
                var m = (MyRenderMessagePreloadTextures)message;
                var kind = TextureKindOf(m.TextureType);
                foreach (var file in m.Files)
                    textures.Get(NormalizeAssetPath(file), waitTillLoaded: false, kind);
                break;
            }

            case MyRenderMessageEnum.PreloadModels:
            {
                var m = (MyRenderMessagePreloadModels)message;
                foreach (var model in m.Models)
                    models?.StartLoading(model);
                break;
            }

            case MyRenderMessageEnum.PreloadModel:
                models?.StartLoading(((MyRenderMessagePreloadModel)message).Name);
                break;

            case MyRenderMessageEnum.PreloadMaterials:
                models?.StartLoading(((MyRenderMessagePreloadMaterials)message).Name);
                break;

            case MyRenderMessageEnum.CreateRenderLight:
                sceneLights.Create(((MyRenderMessageCreateRenderLight)message).ID);
                break;

            case MyRenderMessageEnum.CreateRenderVoxelMaterials:
            {
                var m = (MyRenderMessageCreateRenderVoxelMaterials)message;
                voxelPass?.SetMaterials(m.Materials);
                m.Materials = null;
                break;
            }

            case MyRenderMessageEnum.UpdateRenderVoxelMaterials:
            {
                var m = (MyRenderMessageUpdateRenderVoxelMaterials)message;
                voxelPass?.SetMaterials(m.Materials);
                m.Materials = null;
                break;
            }

            case MyRenderMessageEnum.PreloadVoxelMaterials:
                // Materials are uploaded when Create/UpdateRenderVoxelMaterials arrives.
                break;

            case MyRenderMessageEnum.VoxelCreate:
                voxelPass?.Create((MyRenderMessageVoxelCreate)message);
                break;

            case MyRenderMessageEnum.InvalidateClipmapRange:
            {
                var m = (MyRenderMessageInvalidateClipmapRange)message;
                voxelPass?.InvalidateRange(m.ClipmapId, m.MinCellLod0, m.MaxCellLod0);
                break;
            }

            case MyRenderMessageEnum.ClipmapsReady:
                break;

            case MyRenderMessageEnum.UpdatePostprocessSettings:
            {
                postprocessSettings = ((MyRenderMessageUpdatePostprocessSettings)message).Settings;
                break;
            }

            case MyRenderMessageEnum.UpdateRenderLight:
                sceneLights.Update(((MyRenderMessageUpdateRenderLight)message).Data);
                break;

            case MyRenderMessageEnum.UpdateRenderComponent:
                thrusterEffects.Process((MyRenderMessageUpdateComponent)message);
                break;

            case MyRenderMessageEnum.AddRuntimeModel:
                models?.AddRuntime((MyRenderMessageAddRuntimeModel)message);
                break;

            case MyRenderMessageEnum.UnloadTextures:
                foreach (var file in ((MyRenderMessageUnloadTextures)message).Files)
                    textures.Unload(NormalizeAssetPath(file));
                break;

            case MyRenderMessageEnum.DeprioritizeTextures:
                // No streaming priority: all textures use one unified cache; nothing to do.
                break;

            case MyRenderMessageEnum.CreateGeneratedTexture:
            {
                var m = (MyRenderMessageCreateGeneratedTexture)message;
                textures.CreateGenerated(m.TextureName, m.Width, m.Height, m.Type, m.GenerateMipMaps, m.Data, m.ImmediatelyReady);
                break;
            }

            case MyRenderMessageEnum.ResetGeneratedTexture:
            {
                var m = (MyRenderMessageResetGeneratedTexture)message;
                textures.ResetGenerated(m.TextureName, m.Data);
                break;
            }

            case MyRenderMessageEnum.DestroyGeneratedTexture:
                textures.DestroyGenerated(((MyRenderMessageDestroyGeneratedTexture)message).TextureName);
                break;

            case MyRenderMessageEnum.DrawCommands:
                foreach (var inner in ((MyRenderMessageDrawCommands)message).Messages)
                    ProcessMessage(inner);
                break;

            case MyRenderMessageEnum.VideoAdaptersRequest:
                MyRenderProxy.SendVideoAdapters(adapters);
                break;

            case MyRenderMessageEnum.SwitchDeviceSettings:
                MyRenderProxy.RenderThread.SwitchSettings(((MyRenderMessageSwitchDeviceSettings)message).Settings);
                break;

            case MyRenderMessageEnum.SwitchRenderSettings:
                renderSettings = ((MyRenderMessageSwitchRenderSettings)message).Settings;
                break;

            case MyRenderMessageEnum.UpdateNewLoddingSettings:
                loddingSettings = ((MyRenderMessageUpdateNewLoddingSettings)message).Settings;
                break;

            case MyRenderMessageEnum.UpdateDebugOverrides:
                debugOverrides = ((MyRenderMessageUpdateDebugOverrides)message).Overrides;
                break;

            case MyRenderMessageEnum.DrawScene:
                drawScene = true;
                break;

            case MyRenderMessageEnum.SetCameraViewMatrix:
            {
                var m = (MyRenderMessageSetCameraViewMatrix)message;
                camera.Set(m, BackBufferResolution);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderEnvironment:
                environment = ((MyRenderMessageUpdateRenderEnvironment)message).Data;
                hasEnvironment = true;
                break;

            case MyRenderMessageEnum.CreateRenderEntity:
            {
                var m = (MyRenderMessageCreateRenderEntity)message;
                actors.Create(m);
                models?.StartLoading(m.Model);
                break;
            }

            case MyRenderMessageEnum.CreateRenderCharacter:
            {
                var m = (MyRenderMessageCreateRenderCharacter)message;
                actors.CreateCharacter(m);
                models?.StartLoading(m.Model);
                break;
            }

            case MyRenderMessageEnum.SetVisibilityUpdates:
            {
                var m = (MyRenderMessageSetVisibilityUpdates)message;
                actors.SetVisibilityUpdates(m.ID, m.State);
                break;
            }

            case MyRenderMessageEnum.SetCharacterSkeleton:
                actors.SetCharacterSkeleton((MyRenderMessageSetCharacterSkeleton)message);
                break;

            case MyRenderMessageEnum.SetCharacterTransforms:
            {
                var m = (MyRenderMessageSetCharacterTransforms)message;
                actors.SetCharacterTransforms(m);
                break;
            }

            case MyRenderMessageEnum.CreateStaticGroup:
            {
                var m = (MyRenderMessageCreateStaticGroup)message;
                actors.CreateStaticGroup(m);
                models?.StartLoading(m.Model);
                break;
            }

            case MyRenderMessageEnum.CreateManualCullObject:
                actors.CreateManualCullObject((MyRenderMessageCreateManualCullObject)message);
                break;

            case MyRenderMessageEnum.SetParentCullObject:
            {
                var m = (MyRenderMessageSetParentCullObject)message;
                sceneLights.SetParent(m.ID, m.CullObjectID);
                actors.SetParentCullObject(m);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderObject:
            {
                var update = (MyRenderMessageUpdateRenderObject)message;
                actors.Update(update);
                voxelPass?.UpdateObject(update);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderObjectVisibility:
            {
                var m = (MyRenderMessageUpdateRenderObjectVisibility)message;
                actors.SetVisible(m.ID, m.Visible, m.NearFlag);
                voxelPass?.SetVisible(m.ID, m.Visible);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderEntity:
            {
                var m = (MyRenderMessageUpdateRenderEntity)message;
                actors.UpdateEntity(m);
                // Dithering is used by the game while voxel LOD cells fade in/out. The basic pass
                // doesn't alpha-test, but acknowledging the message keeps the state coherent.
                break;
            }

            case MyRenderMessageEnum.UpdateModelProperties:
                actors.SetModelProperties((MyRenderMessageUpdateModelProperties)message);
                break;

            case MyRenderMessageEnum.ChangeMaterialTexture:
            {
                actors.ChangeMaterialTexture((MyRenderMessageChangeMaterialTexture)message);
                break;
            }

            case MyRenderMessageEnum.UpdateColorEmissivity:
                actors.SetColorEmissivity((MyRenderMessageUpdateColorEmissivity)message);
                break;

            case MyRenderMessageEnum.ChangeModel:
            {
                var m = (MyRenderMessageChangeModel)message;
                if (reportedModelChanges++ < 30)
                    RenderLog.Info($"ChangeModel: actor {m.ID} -> {m.Model}");
                actors.ChangeModel(m);
                models?.StartLoading(m.Model);
                break;
            }

            case MyRenderMessageEnum.CreateRenderInstanceBuffer:
                instanceBuffers.Create((MyRenderMessageCreateRenderInstanceBuffer)message);
                break;

            case MyRenderMessageEnum.UpdateRenderCubeInstanceBuffer:
                instanceBuffers.UpdateCube((MyRenderMessageUpdateRenderCubeInstanceBuffer)message);
                break;

            case MyRenderMessageEnum.UpdateRenderInstanceBufferRange:
                instanceBuffers.UpdateGeneric((MyRenderMessageUpdateRenderInstanceBufferRange)message);
                break;

            case MyRenderMessageEnum.SetInstanceBuffer:
                actors.SetInstanceBuffer((MyRenderMessageSetInstanceBuffer)message);
                break;

            case MyRenderMessageEnum.CollectGarbage:
            {
                var gcWatch = Stopwatch.StartNew();
                MyVRage.Platform.System.CollectGC();
                if (gcWatch.Elapsed.TotalMilliseconds > 2)
                    RenderLog.Warning($"CollectGarbage took {gcWatch.Elapsed.TotalMilliseconds:0.0} ms");
                break;
            }

            // The game tracks the IDs of render objects; release them like the stock renderer does
            case MyRenderMessageEnum.RemoveRenderObject:
            {
                var id = ((MyRenderMessageRemoveRenderObject)message).ID;
                actors.Remove(id);
                voxelPass?.Remove(id);
                instanceBuffers.Remove(id);
                sceneLights.Remove(id);
                thrusterEffects.Remove(id);
                var type = MyRenderProxy.GetObjectType(id);
                if (type != MyRenderProxy.ObjectType.Invalid)
                    MyRenderProxy.RemoveMessageId(id, type);
                break;
            }

            case MyRenderMessageEnum.CloseVideo:
                MyRenderProxy.RemoveMessageId(((MyRenderMessageCloseVideo)message).ID, MyRenderProxy.ObjectType.Video);
                break;

            // Everything else is not implemented yet. Ignoring is safe: the message is
            // disposed by MessageQueueProcessor once its frame is done.
        }
    }

    #endregion

    #region IMyRender members

    public bool IsSupported => true;
    public bool MessageProcessingSupported => true;

    public string RootDirectory { get; set; }
    public string RootDirectoryEffects { get; set; }
    public string RootDirectoryDebug { get; set; }

    public MySharedData SharedData => sharedData;
    public MyMessageQueue OutputQueue => sharedData.RenderOutputMessageQueue;
    public MyLog Log => MyLog.Default;
    public FrameProcessStatusEnum FrameProcessStatus => messages.Status;
    public uint GlobalMessageCounter { get; set; } = 1;

    private MyTimeSpan currentDrawTime;

    public MyTimeSpan CurrentDrawTime
    {
        get => currentDrawTime;
        set => currentDrawTime = value;
    }

    public Vector2I BackBufferResolution => new(settings.BackBufferWidth, settings.BackBufferHeight);
    public MyViewport MainViewport => new(settings.BackBufferWidth, settings.BackBufferHeight);

    public MyRenderProfiler GetRenderProfiler() => profiler;
    public MyTimeSpan GetDrawCpuNOPGap() => MyTimeSpan.Zero;
    public void UpdateDRSScale() { }
    public void UpdateScreenDeviceState() { }
    public void Ansel_DrawScene() { }
    public void SetTimings(MyTimeSpan cpuDraw, MyTimeSpan cpuWait) { }
    public float GetDRSDebugData() => 1f;
    public string GetLastExecutedAnnotation() => "";
    public string GetStatistics() => "";

    // Shader cache generation belongs to the D3D11 shader compiler; our shaders are precompiled SPIR-V
    public void GenerateShaderCache(bool clean, bool fastBuild, OnShaderCacheProgressDelegate onShaderCacheProgress) { }

    // Video playback is not implemented yet (Phase 7). Reporting no video lets the game skip it.
    public bool IsVideoValid(uint id) => false;
    public VideoState GetVideoState(uint id) => VideoState.Stopped;

    public Vector2 GetTextureSize(string name) => textures?.GetSize(name) ?? Vector2.Zero;
    public bool IsTextureLoaded(string name) => textures?.IsLoaded(name) ?? false;

    #endregion
}
