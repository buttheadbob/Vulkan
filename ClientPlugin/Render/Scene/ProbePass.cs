using System;
using System.Diagnostics;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Scene;

// Dynamic reflection probe: renders the nearby opaque scene into a cube around the camera and
// prefilters it into a mip chain. Stock uses a close/far environment probe for the same purpose.
internal sealed unsafe class ProbePass : IDisposable
{
    // The whole cube is captured every frame so moving ships, thrusters and lights stay live.
    // Throttling/cross-fading made reflections visibly step; the lower default resolution keeps
    // the per-frame cost bounded.

    public uint Resolution { get; }
    public uint MipLevels { get; }

    private readonly GpuContext context;
    private readonly ModelPass modelPass;
    private readonly SkyboxPass skybox;
    private readonly BillboardPass billboards;
    private readonly VoxelPass voxels;
    private readonly SceneTargets[] faces = new SceneTargets[6];
    private readonly GpuImage[] cubes = new GpuImage[2];
    private readonly uint[] cubeSlots = new uint[2];
    private readonly VkImageLayout[] cubeLayouts = [VkImageLayout.Undefined, VkImageLayout.Undefined];
    private readonly bool[] faceInitialized = new bool[6];
    private readonly VkImageLayout[] faceColorLayouts = new VkImageLayout[6];

    // Two cubes: the completed display cube and the cube being filled one face per frame. During
    // a cycle both are sampled and blended, so a moving ship fades between captures instead of
    // popping when a face changes.
    private int currentCube = -1;
    private int targetCube;
    private bool cycling;
    private bool needCubeCopy;
    private int nextFace;
    private int completedFaces;
    private float timeBlend;
    private double lastRecordTimestamp;
    private Vector3D capturePosition;
    private readonly Matrix[] faceViewProjections = new Matrix[6];
    private readonly Matrix[] faceInverseViewProjections = new Matrix[6];
    private float cachedFaceNear = float.NaN;

    public bool HasActive => currentCube >= 0;
    // While a cycle is running the target is the incoming cube and the completed cube is the
    // previous one. Between cycles the completed cube is used directly.
    public uint ActiveSlot => HasActive ? cubeSlots[cycling ? targetCube : currentCube] : 0;
    public uint PreviousSlot => HasActive ? cubeSlots[currentCube] : 0;

    // 0: previous completed cube, 1: target cube. Clamped to the faces completed so far so stale
    // target faces are never trusted before the cycle finishes.
    public float Blend => HasActive
        ? (cycling ? Math.Min(timeBlend, completedFaces / 6f) : 1f)
        : 1f;

    // CPU time spent recording the most recent one-face update (0 on frames without a capture).
    public double LastCpuMs { get; private set; }

    public ProbePass(GpuContext context, ModelPass modelPass, SkyboxPass skybox, BillboardPass billboards,
        VoxelPass voxels, uint resolution = 256)
    {
        this.context = context;
        this.modelPass = modelPass;
        this.skybox = skybox;
        this.billboards = billboards;
        this.voxels = voxels;

        Resolution = Math.Max(16, resolution);
        MipLevels = (uint)Math.Log2(Resolution) + 1;

        for (var i = 0; i < faces.Length; i++)
            faces[i] = new SceneTargets(context);

        for (var i = 0; i < cubes.Length; i++)
        {
            cubes[i] = GpuImage.Create2D(context, Resolution, Resolution, MipLevels, 6,
                SceneTargets.ColorFormat,
                VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst,
                isCube: true);
            cubeSlots[i] = context.Bindless.AllocateCubeSlot();
            context.Bindless.WriteCube(cubeSlots[i], cubes[i].View);
        }
    }

    // Kept for the frame loop; the live probe needs no per-frame blend state.
    public void BeginFrame()
    {
    }

    // The six cubemap face matrices only depend on the camera near plane; cache their transposed
    // view-projection and inverse instead of rebuilding/inverting six 4x4 matrices per update.
    private void EnsureFaceMatrices(CameraState camera)
    {
        var near = camera.Near > 0f ? camera.Near : 0.05f;
        if (near == cachedFaceNear)
            return;

        for (var face = 0; face < faces.Length; face++)
        {
            var probe = camera.CreateProbeFace(face);
            faceViewProjections[face] = Matrix.Transpose(probe.ViewProjectionAt0);
            faceInverseViewProjections[face] = Matrix.Transpose(Matrix.Invert(probe.ViewProjectionAt0));
        }
        cachedFaceNear = near;
    }

    // Captures one face per frame into the target cube. The target is initialised from the
    // completed cube at the start of each six-face cycle, so every unrendered face still has valid
    // content. The reflection shader blends current -> target over a short time window; this avoids
    // the visible stepping of the old one-face version without paying for a full cube every frame.
    public void RecordCube(VkCommandBuffer commands, int frameSlot, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, in FrameConstants source,
        MySharedData sharedData, ThrusterEffects thrusterEffects, uint staticIblSlot, uint staticIblMips)
    {
        LastCpuMs = 0;
        var start = Stopwatch.GetTimestamp();
        try
        {
            capturePosition = camera.Position;

            var actorsAddress = modelPass.PrepareProbeActorStates(frameSlot, out var actorCount);
            if (actorCount == 0)
                return;

            EnsureFaceMatrices(camera);
            var skinnedOffset = (Vector3)(camera.Position - capturePosition);
            var now = Stopwatch.GetTimestamp();
            var dt = lastRecordTimestamp == 0
                ? 0f
                : (float)Math.Min(0.1, (now - lastRecordTimestamp) / (double)Stopwatch.Frequency);
            lastRecordTimestamp = now;

            if (currentCube < 0)
            {
                // Startup: fill the whole target once so the first completed cube has no black faces.
                PrepareCapture(commands);
                for (var face = 0; face < faces.Length; face++)
                    RecordFace(commands, frameSlot, face, camera, lodding, actors, actorsAddress, actorCount,
                        skinnedOffset, source, sharedData, thrusterEffects, staticIblSlot, staticIblMips);
                GenerateMips(commands);
                currentCube = targetCube;
                targetCube ^= 1;
                cycling = false;
                needCubeCopy = true;
                completedFaces = 0;
                timeBlend = 0f;
                nextFace = 0;
                return;
            }

            if (needCubeCopy)
            {
                CopyCube(commands, currentCube, targetCube);
                needCubeCopy = false;
                cycling = true;
                completedFaces = 0;
                timeBlend = 0f;
                nextFace = 0;
            }

            var blendSeconds = Math.Max(0.05f, Config.Current.ReflectionProbeBlendSeconds);
            timeBlend = Math.Min(1f, timeBlend + dt / blendSeconds);

            if (completedFaces < 6)
            {
                PrepareCapture(commands);
                RecordFace(commands, frameSlot, nextFace, camera, lodding, actors, actorsAddress, actorCount,
                    skinnedOffset, source, sharedData, thrusterEffects, staticIblSlot, staticIblMips);
                nextFace = (nextFace + 1) % faces.Length;
                completedFaces++;
                GenerateMips(commands);
            }

            if (completedFaces == 6 && timeBlend >= 1f)
            {
                currentCube = targetCube;
                targetCube ^= 1;
                cycling = false;
                needCubeCopy = true;
                completedFaces = 0;
                timeBlend = 0f;
                nextFace = 0;
            }
        }
        finally
        {
            LastCpuMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private void RecordFace(VkCommandBuffer commands, int frameSlot, int face, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, ulong actorsAddress, uint actorCount,
        Vector3 skinnedOffset, in FrameConstants source, MySharedData sharedData, ThrusterEffects thrusterEffects,
        uint staticIblSlot, uint staticIblMips)
    {
        var targets = faces[face];
        targets.EnsureSize(Resolution, Resolution, sceneSamples);
        TransitionFace(commands, face, targets);

        var probeCamera = camera.CreateProbeFace(face, capturePosition);
        modelPass.RecordProbePrepare(commands, frameSlot, probeCamera, lodding, actorsAddress, actorCount,
            skinnedOffset);

        var constants = probeCamera.BuildProbeFrameConstants(source, MipLevels,
            faceViewProjections[face], faceInverseViewProjections[face]);
        // Capture against the static skybox IBL, not the probe cube being replaced. Sampling the
        // live probe here would feed every metal reflection back into the next capture.
        constants.IblCube = staticIblSlot;
        constants.IblCubePrev = 0;
        constants.IblBlend = 1f;
        constants.IblProbe = 0;
        constants.SkyboxParams = new Vector4(constants.SkyboxParams.X, staticIblMips,
            constants.SkyboxParams.Z, constants.SkyboxParams.W);
        var allocation = context.Transient.Allocate<FrameConstants>();
        allocation.As<FrameConstants>() = constants;

        var api = context.Device.Api;
        var msaa = targets.Samples != VkSampleCountFlags.Count1;
        var attachments = stackalloc VkRenderingAttachmentInfo[3];
        attachments[0] = ColorAttachment(msaa ? targets.ColorMs.View : targets.Color.View,
            msaa, msaa ? targets.Color.View : default);
        attachments[1] = ColorAttachment(msaa ? targets.GBufferMs.View : targets.GBuffer.View,
            msaa, msaa ? targets.GBuffer.View : default);
        attachments[2] = ColorAttachment(msaa ? targets.GBuffer2Ms.View : targets.GBuffer2.View,
            msaa, msaa ? targets.GBuffer2.View : default);
        var depth = new VkRenderingAttachmentInfo
        {
            imageView = msaa ? targets.DepthMs.View : targets.Depth.View,
            imageLayout = VkImageLayout.DepthAttachmentOptimal,
            resolveMode = VkResolveModeFlags.None,
            resolveImageView = default,
            resolveImageLayout = VkImageLayout.Undefined,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(0f, 0) },
        };
        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, Resolution, Resolution),
            layerCount = 1,
            colorAttachmentCount = 3,
            pColorAttachments = attachments,
            pDepthAttachment = &depth,
        };

        api.vkCmdBeginRendering(commands, &renderingInfo);
        skybox.Record(commands, targets, allocation.Address);
        modelPass.RecordProbe(commands, targets, allocation.Address);
        modelPass.RecordProbeActors(commands, targets, probeCamera, camera, actors, lodding,
            allocation.Address);
        // Planets/asteroids are voxels, not MWM actors; include their coarse colour in reflections.
        voxels?.RecordProbe(commands, targets, probeCamera, allocation.Address);
        // Thruster flames, particle glows and other billboards are not part of the opaque scene;
        // draw them into the probe too so glass and metal reflect the engine/emissive effects.
        billboards.RecordProbe(commands, targets, probeCamera, actors, allocation.Address, sharedData,
            thrusterEffects);
        api.vkCmdEndRendering(commands);

        CopyFace(commands, face, targets);
        faceColorLayouts[face] = VkImageLayout.TransferSrcOptimal;
    }

    private VkSampleCountFlags sceneSamples = VkSampleCountFlags.Count1;

    public void SetSamples(VkSampleCountFlags samples) => sceneSamples = samples;

    private static VkRenderingAttachmentInfo ColorAttachment(VkImageView view, bool msaa, VkImageView resolve)
    {
        return new VkRenderingAttachmentInfo
        {
            imageView = view,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            resolveMode = msaa ? VkResolveModeFlags.Average : VkResolveModeFlags.None,
            resolveImageView = resolve,
            resolveImageLayout = resolve != VkImageView.Null
                ? VkImageLayout.ColorAttachmentOptimal
                : VkImageLayout.Undefined,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = new VkClearColorValue(0f, 0f, 0f, 0f) },
        };
    }

    // Copies the completed cube into the target at the start of a cycle. The previous cube stays
    // readable for the blend, and the target's untouched faces contain the old content rather than
    // black. This runs once per six frames.
    private void CopyCube(VkCommandBuffer commands, int source, int destination)
    {
        var api = context.Device.Api;
        TransitionCube(commands, source, 0, MipLevels, 0, 6,
            cubeLayouts[source], VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags2.AllCommands, VkAccessFlags2.ShaderSampledRead,
            VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferRead);
        TransitionCube(commands, destination, 0, MipLevels, 0, 6,
            cubeLayouts[destination], VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags2.AllCommands, VkAccessFlags2.ShaderSampledRead,
            VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite);

        for (var mip = 0u; mip < MipLevels; mip++)
        {
            var width = Math.Max(1u, Resolution >> (int)mip);
            var height = Math.Max(1u, Resolution >> (int)mip);
            for (var layer = 0u; layer < 6; layer++)
            {
                var region = new VkImageCopy
                {
                    srcSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, mip, layer, 1),
                    dstSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, mip, layer, 1),
                    extent = new VkExtent3D(width, height, 1),
                };
                api.vkCmdCopyImage(commands, cubes[source].Handle, VkImageLayout.TransferSrcOptimal,
                    cubes[destination].Handle, VkImageLayout.TransferDstOptimal, 1, &region);
            }
        }

        // The completed cube is sampled again next frame; the target stays a copy destination.
        TransitionCube(commands, source, 0, MipLevels, 0, 6,
            VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferRead,
            VkPipelineStageFlags2.AllCommands, VkAccessFlags2.ShaderSampledRead);
        cubeLayouts[source] = VkImageLayout.ShaderReadOnlyOptimal;
        cubeLayouts[destination] = VkImageLayout.TransferDstOptimal;
    }

    private void PrepareCapture(VkCommandBuffer commands)
    {
        // Everything is written at mip 0 first; mips are generated after the face.
        var oldLayout = cubeLayouts[targetCube];
        var wasSampled = oldLayout == VkImageLayout.ShaderReadOnlyOptimal;
        TransitionCube(commands, targetCube, 0, MipLevels, 0, 6,
            oldLayout, VkImageLayout.TransferDstOptimal,
            wasSampled ? VkPipelineStageFlags2.AllCommands : VkPipelineStageFlags2.None,
            wasSampled ? VkAccessFlags2.ShaderSampledRead : VkAccessFlags2.None,
            VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite);
        cubeLayouts[targetCube] = VkImageLayout.TransferDstOptimal;
    }

    private void TransitionFace(VkCommandBuffer commands, int face, SceneTargets targets)
    {
        var api = context.Device.Api;
        if (!faceInitialized[face])
        {
            var barriers = stackalloc VkImageMemoryBarrier2[targets.Samples == VkSampleCountFlags.Count1 ? 4 : 5];
            var count = 0;
            barriers[count++] = new VkImageMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.None,
                srcAccessMask = VkAccessFlags2.None,
                dstStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
                dstAccessMask = VkAccessFlags2.ColorAttachmentWrite,
                oldLayout = VkImageLayout.Undefined,
                newLayout = VkImageLayout.ColorAttachmentOptimal,
                image = targets.Color.Handle,
                subresourceRange = targets.Color.FullRange,
                srcQueueFamilyIndex = uint.MaxValue,
                dstQueueFamilyIndex = uint.MaxValue,
            };
            var msaa = targets.Samples != VkSampleCountFlags.Count1;
            barriers[count++] = ColorBarrier(msaa ? targets.GBufferMs : targets.GBuffer);
            barriers[count++] = ColorBarrier(msaa ? targets.GBuffer2Ms : targets.GBuffer2);
            barriers[count++] = DepthBarrier(msaa ? targets.DepthMs : targets.Depth);
            if (msaa)
                barriers[count++] = ColorBarrier(targets.ColorMs);
            var info = new VkDependencyInfo { imageMemoryBarrierCount = (uint)count, pImageMemoryBarriers = barriers };
            api.vkCmdPipelineBarrier2(commands, &info);
            faceInitialized[face] = true;
            faceColorLayouts[face] = VkImageLayout.ColorAttachmentOptimal;
            return;
        }

        // Only the resolved color image changes layout after the first use; the MSAA color,
        // G-buffers and depth stay in attachment layouts.
        var toAttachment = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.Transfer,
            srcAccessMask = VkAccessFlags2.TransferRead,
            dstStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            oldLayout = faceColorLayouts[face],
            newLayout = VkImageLayout.ColorAttachmentOptimal,
            image = targets.Color.Handle,
            subresourceRange = targets.Color.FullRange,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
        };
        var attachmentInfo = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &toAttachment };
        api.vkCmdPipelineBarrier2(commands, &attachmentInfo);
        faceColorLayouts[face] = VkImageLayout.ColorAttachmentOptimal;
    }

    private static VkImageMemoryBarrier2 DepthBarrier(GpuImage image)
    {
        return new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.None,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
            dstAccessMask = VkAccessFlags2.DepthStencilAttachmentWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.DepthAttachmentOptimal,
            image = image.Handle,
            subresourceRange = image.FullRange,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
        };
    }

    private static VkImageMemoryBarrier2 ColorBarrier(GpuImage image)
    {
        return new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.None,
            srcAccessMask = VkAccessFlags2.None,
            dstStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.ColorAttachmentOptimal,
            image = image.Handle,
            subresourceRange = image.FullRange,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
        };
    }

    private void CopyFace(VkCommandBuffer commands, int face, SceneTargets targets)
    {
        var api = context.Device.Api;
        var color = targets.Color;
        var toTransfer = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            srcAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            dstStageMask = VkPipelineStageFlags2.Transfer,
            dstAccessMask = VkAccessFlags2.TransferRead,
            oldLayout = VkImageLayout.ColorAttachmentOptimal,
            newLayout = VkImageLayout.TransferSrcOptimal,
            image = color.Handle,
            subresourceRange = color.FullRange,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
        };
        var toTransferInfo = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &toTransfer };
        api.vkCmdPipelineBarrier2(commands, &toTransferInfo);

        var region = new VkImageCopy
        {
            srcSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            dstSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, (uint)face, 1),
            extent = new VkExtent3D(Resolution, Resolution, 1),
        };
        api.vkCmdCopyImage(commands, color.Handle, VkImageLayout.TransferSrcOptimal,
            cubes[targetCube].Handle, VkImageLayout.TransferDstOptimal, 1, &region);
    }

    private void GenerateMips(VkCommandBuffer commands)
    {
        var cube = cubes[targetCube];
        MipGenerator.Record(context.Device.Api, commands, cube,
            VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite);
        cubeLayouts[targetCube] = VkImageLayout.ShaderReadOnlyOptimal;
    }

    private void TransitionCube(VkCommandBuffer commands, int cubeIndex, uint baseMip, uint mipCount,
        uint baseLayer, uint layerCount, VkImageLayout oldLayout, VkImageLayout newLayout,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess,
        VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
    {
        if (oldLayout == newLayout)
            return;

        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            image = cubes[cubeIndex].Handle,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, baseMip, mipCount,
                baseLayer, layerCount),
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
        };
        var info = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
        context.Device.Api.vkCmdPipelineBarrier2(commands, &info);
    }

    public void Dispose()
    {
        foreach (var face in faces)
            face.Dispose();
        foreach (var cube in cubes)
            cube?.Dispose();
        foreach (var slot in cubeSlots)
            context.Bindless.FreeCubeSlot(slot);
    }
}
