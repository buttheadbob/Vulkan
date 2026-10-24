using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Half-resolution HDR bloom: bright pass, separable blur, then the composite adds it. The scene color
// is already in ShaderReadOnlyOptimal when this runs (end of RecordScene).
internal sealed unsafe class BloomPass : IDisposable
{
    private const VkFormat Format = VkFormat.R16G16B16A16Sfloat;
    private const float Threshold = 1.0f;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public uint Source;
        public float Threshold;
        public float DirectionX;
        public float DirectionY;
        public uint OitAccum;
        public uint OitReveal;
    }

    private readonly GpuContext context;
    private readonly VkPipeline prefilter;
    private readonly VkPipeline blurHorizontal;
    private readonly VkPipeline blurVertical;
    private GpuImage a;
    private GpuImage b;
    private uint slotA;
    private uint slotB;
    private VkExtent2D extent;
    private VkImageLayout layoutA = VkImageLayout.Undefined;
    private VkImageLayout layoutB = VkImageLayout.Undefined;

    public uint Slot => slotA;

    public BloomPass(GpuContext context)
    {
        this.context = context;
        prefilter = BuildPipeline(context, ShaderBytecode.Bloom.PrefilterMain, "bloom-prefilter");
        blurHorizontal = BuildPipeline(context, ShaderBytecode.Bloom.BlurHorizontalMain, "bloom-blur-h");
        blurVertical = BuildPipeline(context, ShaderBytecode.Bloom.BlurVerticalMain, "bloom-blur-v");
    }

    private static VkPipeline BuildPipeline(GpuContext context, string entry, string name) => new GraphicsPipelineBuilder
    {
        Code = ShaderBytecode.Bloom.Code.ToArray(),
        VertexEntry = ShaderBytecode.Bloom.VertexMain,
        FragmentEntry = entry,
        ColorFormats = [Format],
    }.Build(context, name);

    private void EnsureSize(VkExtent2D full)
    {
        var width = Math.Max(1u, full.width / 2);
        var height = Math.Max(1u, full.height / 2);
        if (a != null && extent.width == width && extent.height == height)
            return;

        a?.Dispose();
        b?.Dispose();
        context.Bindless.FreeTextureSlot(slotA);
        context.Bindless.FreeTextureSlot(slotB);

        extent = new VkExtent2D(width, height);
        a = GpuImage.Create2D(context, width, height, 1, 1, Format,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        b = GpuImage.Create2D(context, width, height, 1, 1, Format,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        slotA = context.Bindless.AllocateTextureSlot();
        slotB = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(slotA, a.View);
        context.Bindless.WriteTexture(slotB, b.View);
        layoutA = VkImageLayout.Undefined;
        layoutB = VkImageLayout.Undefined;
    }

    public void Record(VkCommandBuffer commands, uint sceneTexture, uint oitAccum, uint oitReveal,
        VkExtent2D fullExtent)
    {
        EnsureSize(fullExtent);
        Prefilter(commands, sceneTexture, oitAccum, oitReveal);

        var dx = 1f / extent.width;
        var dy = 1f / extent.height;
        Blur(commands, a, slotA, ref layoutA, b, ref layoutB, blurHorizontal, new PushConstants
        {
            Source = slotA,
            Threshold = Threshold,
            DirectionX = dx,
            DirectionY = 0f,
        });
        Blur(commands, b, slotB, ref layoutB, a, ref layoutA, blurVertical, new PushConstants
        {
            Source = slotB,
            Threshold = Threshold,
            DirectionX = 0f,
            DirectionY = dy,
        });

        // Leave the result readable by the composite.
        Transition(commands, a, ref layoutA, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);
    }

    private void Prefilter(VkCommandBuffer commands, uint sceneTexture, uint oitAccum, uint oitReveal)
    {
        Transition(commands, a, ref layoutA, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
        BeginTarget(commands, a);
        Bind(commands, prefilter, new PushConstants
        {
            Source = sceneTexture,
            Threshold = Threshold,
            OitAccum = oitAccum,
            OitReveal = oitReveal,
            DirectionX = 0f,
            DirectionY = 0f,
        });
        context.Device.Api.vkCmdDraw(commands, 3, 1, 0, 0);
        context.Device.Api.vkCmdEndRendering(commands);
    }

    private void Blur(VkCommandBuffer commands, GpuImage source, uint sourceSlot, ref VkImageLayout sourceLayout,
        GpuImage target, ref VkImageLayout targetLayout, VkPipeline pipeline, PushConstants push)
    {
        Transition(commands, source, ref sourceLayout, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead);
        Transition(commands, target, ref targetLayout, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderSampledRead,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
        BeginTarget(commands, target);
        Bind(commands, pipeline, push);
        context.Device.Api.vkCmdDraw(commands, 3, 1, 0, 0);
        context.Device.Api.vkCmdEndRendering(commands);
    }

    private void Bind(VkCommandBuffer commands, VkPipeline pipeline, PushConstants push)
    {
        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
            0, (uint)sizeof(PushConstants), &push);
    }

    private void BeginTarget(VkCommandBuffer commands, GpuImage target)
    {
        var api = context.Device.Api;
        var viewport = new VkViewport(0, 0, extent.width, extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, extent.width, extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var attachment = new VkRenderingAttachmentInfo
        {
            imageView = target.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
        };
        var info = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, extent.width, extent.height),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &attachment,
        };
        api.vkCmdBeginRendering(commands, &info);
    }

    private void Transition(VkCommandBuffer commands, GpuImage image, ref VkImageLayout layout,
        VkImageLayout newLayout, VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess,
        VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
    {
        if (layout == newLayout)
            return;

        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = layout == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : srcStage,
            srcAccessMask = layout == VkImageLayout.Undefined ? VkAccessFlags2.None : srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = layout,
            newLayout = newLayout,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = image.Handle,
            subresourceRange = image.FullRange,
        };
        var dependency = new VkDependencyInfo
        {
            imageMemoryBarrierCount = 1,
            pImageMemoryBarriers = &barrier,
        };
        context.Device.Api.vkCmdPipelineBarrier2(commands, &dependency);
        layout = newLayout;
    }

    public void Dispose()
    {
        var api = context.Device.Api;
        var prefilterHandle = prefilter;
        var horizontal = blurHorizontal;
        var vertical = blurVertical;
        context.Release(() =>
        {
            api.vkDestroyPipeline(prefilterHandle, null);
            api.vkDestroyPipeline(horizontal, null);
            api.vkDestroyPipeline(vertical, null);
        });
        a?.Dispose();
        b?.Dispose();
        if (a != null)
        {
            context.Bindless.FreeTextureSlot(slotA);
            context.Bindless.FreeTextureSlot(slotB);
        }
    }
}
