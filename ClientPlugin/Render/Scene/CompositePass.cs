using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Tonemaps the HDR scene into the tonemapped target. The FXAA pass then resolves that target onto
// the backbuffer, so post AA runs on LDR values like the stock renderer's tonemapping/FXAA chain.
internal sealed unsafe class CompositePass : IDisposable
{
    public const VkFormat Format = VkFormat.R8G8B8A8Srgb;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public uint SceneTexture;
        public uint OitAccum;
        public uint OitReveal;
        public uint ExposureTexture;
        public uint BloomTexture;
        public uint ShadowMap;
        public uint ShadowDebug;
        public uint DepthTexture;
        public uint GBufferTexture;
        public uint GBuffer2Texture;
        public float WhitePoint;
        public uint Pad;
        public ulong Frame;
    }

    private readonly GpuContext context;
    private readonly VkPipeline pipeline;

    private GpuImage target;
    private uint targetSlot;
    private VkExtent2D extent;
    private VkImageLayout targetLayout = VkImageLayout.Undefined;

    public uint SourceSlot => targetSlot;

    public CompositePass(GpuContext context)
    {
        this.context = context;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Composite.Code.ToArray(),
            VertexEntry = ShaderBytecode.Composite.VertexMain,
            FragmentEntry = ShaderBytecode.Composite.FragmentMain,
            ColorFormats = [Format],
        }.Build(context, "composite");
    }

    public void Record(VkCommandBuffer commands, SceneTargets targets, VkExtent2D backbuffer,
        ulong frameConstants, uint exposureTexture, uint bloomTexture, uint shadowMap, uint shadowDebug,
        float whitePoint)
    {
        EnsureSize(backbuffer);

        Transition(commands, targetLayout, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);

        var api = context.Device.Api;
        var attachment = new VkRenderingAttachmentInfo
        {
            imageView = target.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
        };
        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &attachment,
        };
        api.vkCmdBeginRendering(commands, &renderingInfo);

        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, extent.width, extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, extent.width, extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var push = new PushConstants
        {
            SceneTexture = targets.ColorSlot,
            OitAccum = targets.OitAccumSlot,
            OitReveal = targets.OitRevealSlot,
            ExposureTexture = exposureTexture,
            BloomTexture = bloomTexture,
            ShadowMap = shadowMap,
            ShadowDebug = shadowDebug,
            DepthTexture = targets.DepthSlot,
            GBufferTexture = targets.GBufferSlot,
            GBuffer2Texture = targets.GBuffer2Slot,
            WhitePoint = whitePoint,
            Frame = frameConstants,
        };
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All, 0, (uint)sizeof(PushConstants), &push);
        api.vkCmdDraw(commands, 3, 1, 0, 0);
        api.vkCmdEndRendering(commands);

        Transition(commands, targetLayout, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);
    }

    private void EnsureSize(VkExtent2D size)
    {
        if (target != null && extent.width == size.width && extent.height == size.height)
            return;

        target?.Dispose();
        if (target != null)
            context.Bindless.FreeTextureSlot(targetSlot);

        extent = size;
        target = GpuImage.Create2D(context, size.width, size.height, 1, 1, Format,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        targetSlot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(targetSlot, target.View);
        targetLayout = VkImageLayout.Undefined;
    }

    private void Transition(VkCommandBuffer commands, VkImageLayout oldLayout, VkImageLayout newLayout,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess,
        VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
    {
        if (oldLayout == newLayout)
            return;

        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = oldLayout == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : srcStage,
            srcAccessMask = oldLayout == VkImageLayout.Undefined ? VkAccessFlags2.None : srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = target.Handle,
            subresourceRange = target.FullRange,
        };
        var dependency = new VkDependencyInfo
        {
            imageMemoryBarrierCount = 1,
            pImageMemoryBarriers = &barrier,
        };
        context.Device.Api.vkCmdPipelineBarrier2(commands, &dependency);
        targetLayout = newLayout;
    }

    public void Dispose()
    {
        var api = context.Device.Api;
        var handle = pipeline;
        context.Release(() => api.vkDestroyPipeline(handle, null));
        target?.Dispose();
        if (target != null)
            context.Bindless.FreeTextureSlot(targetSlot);
        target = null;
    }
}
