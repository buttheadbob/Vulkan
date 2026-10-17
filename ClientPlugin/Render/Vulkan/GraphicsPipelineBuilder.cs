using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Describes a graphics pipeline for dynamic rendering with the shared bindless pipeline layout.
// Viewport and scissor are always dynamic. No vertex input: vertex data is fetched through buffer device addresses.
internal sealed class GraphicsPipelineBuilder
{
    public ReadOnlyMemory<uint> Code { get; init; }           // SPIR-V module from ShaderBytecode
    public string VertexEntry { get; init; }
    public string FragmentEntry { get; init; }                // null for depth-only pipelines
    public VkFormat[] ColorFormats { get; init; } = [];
    public VkFormat DepthFormat { get; init; } = VkFormat.Undefined;
    public VkPrimitiveTopology Topology { get; init; } = VkPrimitiveTopology.TriangleList;
    public VkCullModeFlags CullMode { get; init; } = VkCullModeFlags.None;
    public VkFrontFace FrontFace { get; init; } = VkFrontFace.CounterClockwise;
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
    public VkCompareOp DepthCompare { get; init; } = VkCompareOp.GreaterOrEqual; // Reverse Z
    public VkPipelineColorBlendAttachmentState Blend { get; init; } = Opaque;

    public static readonly VkPipelineColorBlendAttachmentState Opaque = new()
    {
        blendEnable = false,
        colorWriteMask = VkColorComponentFlags.All,
    };

    public unsafe VkPipeline Build(GpuContext context, string debugName)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, Code.Span);

        try
        {
            using var names = new VkStringArray(FragmentEntry == null ? [VertexEntry] : [VertexEntry, FragmentEntry]);
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = module, pName = ((byte**)names)[0] };
            var stageCount = 1u;
            if (FragmentEntry != null)
            {
                stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = module, pName = ((byte**)names)[1] };
                stageCount = 2;
            }

            var vertexInput = new VkPipelineVertexInputStateCreateInfo();
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { topology = Topology };
            var viewportState = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            var rasterization = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = CullMode,
                frontFace = FrontFace,
                lineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo
            {
                depthTestEnable = DepthTest,
                depthWriteEnable = DepthWrite,
                depthCompareOp = DepthCompare,
            };

            var attachments = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, ColorFormats.Length)];
            for (var i = 0; i < ColorFormats.Length; i++)
                attachments[i] = Blend;
            var colorBlend = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)ColorFormats.Length, pAttachments = attachments };

            var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamicState = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dynamicStates };

            fixed (VkFormat* colorFormats = ColorFormats)
            {
                var rendering = new VkPipelineRenderingCreateInfo
                {
                    colorAttachmentCount = (uint)ColorFormats.Length,
                    pColorAttachmentFormats = colorFormats,
                    depthAttachmentFormat = DepthFormat,
                };

                var createInfo = new VkGraphicsPipelineCreateInfo
                {
                    pNext = &rendering,
                    stageCount = stageCount,
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

                VkPipeline pipeline;
                api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &createInfo, null, &pipeline).CheckResult($"vkCreateGraphicsPipelines ({debugName})");
                return pipeline;
            }
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }
}
