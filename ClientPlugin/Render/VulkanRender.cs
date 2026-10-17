using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Render.Models;
using ClientPlugin.Render.Platform;
using ClientPlugin.Render.Scene;
using ClientPlugin.Render.Sprites;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using ClientPlugin.Settings;
using Vortice.Vulkan;
using VRage;
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
    private CompositePass composite;
    private VkFormat compositeFormat;
    private ModelCache models;
    private InstanceBuffers instanceBuffers;
    private ModelPass modelPass;

    private readonly CameraState camera = new();
    private readonly SceneActors actors = new();
    private MyEnvironmentData environment;
    private bool hasEnvironment;
    private int sceneFrameCounter;
    private int lastDrawnActors;
    private readonly HashSet<MyRenderMessageEnum> reported = new();

    // Set by the DrawScene message of the frame being drawn (MyRender11.m_drawScene): without it only the GUI is shown
    private bool drawScene;

    private MyAdapterInfo[] adapters = [];
    private MyRenderDeviceSettings settings = new() { AdapterOrdinal = -1 };
    private MyWindowModeEnum? lastForwardedWindowMode;
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
            swapchain.Recreate(new Vector2I(requested.BackBufferWidth, requested.BackBufferHeight), requested.VSync != 0);
            frameLoop = new FrameLoop(device, swapchain);

            gpu = new GpuContext(instance, device);
            textures = new TextureCache(gpu);
            models = new ModelCache(gpu, textures);
            instanceBuffers = new InstanceBuffers(gpu);
            modelPass = new ModelPass(gpu, models, instanceBuffers);
            gui = new GuiRenderer(gpu, textures, () => messages.FrameId);
            capture = FrameCapture.FromEnvironment(gpu);
            sceneTargets = new SceneTargets(gpu);

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
        device?.WaitIdle();

        gui?.Dispose();
        gui = null;

        capture?.Dispose();
        capture = null;

        composite?.Dispose();
        composite = null;

        sceneTargets?.Dispose();
        sceneTargets = null;

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
                     || newSettings.BackBufferHeight != settings.BackBufferHeight
                     || newSettings.VSync != settings.VSync;

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
        if (!force && lastForwardedWindowMode == settings.WindowMode)
            return;

        if (settings.AdapterOrdinal < 0 || settings.AdapterOrdinal >= adapters.Length)
            return;

        lastForwardedWindowMode = settings.WindowMode;
        try
        {
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

        swapchain.Recreate(new Vector2I(settings.BackBufferWidth, settings.BackBufferHeight), settings.VSync != 0);
        frameLoop.OnSwapchainRecreated();
    }

    public long GetAvailableTextureMemory() =>
        settings.AdapterOrdinal >= 0 && settings.AdapterOrdinal < adapters.Length
            ? (long)adapters[settings.AdapterOrdinal].VRAM
            : 0;

    #endregion

    #region Frame

    public void Draw(bool draw = true)
    {
        // The frame slot must be free (fence waited) before messages run: texture uploads issued while
        // processing them are written into this slot's staging memory.
        var frameStarted = false;
        if (draw && frameLoop != null)
        {
            if (frameLoop.SwapchainOutdated)
                RecreateSwapchain();

            frameStarted = frameLoop.TryBeginFrame();
            if (frameStarted)
            {
                gpu.BeginFrame(frameLoop.FrameSlot);
                gui.BeginFrame(frameLoop.FrameSlot);
                drawScene = false;
            }
        }

        // Without a frame to draw into, only state changes are applied; the draw messages of this
        // update frame are processed with the next frame that is drawn.
        messages.Process(frameStarted);

        if (!frameStarted)
            return;

        textures.ProcessDecoded();
        models.ProcessReady();

        var commands = frameLoop.Commands;
        gpu.Uploads.Record(commands);
        gui.RecordOffscreen(commands);
        if (drawScene)
            RecordScene(commands);
        RecordFrame(commands);
        frameLoop.EndFrame();
    }

    // The 3D scene into the HDR targets. No geometry yet: clears to the space background.
    private unsafe void RecordScene(VkCommandBuffer commands)
    {
        var api = device.Api;
        var resolution = BackBufferResolution;
        sceneTargets.EnsureSize((uint)Math.Max(1, resolution.X), (uint)Math.Max(1, resolution.Y));
        var extent = sceneTargets.Extent;

        frameLoop.Barrier(sceneTargets.Color.Handle, sceneTargets.Color.FullRange,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.None, VkImageLayout.Undefined,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);
        frameLoop.Barrier(sceneTargets.Depth.Handle, sceneTargets.Depth.FullRange,
            VkPipelineStageFlags2.FragmentShader | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.None, VkImageLayout.Undefined,
            VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.DepthStencilAttachmentWrite, VkImageLayout.DepthAttachmentOptimal);

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            imageView = sceneTargets.Color.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0.004f, 0.005f, 0.009f, 1f) },
        };
        var depthAttachment = new VkRenderingAttachmentInfo
        {
            imageView = sceneTargets.Depth.View,
            imageLayout = VkImageLayout.DepthAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(0f, 0) }, // Reverse Z: far = 0
        };
        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
            pDepthAttachment = &depthAttachment,
        };

        api.vkCmdBeginRendering(commands, &renderingInfo);
        lastDrawnActors = camera.HasValue && hasEnvironment
            ? modelPass.Record(commands, sceneTargets, camera, environment, actors)
            : 0;
        api.vkCmdEndRendering(commands);

        if (++sceneFrameCounter % 300 == 0)
            RenderLog.Info($"Scene: {actors.Count} actors ({actors.InstancedCount} instanced), {lastDrawnActors} draws, " +
                           $"instance buffers {instanceBuffers.Count} ({instanceBuffers.TotalInstances} instances), " +
                           $"camera {camera.HasValue}, environment {hasEnvironment}");

        frameLoop.Barrier(sceneTargets.Color.Handle, sceneTargets.Color.FullRange,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead, VkImageLayout.ShaderReadOnlyOptimal);
    }

    private unsafe void RecordFrame(VkCommandBuffer commands)
    {
        var api = device.Api;
        var image = frameLoop.BackbufferImage;
        var extent = frameLoop.BackbufferExtent;
        var range = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);

        frameLoop.Barrier(image, range,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.None, VkImageLayout.Undefined,
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

        api.vkCmdBeginRendering(commands, &renderingInfo);
        if (drawScene)
        {
            if (composite == null || compositeFormat != swapchain.Format)
            {
                composite?.Dispose();
                composite = new CompositePass(gpu, swapchain.Format);
                compositeFormat = swapchain.Format;
            }

            composite.Record(commands, sceneTargets, extent);
        }

        gui.RecordMain(commands, swapchain.Format, BackBufferResolution, extent);
        api.vkCmdEndRendering(commands);

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

            case MyRenderMessageEnum.UpdateDebugOverrides:
                debugOverrides = ((MyRenderMessageUpdateDebugOverrides)message).Overrides;
                break;

            case MyRenderMessageEnum.DrawScene:
                drawScene = true;
                break;

            case MyRenderMessageEnum.SetCameraViewMatrix:
            {
                var m = (MyRenderMessageSetCameraViewMatrix)message;
                if (!camera.HasValue)
                    RenderLog.Info($"Camera: FOV {m.FOV}, near {m.NearPlane}, far {m.FarPlane}, position {m.CameraPosition}");
                camera.Set(m, BackBufferResolution);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderEnvironment:
            {
                var data = ((MyRenderMessageUpdateRenderEnvironment)message).Data;
                if (!hasEnvironment)
                    RenderLog.Info($"Environment: sun direction {data.EnvironmentLight.SunLightDirection}, " +
                                   $"color {data.EnvironmentLight.SunColorRaw}, diffuse {data.EnvironmentLight.SunDiffuseFactor}, " +
                                   $"ambient diffuse {data.EnvironmentLight.AmbientDiffuseFactor}, forward {data.EnvironmentLight.AmbientForwardPass}, " +
                                   $"skybox '{data.Skybox}'");
                environment = data;
                hasEnvironment = true;
                break;
            }

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

            case MyRenderMessageEnum.CreateStaticGroup:
            {
                var m = (MyRenderMessageCreateStaticGroup)message;
                actors.CreateStaticGroup(m);
                models?.StartLoading(m.Model);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderObject:
                actors.Update((MyRenderMessageUpdateRenderObject)message);
                break;

            case MyRenderMessageEnum.UpdateRenderObjectVisibility:
            {
                var m = (MyRenderMessageUpdateRenderObjectVisibility)message;
                actors.SetVisible(m.ID, m.Visible, m.NearFlag);
                break;
            }

            case MyRenderMessageEnum.UpdateRenderEntity:
                actors.UpdateEntity((MyRenderMessageUpdateRenderEntity)message);
                break;

            case MyRenderMessageEnum.UpdateModelProperties:
                actors.SetModelProperties((MyRenderMessageUpdateModelProperties)message);
                break;

            case MyRenderMessageEnum.UpdateColorEmissivity:
                actors.SetColorEmissivity((MyRenderMessageUpdateColorEmissivity)message);
                break;

            case MyRenderMessageEnum.ChangeModel:
            {
                var m = (MyRenderMessageChangeModel)message;
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

            // Not implemented yet; report each kind once so the log shows what a world still needs.
            case MyRenderMessageEnum.CreateManualCullObject:
            case MyRenderMessageEnum.SetParentCullObject:
            case MyRenderMessageEnum.VoxelCreate:
            case MyRenderMessageEnum.CreateRenderVoxelMaterials:
            case MyRenderMessageEnum.UpdateRenderVoxelMaterials:
                ReportOnce(message.MessageType);
                break;

            case MyRenderMessageEnum.CollectGarbage:
                MyVRage.Platform.System.CollectGC();
                break;

            // The game tracks the IDs of render objects; release them like the stock renderer does
            case MyRenderMessageEnum.RemoveRenderObject:
            {
                var id = ((MyRenderMessageRemoveRenderObject)message).ID;
                actors.Remove(id);
                instanceBuffers.Remove(id);
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

    private void ReportOnce(MyRenderMessageEnum type)
    {
        if (reported.Add(type))
            RenderLog.Info($"Message {type} is not implemented yet, ignoring it");
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
