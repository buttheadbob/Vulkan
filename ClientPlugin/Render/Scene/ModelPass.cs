using System;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Models;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using VRageRender.Import;
using VRageRender.Messages;

namespace ClientPlugin.Render.Scene;

// Per-part material data referenced by a pointer in the push constants (std430, 48 bytes).
[StructLayout(LayoutKind.Sequential)]
internal struct DrawMaterial
{
    public Vector4 ColorMulEmissive; // rgb: color multiplier, w: emissivity
    public uint ColorMetal;
    public uint NormalGloss;
    public uint Extensions;
    public uint Alphamask;
    public uint Flags;
    public float DepthBias;
    public uint Pad0;
    public uint Pad1;

    public const uint HasColorMetal = 1 << 0;
    public const uint HasNormalGloss = 1 << 1;
    public const uint HasExtensions = 1 << 2;
    public const uint HasAlphamask = 1 << 3;
    public const uint HasTangent = 1 << 4;
    public const uint AlphaMasked = 1 << 5;
    public const uint Instanced = 1 << 6;

    public const uint Size = 48;
}

// Must match Shaders/Scene/Model.slang
[StructLayout(LayoutKind.Sequential)]
internal struct ModelPushConstants
{
    public Vector4 World0;
    public Vector4 World1;
    public Vector4 World2;
    public Vector4 World3;
    public Vector4 ColorMaskHsv;
    public ulong Frame;
    public ulong Material;
    public ulong Vertices;
    public ulong Instances;
    public uint InstanceStart;
}

// Opaque world geometry (Phase 2): MWM meshes with the stock material channels and a forward sun + ambient.
internal sealed unsafe class ModelPass : IDisposable
{
    private readonly GpuContext context;
    private readonly ModelCache models;
    private readonly InstanceBuffers instanceBuffers;
    private readonly VkPipeline pipeline;

    public ModelPass(GpuContext context, ModelCache models, InstanceBuffers instanceBuffers)
    {
        this.context = context;
        this.models = models;
        this.instanceBuffers = instanceBuffers;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Model.Code.ToArray(),
            VertexEntry = ShaderBytecode.Model.VertexMain,
            FragmentEntry = ShaderBytecode.Model.FragmentMain,
            ColorFormats = [SceneTargets.ColorFormat],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
        }.Build(context, "model");
    }

    // Records all opaque actors into the scene targets. Returns the number of draws recorded.
    public int Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        MyEnvironmentData environment, SceneActors actors)
    {
        var frameAllocation = context.Transient.Allocate<FrameConstants>();
        frameAllocation.As<FrameConstants>() = camera.BuildFrameConstants(environment);

        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var draws = 0;
        foreach (var actor in actors.All)
        {
            if (!actor.Visible || (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) != 0)
                continue;

            var model = models.Get(actor.Model);
            if (model == null)
                continue;

            var instances = (InstanceBuffer)null;
            var instanceStart = 0;
            var instanceCount = 1;
            if (actor.HasInstances)
            {
                instances = instanceBuffers.Get(actor.InstanceBufferId);
                if (instances?.Buffer == null || instances.Type != MyRenderInstanceBufferType.Cube)
                    continue;

                instanceStart = actor.InstanceStart;
                instanceCount = Math.Min(actor.InstanceCount, instances.Count - instanceStart);
                if (instanceCount <= 0)
                    continue;
            }

            var world = BuildWorld(actor, camera.Position, out var staticCount);
            api.vkCmdBindIndexBuffer(commands, model.Indices.Handle, 0, VkIndexType.Uint32);

            for (var instance = 0; instance < staticCount; instance++)
            {
                var matrix = staticCount == 1 ? world : BuildStaticWorld(actor, camera.Position, instance);
                var colorMask = new Vector4(actor.ColorMaskHsv, 0);

                foreach (var part in model.Parts)
                {
                    if (!IsOpaque(part.Technique) || part.IndexCount == 0)
                        continue;

                    var material = BuildMaterial(actor, part, model, instances != null);
                    var materialAllocation = context.Transient.Allocate<DrawMaterial>();
                    materialAllocation.As<DrawMaterial>() = material;

                    // Shader uses dot(position, row), so each float4 is a column of the XNA matrix
                    var worldT = Matrix.Transpose(matrix);
                    var push = new ModelPushConstants
                    {
                        World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                        World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                        World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                        World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                        ColorMaskHsv = colorMask,
                        Frame = frameAllocation.Address,
                        Material = materialAllocation.Address,
                        Vertices = model.Vertices.DeviceAddress,
                        Instances = instances?.Buffer.DeviceAddress ?? 0,
                        InstanceStart = (uint)instanceStart,
                    };

                    api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
                        0, (uint)sizeof(ModelPushConstants), &push);
                    api.vkCmdDrawIndexed(commands, (uint)part.IndexCount, (uint)instanceCount,
                        (uint)part.IndexStart, 0, 0);
                    draws++;
                }
            }
        }

        return draws;
    }

    private static Matrix BuildWorld(SceneActor actor, Vector3D cameraPosition, out int count)
    {
        count = 1;
        if (actor.StaticMatrices != null)
        {
            count = actor.StaticMatrices.Length;
            return default;
        }

        var world = (Matrix)actor.WorldMatrix;
        world.Translation = (Vector3)(actor.WorldMatrix.Translation - cameraPosition);

        if (actor.Scale != 1f)
        {
            world.M11 *= actor.Scale;
            world.M12 *= actor.Scale;
            world.M13 *= actor.Scale;
            world.M21 *= actor.Scale;
            world.M22 *= actor.Scale;
            world.M23 *= actor.Scale;
            world.M31 *= actor.Scale;
            world.M32 *= actor.Scale;
            world.M33 *= actor.Scale;
        }

        return world;
    }

    // MyStaticGroupComponent: the group matrix is the translation, local matrices are relative to it
    private static Matrix BuildStaticWorld(SceneActor actor, Vector3D cameraPosition, int index)
    {
        var world = actor.StaticMatrices[index];
        world.Translation += (Vector3)(actor.StaticTranslation - cameraPosition);
        return world;
    }

    private static DrawMaterial BuildMaterial(SceneActor actor, ModelPart part, GpuModel model, bool instanced)
    {
        var flags = 0u;
        if (part.ColorMetal != null) flags |= DrawMaterial.HasColorMetal;
        if (part.NormalGloss != null) flags |= DrawMaterial.HasNormalGloss;
        if (part.Extensions != null) flags |= DrawMaterial.HasExtensions;
        if (part.Alphamask != null) flags |= DrawMaterial.HasAlphamask;
        if (model.HasTangents) flags |= DrawMaterial.HasTangent;
        if (part.Technique is MyMeshDrawTechnique.ALPHA_MASKED or MyMeshDrawTechnique.ALPHA_MASKED_SINGLE_SIDED)
            flags |= DrawMaterial.AlphaMasked;
        if (instanced)
            flags |= DrawMaterial.Instanced;

        var colorMul = actor.ColorMul;
        var emissivity = actor.Emissivity;
        if (actor.MaterialOverrides != null && !string.IsNullOrEmpty(part.MaterialName) &&
            actor.MaterialOverrides.TryGetValue(part.MaterialName, out var materialOverride))
        {
            if (materialOverride.ColorMul.HasValue)
                colorMul = materialOverride.ColorMul.Value;
            if (materialOverride.Emissivity.HasValue)
                emissivity = materialOverride.Emissivity.Value;
        }

        return new DrawMaterial
        {
            ColorMulEmissive = new Vector4(colorMul, emissivity),
            ColorMetal = TextureCache.SlotOf(part.ColorMetal),
            NormalGloss = TextureCache.SlotOf(part.NormalGloss),
            Extensions = TextureCache.SlotOf(part.Extensions),
            Alphamask = TextureCache.SlotOf(part.Alphamask),
            Flags = flags,
            DepthBias = actor.DepthBias,
        };
    }

    private static bool IsOpaque(MyMeshDrawTechnique technique) =>
        technique is MyMeshDrawTechnique.MESH
            or MyMeshDrawTechnique.ALPHA_MASKED
            or MyMeshDrawTechnique.ALPHA_MASKED_SINGLE_SIDED
            or MyMeshDrawTechnique.MESH_INSTANCED
            or MyMeshDrawTechnique.MESH_INSTANCED_GENERIC;

    public void Dispose()
    {
        var api = context.Device.Api;
        var handle = pipeline;
        context.Release(() => api.vkDestroyPipeline(handle, null));
    }
}
