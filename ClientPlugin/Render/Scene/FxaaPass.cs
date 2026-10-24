using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Resolves the tonemapped scene to the backbuffer with FXAA 3.11. When disabled it is a plain copy.
internal sealed unsafe class FxaaPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public uint Source;
        public float RcpFrameX;
        public float RcpFrameY;
        public uint LinearOutput;
        public uint Enabled;
        public uint Quality;
    }

    private readonly GpuContext context;
    private readonly VkPipeline pipeline;

    public FxaaPass(GpuContext context, VkFormat backbufferFormat)
    {
        this.context = context;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Fxaa.Code.ToArray(),
            VertexEntry = ShaderBytecode.Fxaa.VertexMain,
            FragmentEntry = ShaderBytecode.Fxaa.FragmentMain,
            ColorFormats = [backbufferFormat],
        }.Build(context, "fxaa");
    }

    public void Record(VkCommandBuffer commands, uint source, VkExtent2D extent, bool enabled, bool linearOutput, int quality)
    {
        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, extent.width, extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, extent.width, extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var push = new PushConstants
        {
            Source = source,
            RcpFrameX = 1f / extent.width,
            RcpFrameY = 1f / extent.height,
            LinearOutput = linearOutput ? 1u : 0u,
            Enabled = enabled ? 1u : 0u,
            Quality = (uint)Math.Clamp(quality, 0, 3),
        };
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All, 0, (uint)sizeof(PushConstants), &push);
        api.vkCmdDraw(commands, 3, 1, 0, 0);
    }

    public void Dispose()
    {
        var api = context.Device.Api;
        var handle = pipeline;
        context.Release(() => api.vkDestroyPipeline(handle, null));
    }
}
