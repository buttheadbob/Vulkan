using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Space background drawn into the HDR scene target before geometry. It has no depth interaction;
// the depth format matches the scene pass so the pipeline is compatible with the same rendering info.
internal sealed unsafe class SkyboxPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public ulong Frame;
    }

    private readonly GpuContext context;
    private readonly VkPipeline pipeline;

    public SkyboxPass(GpuContext context)
    {
        this.context = context;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Skybox.Code.ToArray(),
            VertexEntry = ShaderBytecode.Skybox.VertexMain,
            FragmentEntry = ShaderBytecode.Skybox.FragmentMain,
            ColorFormats = [SceneTargets.ColorFormat],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = false,
            DepthWrite = false,
            CullMode = VkCullModeFlags.None,
        }.Build(context, "skybox");
    }

    public void Record(VkCommandBuffer commands, SceneTargets targets, ulong frameConstants)
    {
        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var push = new PushConstants { Frame = frameConstants };
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
            0, (uint)sizeof(PushConstants), &push);
        api.vkCmdDraw(commands, 3, 1, 0, 0);
    }

    public void Dispose()
    {
        var api = context.Device.Api;
        var handle = pipeline;
        context.Release(() => api.vkDestroyPipeline(handle, null));
    }
}
