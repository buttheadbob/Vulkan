using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Sprites;

internal enum SpriteBlend
{
    // Premultiplied alpha into color and alpha (MyBlendStateManager.BlendAlphaPremult): the screen
    Premultiplied,

    // Premultiplied alpha into color only, alpha is left as cleared (BlendAlphaPremultNoAlphaChannel): offscreen textures
    PremultipliedColorOnly,
}

// Draws the sprites of SpriteBatchers into the current dynamic rendering pass.
// Instances of all batches of a frame are appended to a host-visible buffer per frame in flight and read by
// the vertex shader through its device address; each draw is one instanced triangle strip.
internal sealed unsafe class SpriteRenderer : IDisposable
{
    private readonly GpuContext context;
    private readonly GpuBuffer[] instanceBuffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly Dictionary<(VkFormat, SpriteBlend), VkPipeline> pipelines = new();
    private int frameSlot;
    private ulong instanceOffset;

    public SpriteRenderer(GpuContext context)
    {
        this.context = context;
    }

    public void BeginFrame(int slot)
    {
        frameSlot = slot;
        instanceOffset = 0;
    }

    // Records the sprites into the dynamic rendering pass that is active on the command buffer.
    // coordinateSize: size of the space sprite coordinates are given in.
    // framebuffer: size of the render target in pixels.
    // boundViewport: the main viewport scaled by SpriteMainViewportScale; fullViewport: the whole target.
    public void Record(VkCommandBuffer commands, SpriteBatcher batcher, VkFormat targetFormat, SpriteBlend blend,
        Vector2 coordinateSize, VkExtent2D framebuffer, MyViewport boundViewport, MyViewport fullViewport)
    {
        if (batcher.InstanceCount == 0)
            return;

        var api = context.Device.Api;
        var bytes = (ulong)(batcher.InstanceCount * SpriteInstance.Size);
        var buffer = Allocate(bytes, out var offset);
        MemoryMarshal.AsBytes(batcher.Instances).CopyTo(new Span<byte>(buffer.Mapped + offset, (int)bytes));
        var baseAddress = buffer.DeviceAddress + offset;

        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, GetPipeline(targetFormat, blend));
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var invResolution = new Vector2(1f / coordinateSize.X, 1f / coordinateSize.Y);
        var width = (int)framebuffer.width;
        var height = (int)framebuffer.height;

        foreach (var draw in batcher.Draws)
        {
            if (draw.Count == 0)
                continue;

            var viewport = draw.IgnoreBounds ? fullViewport : boundViewport;
            var vkViewport = new VkViewport(viewport.OffsetX, viewport.OffsetY, viewport.Width, viewport.Height, 0, 1);
            api.vkCmdSetViewport(commands, 0, 1, &vkViewport);

            // Scissor rectangles are in sprite coordinates and follow the viewport mapping, like in MySpritesManager.Render
            VkRect2D scissor;
            if (draw.Scissor.HasValue)
            {
                var s = draw.Scissor.Value;
                var scaleX = viewport.Width * invResolution.X;
                var scaleY = viewport.Height * invResolution.Y;
                var x0 = Math.Clamp((int)(s.X * scaleX + viewport.OffsetX), 0, width);
                var y0 = Math.Clamp((int)(s.Y * scaleY + viewport.OffsetY), 0, height);
                var x1 = Math.Clamp((int)((s.X + s.Width) * scaleX + viewport.OffsetX), x0, width);
                var y1 = Math.Clamp((int)((s.Y + s.Height) * scaleY + viewport.OffsetY), y0, height);
                scissor = new VkRect2D(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0));
                if (scissor.extent.width == 0 || scissor.extent.height == 0)
                    continue;
            }
            else
            {
                scissor = new VkRect2D(0, 0, framebuffer.width, framebuffer.height);
            }

            api.vkCmdSetScissor(commands, 0, 1, &scissor);

            var push = new SpritePushConstants
            {
                Instances = baseAddress + (ulong)(draw.First * SpriteInstance.Size),
                TargetOffset = new Vector2(-1, -1),
                TargetSize = new Vector2(2, 2),
                InvResolution = invResolution,
            };
            api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All, 0, (uint)sizeof(SpritePushConstants), &push);
            api.vkCmdDraw(commands, 4, (uint)draw.Count, 0, 0);
        }
    }

    // Linear allocation from this frame's instance buffer. When it is full a larger one replaces it; the old one
    // stays alive until the frame has finished (deferred release), so already recorded draws remain valid.
    private GpuBuffer Allocate(ulong bytes, out ulong offset)
    {
        var buffer = instanceBuffers[frameSlot];
        if (buffer == null || instanceOffset + bytes > buffer.Size)
        {
            var capacity = Math.Max(Math.Max(bytes, (buffer?.Size ?? 0) * 2), 256ul * 1024);
            buffer?.Dispose();
            buffer = GpuBuffer.Create(context, capacity, VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            instanceBuffers[frameSlot] = buffer;
            instanceOffset = 0;
        }

        offset = instanceOffset;
        instanceOffset = (instanceOffset + bytes + 15) & ~15ul;
        return buffer;
    }

    private VkPipeline GetPipeline(VkFormat format, SpriteBlend blend)
    {
        if (!pipelines.TryGetValue((format, blend), out var pipeline))
        {
            pipeline = CreatePipeline(format, blend);
            pipelines[(format, blend)] = pipeline;
        }

        return pipeline;
    }

    private VkPipeline CreatePipeline(VkFormat targetFormat, SpriteBlend blend)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, ShaderBytecode.Sprite.Code);

        try
        {
            using var names = new VkStringArray([ShaderBytecode.Sprite.VertexMain, ShaderBytecode.Sprite.FragmentMain]);
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = module, pName = ((byte**)names)[0] };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = module, pName = ((byte**)names)[1] };

            var vertexInput = new VkPipelineVertexInputStateCreateInfo();
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { topology = VkPrimitiveTopology.TriangleStrip };
            var viewportState = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            var rasterization = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.CounterClockwise,
                lineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo();

            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = true,
                srcColorBlendFactor = VkBlendFactor.One,
                dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                colorBlendOp = VkBlendOp.Add,
                srcAlphaBlendFactor = VkBlendFactor.One,
                dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                alphaBlendOp = VkBlendOp.Add,
                colorWriteMask = blend == SpriteBlend.Premultiplied
                    ? VkColorComponentFlags.All
                    : VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B,
            };
            var colorBlend = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &blendAttachment };

            var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamicState = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dynamicStates };

            var format = targetFormat;
            var rendering = new VkPipelineRenderingCreateInfo { colorAttachmentCount = 1, pColorAttachmentFormats = &format };

            var createInfo = new VkGraphicsPipelineCreateInfo
            {
                pNext = &rendering,
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &inputAssembly,
                pViewportState = &viewportState,
                pRasterizationState = &rasterization,
                pMultisampleState = &multisample,
                pDepthStencilState = &depthStencil,
                pColorBlendState = &colorBlend,
                pDynamicState = &dynamicState,
                layout = context.Bindless.PipelineLayout,
            };

            VkPipeline result;
            api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &createInfo, null, &result).CheckResult("vkCreateGraphicsPipelines (sprites)");
            return result;
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }

    public void Dispose()
    {
        foreach (var buffer in instanceBuffers)
            buffer?.Dispose();

        var api = context.Device.Api;
        foreach (var pipeline in pipelines.Values)
        {
            var handle = pipeline;
            context.Release(() => api.vkDestroyPipeline(handle, null));
        }

        pipelines.Clear();
    }
}
