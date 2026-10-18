using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageRender;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Scene;

// 1x1 auto-exposure: averages the HDR scene on a sparse grid with the shipped
// CalculateExposure formula and exposes the result to the composite as a texture.
internal sealed unsafe class ExposurePass : IDisposable
{
    public const VkFormat Format = VkFormat.R32Sfloat;

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public uint SceneTexture;
        public uint EyeAdaptation;
        public float ConstantLuminance;
        public float LuminanceExposure;
        public float MinLogBrightness;
        public float MaxLogBrightness;
        public float ExposureScale;
        public uint Pad1;
    }

    private readonly GpuContext context;
    private readonly VkPipeline pipeline;
    private GpuImage image;
    private uint slot;
    private VkImageLayout layout = VkImageLayout.Undefined;

    public uint Slot => slot;

    public ExposurePass(GpuContext context)
    {
        this.context = context;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Exposure.Code.ToArray(),
            VertexEntry = ShaderBytecode.Exposure.VertexMain,
            FragmentEntry = ShaderBytecode.Exposure.FragmentMain,
            ColorFormats = [Format],
        }.Build(context, "exposure");

        image = GpuImage.Create2D(context, 1, 1, 1, 1, Format,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        slot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(slot, image.View);
    }

    public void Record(VkCommandBuffer commands, uint sceneTexture, MyPostprocessSettings settings)
    {
        var api = context.Device.Api;
        var srcStage = layout == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.FragmentShader;
        var srcAccess = layout == VkImageLayout.Undefined ? VkAccessFlags2.None : VkAccessFlags2.ShaderSampledRead;
        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = VkPipelineStageFlags2.ColorAttachmentOutput,
            dstAccessMask = VkAccessFlags2.ColorAttachmentWrite,
            oldLayout = layout,
            newLayout = VkImageLayout.ColorAttachmentOptimal,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = image.Handle,
            subresourceRange = image.FullRange,
        };
        var dependency = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
        api.vkCmdPipelineBarrier2(commands, &dependency);
        layout = VkImageLayout.ColorAttachmentOptimal;

        var attachment = new VkRenderingAttachmentInfo
        {
            imageView = image.View,
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.DontCare,
            storeOp = VkAttachmentStoreOp.Store,
        };
        var info = new VkRenderingInfo
        {
            renderArea = new VkRect2D(0, 0, 1, 1),
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &attachment,
        };

        api.vkCmdBeginRendering(commands, &info);
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var data = settings.Data;
        var push = new PushConstants
        {
            SceneTexture = sceneTexture,
            EyeAdaptation = settings.EnableEyeAdaptation ? 1u : 0u,
            ConstantLuminance = data.ConstantLuminance,
            LuminanceExposure = data.LuminanceExposure,
            MinLogBrightness = settings.MinEyeAdaptationLogBrightness,
            MaxLogBrightness = settings.MaxEyeAdaptationLogBrightness,
            ExposureScale = Config.Current.ExposureScale,
        };
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
            0, (uint)sizeof(PushConstants), &push);
        api.vkCmdDraw(commands, 3, 1, 0, 0);
        api.vkCmdEndRendering(commands);

        barrier.srcStageMask = VkPipelineStageFlags2.ColorAttachmentOutput;
        barrier.srcAccessMask = VkAccessFlags2.ColorAttachmentWrite;
        barrier.dstStageMask = VkPipelineStageFlags2.FragmentShader;
        barrier.dstAccessMask = VkAccessFlags2.ShaderSampledRead;
        barrier.oldLayout = VkImageLayout.ColorAttachmentOptimal;
        barrier.newLayout = VkImageLayout.ShaderReadOnlyOptimal;
        api.vkCmdPipelineBarrier2(commands, &dependency);
        layout = VkImageLayout.ShaderReadOnlyOptimal;
    }

    public void Dispose()
    {
        var handle = pipeline;
        var api = context.Device.Api;
        context.Release(() => api.vkDestroyPipeline(handle, null));
        context.Bindless.FreeTextureSlot(slot);
        image?.Dispose();
    }
}
