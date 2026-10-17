using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Draws the HDR scene color onto the backbuffer (inside the backbuffer's rendering pass)
internal sealed unsafe class CompositePass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public uint SceneTexture;
    }

    private readonly GpuContext context;
    private readonly VkPipeline pipeline;

    public CompositePass(GpuContext context, VkFormat backbufferFormat)
    {
        this.context = context;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Composite.Code.ToArray(),
            VertexEntry = ShaderBytecode.Composite.VertexMain,
            FragmentEntry = ShaderBytecode.Composite.FragmentMain,
            ColorFormats = [backbufferFormat],
        }.Build(context, "composite");
    }

    public void Record(VkCommandBuffer commands, SceneTargets targets, VkExtent2D backbuffer)
    {
        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, backbuffer.width, backbuffer.height, 0, 1);
        var scissor = new VkRect2D(0, 0, backbuffer.width, backbuffer.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var push = new PushConstants { SceneTexture = targets.ColorSlot };
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
