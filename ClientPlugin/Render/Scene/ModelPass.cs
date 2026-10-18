using System;
using System.Collections.Generic;
using System.IO;
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
    public const uint MetalnessColorable = 1 << 7;
    public const uint Decal = 1 << 8;
    public const uint Screen = 1 << 9;

    public const uint Size = 48;
}

// Stored in the transient allocator and read through a pointer (std430, 96 bytes).
[StructLayout(LayoutKind.Sequential)]
internal struct DrawTransparentMaterial
{
    public Vector4 Color;            // premultiplied linear
    public Vector4 ColorAdd;
    public Vector4 LightMultiplier;
    public Vector4 Params;           // x reflectivity, y fresnel, z glossTextureAdd, w specularColorFactor
    public Vector4 Params2;          // x gloss, y reflectionShadow, z alphaSaturation, w unused
    public uint ColorTexture;
    public uint GlossTexture;
    public uint Flags;               // 1: alpha cutout, 2: instanced
    public uint Pad0;
}

// Must match Shaders/Scene/Skinning.slang
[StructLayout(LayoutKind.Sequential)]
internal struct SkinningPushConstants
{
    public Vector4 World0;
    public Vector4 World1;
    public Vector4 World2;
    public Vector4 World3;
    public ulong Input;
    public ulong Output;
    public ulong Bones;
    public uint VertexCount;
    public uint BoneCount;
}

// Must match Shaders/Scene/BonePalette.slang
[StructLayout(LayoutKind.Sequential)]
internal struct PalettePushConstants
{
    public ulong SkinTransforms;
    public ulong BoneTransforms;
    public ulong Indices;
    public ulong Output;
    public uint IndexCount;
    public uint SkeletonCount;
}

// Must match Shaders/Scene/Transparent.slang
[StructLayout(LayoutKind.Sequential)]
internal struct TransparentPushConstants
{
    public Vector4 World0;
    public Vector4 World1;
    public Vector4 World2;
    public Vector4 World3;
    public ulong Frame;
    public ulong Material;
    public ulong Vertices;
    public ulong Instances;
    public uint InstanceStart;
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
    public ulong DrawData; // 0 for direct draws; bucket draw data for the GPU-driven path
}

// Opaque world geometry (Phase 2): MWM meshes with the stock material channels and a forward sun + ambient.
internal sealed unsafe class ModelPass : IDisposable
{
    private struct TransparentDraw
    {
        public SceneActor Actor;
        public GpuLod Lod;
        public ModelPart Part;
        public Matrix World;
        public ulong Vertices;
        public InstanceBuffer Instances;
        public int InstanceStart;
        public int InstanceCount;
    }

    // Persistent opaque part data, rebuilt only when the actor list/model/properties change.
    private sealed class CachedTransparentPart
    {
        public GpuLod Lod;
        public ModelPart Part;
    }

    private sealed class CachedActor
    {
        public SceneActor Actor;
        public GpuModel Model;
        public uint RangeBase;
        public uint LodStateIndex;
        public int StaticIndex = -1;
        public List<CachedTransparentPart>[] TransparentByLod;
    }

    // OIT accumulation (RGBA16F): additive premultiplied color and alpha.
    private static readonly VkPipelineColorBlendAttachmentState OitAccumBlend = new()
    {
        blendEnable = true,
        srcColorBlendFactor = VkBlendFactor.One,
        dstColorBlendFactor = VkBlendFactor.One,
        colorBlendOp = VkBlendOp.Add,
        srcAlphaBlendFactor = VkBlendFactor.One,
        dstAlphaBlendFactor = VkBlendFactor.One,
        alphaBlendOp = VkBlendOp.Add,
        colorWriteMask = VkColorComponentFlags.All,
    };

    // OIT revealage (R16F): started at 1, each layer multiplies it by (1 - alpha).
    // OneMinusSrcColor uses the shader's alpha output as the factor, so no alpha channel is needed.
    private static readonly VkPipelineColorBlendAttachmentState OitRevealBlend = new()
    {
        blendEnable = true,
        srcColorBlendFactor = VkBlendFactor.Zero,
        dstColorBlendFactor = VkBlendFactor.OneMinusSrcColor,
        colorBlendOp = VkBlendOp.Add,
        srcAlphaBlendFactor = VkBlendFactor.Zero,
        dstAlphaBlendFactor = VkBlendFactor.One,
        alphaBlendOp = VkBlendOp.Add,
        colorWriteMask = VkColorComponentFlags.R,
    };

    private readonly GpuContext context;
    private readonly ModelCache models;
    private readonly InstanceBuffers instanceBuffers;
    private readonly TextureCache textures;
    private readonly GpuSceneDraws gpuDraws;
    private readonly HashSet<uint> preparedActors = new();
    private readonly List<TransparentDraw> transparentDraws = new();
    private readonly HashSet<uint> reportedSkinning = new();

    private readonly List<CachedActor> cachedActors = new();
    private readonly List<GpuScenePart> cachedParts = new();
    private readonly List<GpuLodRange> cachedRanges = new();
    private readonly List<GpuSceneActor> frameActors = new();
    private GpuBuffer partBuffer;
    private GpuBuffer rangeBuffer;
    private GpuBuffer lodStateBuffer;
    private long cachedRevision = -1;
    private int cachedModelVersion = -1;
    private int cachedPartCount;
    private bool gpuSubmitted;
    private readonly VkPipeline pipeline;
    private readonly VkPipeline pipelineNoCull;
    private readonly VkPipeline pipelineTransparent;
    private readonly VkPipeline pipelineTransparentNoCull;
    private readonly VkPipeline skinningPipeline;
    private readonly VkPipeline palettePipeline;

    public ModelPass(GpuContext context, ModelCache models, InstanceBuffers instanceBuffers, TextureCache textures)
    {
        this.context = context;
        this.models = models;
        this.instanceBuffers = instanceBuffers;
        this.textures = textures;
        gpuDraws = new GpuSceneDraws(context);

        GraphicsPipelineBuilder Builder(VkCullModeFlags cullMode) => new()
        {
            Code = ShaderBytecode.Model.Code.ToArray(),
            VertexEntry = ShaderBytecode.Model.VertexMain,
            FragmentEntry = ShaderBytecode.Model.FragmentMain,
            ColorFormats = [SceneTargets.ColorFormat],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = cullMode,

            // D3D's default rasterizer state treats clockwise as front (IsFrontCounterClockwise=false).
            // The projection Y flip cancels Vulkan's opposite framebuffer Y direction, so the final
            // screen-space winding is still clockwise and Vulkan must be told the same.
            FrontFace = VkFrontFace.Clockwise,
        };

        // Transparent parts render into the two OIT targets, not into the scene color.
        GraphicsPipelineBuilder TransparentBuilder(VkCullModeFlags cullMode) => new()
        {
            Code = ShaderBytecode.Transparent.Code.ToArray(),
            VertexEntry = ShaderBytecode.Transparent.VertexMain,
            FragmentEntry = ShaderBytecode.Transparent.FragmentMain,
            ColorFormats = [SceneTargets.OitAccumFormat, SceneTargets.OitRevealFormat],
            BlendStates = [OitAccumBlend, OitRevealBlend],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = cullMode,
            FrontFace = VkFrontFace.Clockwise,
        };

        pipeline = Builder(VkCullModeFlags.Back).Build(context, "model");
        pipelineNoCull = Builder(VkCullModeFlags.None).Build(context, "model-nocull");
        pipelineTransparent = TransparentBuilder(VkCullModeFlags.Back).Build(context, "transparent");
        pipelineTransparentNoCull = TransparentBuilder(VkCullModeFlags.None).Build(context, "transparent-nocull");
        skinningPipeline = BuildComputePipeline(context, ShaderBytecode.Skinning.Code,
            ShaderBytecode.Skinning.Main, "skinning");
        palettePipeline = BuildComputePipeline(context, ShaderBytecode.BonePalette.Code,
            ShaderBytecode.BonePalette.Main, "bone-palette");
    }

    private static VkPipeline BuildComputePipeline(GpuContext context, ReadOnlySpan<uint> code,
        string entry, string name)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, code);
        try
        {
            using var names = new VkStringArray([entry]);
            var stage = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Compute,
                module = module,
                pName = ((byte**)names)[0],
            };
            var createInfo = new VkComputePipelineCreateInfo
            {
                stage = stage,
                layout = context.Bindless.PipelineLayout,
            };

            VkPipeline pipeline;
            api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &createInfo, null, &pipeline)
                .CheckResult($"vkCreateComputePipelines ({name})");
            return pipeline;
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }

    // GPU skinning pre-pass: deform every visible skinned actor into a static vertex buffer that the
    // forward pass reads. Must be recorded before the dynamic rendering pass begins.
    public void RecordSkinning(VkCommandBuffer commands, CameraState camera, MyNewLoddingSettings lodding,
        SceneActors actors)
    {
        var api = context.Device.Api;
        var settings = lodding.Global;
        var any = false;

        foreach (var actor in actors.All)
            actor.SkinnedVertexAddress = 0;

        foreach (var actor in actors.All)
        {
            if (!actor.Visible || (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) != 0)
                continue;
            if (actor.HasInstances || actor.StaticMatrices != null)
                continue;

            var model = models.Get(actor.Model);
            if (model == null || !model.Lods[0].Skinned)
                continue;

            var worldD = actors.GetWorldMatrix(actor);
            var distance = (float)(worldD.Translation - camera.Position).Length();
            var lod = SelectLod(model, distance, actor.Scale, camera, settings, ref actor.Lod);
            if (!lod.Skinned || lod.SkinnedVertices == null || lod.VertexCount == 0)
                continue;

            var skinTransforms = actor.SkinTransforms;
            var indexUInts = actor.SkeletonIndexUInts;
            var transforms = actor.BoneTransforms;
            if (skinTransforms == null || indexUInts == null || transforms == null || indexUInts.Length == 0)
            {
                // Armor plates carry blend data but have no skeleton: they render from the static
                // stream through the GPU path. Only complain about a model that really has SKINNED
                // parts and is missing its skeleton.
                if (lod.HasSkinnedParts && reportedSkinning.Add(actor.Id))
                    RenderLog.Info($"Skinning: {model.Name} actor {actor.Id} has no skeleton data yet " +
                                   $"(skin transforms {skinTransforms?.Length ?? 0}, " +
                                   $"indices {indexUInts?.Length ?? 0}, transforms {transforms?.Length ?? 0})");
                continue;
            }

            var indexCount = indexUInts.Length;
            var skeletonCount = Math.Min(skinTransforms.Length, transforms.Length);
            if (skeletonCount == 0)
                continue;

            // Upload the inputs and let the GPU assemble the palette (skin * absolute per model bone)
            var skinAllocation = context.Transient.Allocate<Matrix>(skinTransforms.Length);
            skinTransforms.AsSpan().CopyTo(skinAllocation.AsSpan<Matrix>(skinTransforms.Length));
            var transformAllocation = context.Transient.Allocate<Matrix>(transforms.Length);
            transforms.AsSpan(0, transforms.Length).CopyTo(transformAllocation.AsSpan<Matrix>(transforms.Length));
            var indexAllocation = context.Transient.Allocate((ulong)indexUInts.Length * sizeof(uint), 4);
            indexUInts.AsSpan().CopyTo(indexAllocation.AsSpan<uint>(indexUInts.Length));
            var paletteAllocation = context.Transient.Allocate<Matrix>(indexCount);

            var palettePush = new PalettePushConstants
            {
                SkinTransforms = skinAllocation.Address,
                BoneTransforms = transformAllocation.Address,
                Indices = indexAllocation.Address,
                Output = paletteAllocation.Address,
                IndexCount = (uint)indexCount,
                SkeletonCount = (uint)skeletonCount,
            };

            api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, palettePipeline);
            api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
                0, (uint)sizeof(PalettePushConstants), &palettePush);
            api.vkCmdDispatch(commands, (uint)((indexCount + 63) / 64), 1, 1);

            var paletteBarrier = new VkMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.ComputeShader,
                srcAccessMask = VkAccessFlags2.ShaderWrite,
                dstStageMask = VkPipelineStageFlags2.ComputeShader,
                dstAccessMask = VkAccessFlags2.ShaderRead,
            };
            var paletteDependency = new VkDependencyInfo
            {
                memoryBarrierCount = 1,
                pMemoryBarriers = &paletteBarrier,
            };
            api.vkCmdPipelineBarrier2(commands, &paletteDependency);

            var outputAllocation = context.Transient.Allocate((ulong)lod.VertexCount * 32, 16);

            var world = BuildWorld(worldD, actor.Scale, camera.Position);
            var worldT = Matrix.Transpose(world);
            var push = new SkinningPushConstants
            {
                World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                Input = lod.SkinnedVertices.DeviceAddress,
                Output = outputAllocation.Address,
                Bones = paletteAllocation.Address,
                VertexCount = lod.VertexCount,
                BoneCount = (uint)indexCount,
            };

            api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, skinningPipeline);
            api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
                0, (uint)sizeof(SkinningPushConstants), &push);
            api.vkCmdDispatch(commands, (lod.VertexCount + 63) / 64, 1, 1);

            actor.SkinnedVertexAddress = outputAllocation.Address;
            any = true;
            if (reportedSkinning.Add(actor.Id))
                RenderLog.Info($"Skinning: {model.Name} actor {actor.Id}, {lod.VertexCount} vertices, {indexCount} bones");
        }

        if (!any)
            return;

        var barrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.ShaderWrite,
            dstStageMask = VkPipelineStageFlags2.VertexShader,
            dstAccessMask = VkAccessFlags2.ShaderRead,
        };
        var dependency = new VkDependencyInfo
        {
            memoryBarrierCount = 1,
            pMemoryBarriers = &barrier,
        };
        api.vkCmdPipelineBarrier2(commands, &dependency);
    }

    public int GpuPartCount => cachedPartCount;
    public GpuDiagnosticsCounters Diagnostics => gpuDraws.Counters;
    public uint LastDirectTriangles { get; private set; }

    // Rebuilds the persistent opaque parts when actors/models/materials change. The per-frame path then
    // uploads only actor world/colour/visibility state; the GPU selects the LOD and appends part draws.
    private void EnsureCache(SceneActors actors)
    {
        if (actors.Revision == cachedRevision && models.Version == cachedModelVersion)
            return;

        cachedRevision = actors.Revision;
        cachedModelVersion = models.Version;
        cachedActors.Clear();
        cachedParts.Clear();
        cachedRanges.Clear();

        foreach (var actor in actors.All)
        {
            var model = models.Get(actor.Model);
            if (model == null || model.LodCount == 0)
                continue;

            // A model with blend data needs the skinned path when the actor actually has a skeleton
            // (characters). Without skeleton data (armor plates) its MESH parts render statically.
            var skinned = false;
            foreach (var lod in model.Lods)
                skinned |= lod.Skinned;
            var hasSkeleton = actor.SkinTransforms != null && actor.SkeletonIndexUInts != null;
            if (skinned && hasSkeleton)
                continue;

            var rangeBase = (uint)cachedRanges.Count;
            var transparentByLod = new List<CachedTransparentPart>[model.LodCount];

            for (var lodIndex = 0; lodIndex < model.LodCount; lodIndex++)
            {
                var lod = model.Lods[lodIndex];
                var transparent = new List<CachedTransparentPart>();
                transparentByLod[lodIndex] = transparent;
                var firstPart = cachedParts.Count;

                foreach (var part in lod.Parts)
                {
                    if (part.IndexCount == 0)
                        continue;

                    var materialFlags = SceneActors.GetMaterialFlags(actor, part.MaterialName);
                    if ((materialFlags & RenderFlags.Visible) == 0)
                        continue;

                    if (part.Technique.IsTransparent())
                    {
                        if (part.Transparent != null)
                            transparent.Add(new CachedTransparentPart { Lod = lod, Part = part });
                        continue;
                    }

                    if (!IsOpaque(part.Technique))
                        continue;

                    ResolveTextures(actor, part, out var cm, out var ng, out var ext, out var alpha);
                    var material = BuildMaterial(actor, part, lod, actor.HasInstances, cm, ng, ext, alpha,
                        (materialFlags & RenderFlags.MetalnessColorable) != 0);
                    cachedParts.Add(new GpuScenePart
                    {
                        Material = material,
                        Vertices = lod.Vertices.DeviceAddress,
                        Indices = lod.Indices.DeviceAddress,
                        IndexStart = (uint)part.IndexStart,
                        IndexCount = (uint)part.IndexCount,
                    });
                }

                cachedRanges.Add(new GpuLodRange
                {
                    Start = (uint)firstPart,
                    Count = (uint)(cachedParts.Count - firstPart),
                });
            }

            var staticCount = actor.StaticMatrices?.Length ?? 0;
            if (staticCount > 0)
            {
                for (var staticIndex = 0; staticIndex < staticCount; staticIndex++)
                {
                    cachedActors.Add(new CachedActor
                    {
                        Actor = actor,
                        Model = model,
                        RangeBase = rangeBase,
                        LodStateIndex = (uint)cachedActors.Count,
                        StaticIndex = staticIndex,
                        TransparentByLod = transparentByLod,
                    });
                }
            }
            else
            {
                cachedActors.Add(new CachedActor
                {
                    Actor = actor,
                    Model = model,
                    RangeBase = rangeBase,
                    LodStateIndex = (uint)cachedActors.Count,
                    TransparentByLod = transparentByLod,
                });
            }
        }

        cachedPartCount = cachedParts.Count;
        partBuffer?.Dispose();
        rangeBuffer?.Dispose();
        lodStateBuffer?.Dispose();
        partBuffer = null;
        rangeBuffer = null;
        lodStateBuffer = null;

        if (cachedPartCount > 0)
        {
            partBuffer = GpuBuffer.Create(context, (ulong)(cachedPartCount * GpuScenePart.Size),
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            var parts = new Span<GpuScenePart>(partBuffer.Mapped, cachedPartCount);
            for (var i = 0; i < cachedPartCount; i++)
                parts[i] = cachedParts[i];
        }

        if (cachedRanges.Count > 0)
        {
            rangeBuffer = GpuBuffer.Create(context, (ulong)(cachedRanges.Count * sizeof(GpuLodRange)),
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            var ranges = new Span<GpuLodRange>(rangeBuffer.Mapped, cachedRanges.Count);
            for (var i = 0; i < cachedRanges.Count; i++)
                ranges[i] = cachedRanges[i];
        }

        if (cachedActors.Count > 0)
        {
            lodStateBuffer = GpuBuffer.Create(context, (ulong)(cachedActors.Count * sizeof(uint)),
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            var states = new Span<uint>(lodStateBuffer.Mapped, cachedActors.Count);
            for (var i = 0; i < cachedActors.Count; i++)
                states[i] = (uint)Math.Clamp(cachedActors[i].Actor.Lod, 0, cachedActors[i].Model.LodCount - 1);
        }
    }

    // Builds per-frame actor state and dispatches the GPU culling/LOD compute. Parts/materials are persistent.
    public void RecordPrepare(VkCommandBuffer commands, int frameSlot, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors)
    {
        EnsureCache(actors);
        var settings = lodding.Global;
        var frustum = new Frustum(camera.CullingViewProjection);
        preparedActors.Clear();
        transparentDraws.Clear();
        frameActors.Clear();
        gpuSubmitted = false;
        gpuDraws.Begin(frameSlot, Math.Max(cachedPartCount, 1));

        foreach (var cached in cachedActors)
        {
            var actor = cached.Actor;
            if (!actor.Visible || (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) != 0)
                continue;

            var model = cached.Model;
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

            var world = cached.StaticIndex >= 0
                ? BuildStaticWorld(actor, camera.Position, cached.StaticIndex)
                : BuildWorld(actors.GetWorldMatrix(actor), actor.Scale, camera.Position);
            var distance = world.Translation.Length();
            var localBounds = actor.LocalAabb ?? model.Bounds;
            var worldBounds = localBounds.Transform(world);
            var center = (worldBounds.Min + worldBounds.Max) * 0.5f;
            var radius = (worldBounds.Max - worldBounds.Min).Length() * 0.5f;
            var maxViewDistance = (actor.Flags & RenderFlags.DrawOutsideViewDistance) != 0
                ? float.MaxValue
                : actor.MaxViewDistance;
            var cpuVisible = distance - radius <= maxViewDistance && frustum.Intersects(worldBounds);

            preparedActors.Add(actor.Id);
            if (actor.VisibilityUpdates)
                MyRenderProxy.VisibleObjectsWrite.Add(actor.Id);

            var worldT = Matrix.Transpose(world);
            var lodModel = SelectLod(model, distance, actor.Scale, camera, settings, ref actor.Lod);

            if (cpuVisible && cached.TransparentByLod.Length > 0)
            {
                var lodIndex = Math.Clamp(actor.Lod, 0, cached.TransparentByLod.Length - 1);
                foreach (var transparent in cached.TransparentByLod[lodIndex])
                {
                    transparentDraws.Add(new TransparentDraw
                    {
                        Actor = actor,
                        Lod = transparent.Lod,
                        Part = transparent.Part,
                        World = world,
                        Vertices = transparent.Lod.Vertices.DeviceAddress,
                        Instances = instances,
                        InstanceStart = instanceStart,
                        InstanceCount = instances != null ? instanceCount : 1,
                    });
                }
            }

            var lodCount = Math.Min(model.LodCount, 8);
            Vector4 lodDistances0 = default;
            Vector4 lodDistances1 = default;
            for (var i = 0; i < lodCount - 1 && i < 8; i++)
            {
                var threshold = i < model.LodDistances.Length ? model.LodDistances[i] : 0f;
                if (i < 4)
                    lodDistances0[i] = threshold;
                else
                    lodDistances1[i - 4] = threshold;
            }

            frameActors.Add(new GpuSceneActor
            {
                World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                ColorMaskHsv = new Vector4(actor.ColorMaskHsv, 0),
                Bounds = new Vector4(center, radius),
                Origin = new Vector4(world.Translation, actor.Scale),
                LodDistances0 = lodDistances0,
                LodDistances1 = lodDistances1,
                Instances = instances?.Buffer.DeviceAddress ?? 0,
                InstanceStart = (uint)instanceStart,
                InstanceCount = (uint)instanceCount,
                Flags = (actor.Flags & RenderFlags.NoBackFaceCulling) != 0 ? GpuSceneActor.NoBackFaceCulling : 0u,
                RangeBase = cached.RangeBase,
                LodStateIndex = cached.LodStateIndex,
                LodCount = (uint)lodCount,
                MaxViewDistance = maxViewDistance,
                Pad0 = 0,
            });
        }

        // Visibility updates for skinned/static-group actors happen in Record; simple actors were
        // published above. Static groups are not in the persistent cache.
        if (frameActors.Count == 0 || cachedPartCount == 0 || partBuffer == null ||
            rangeBuffer == null || lodStateBuffer == null)
            return;

        var allocation = context.Transient.Allocate<GpuSceneActor>(frameActors.Count);
        var span = allocation.AsSpan<GpuSceneActor>(frameActors.Count);
        for (var i = 0; i < frameActors.Count; i++)
            span[i] = frameActors[i];

        gpuDraws.Dispatch(commands, camera.CullingViewProjection, camera.LodCoefficient,
            allocation.Address, (uint)frameActors.Count, partBuffer.DeviceAddress,
            rangeBuffer.DeviceAddress, lodStateBuffer.DeviceAddress, lodding);
        gpuSubmitted = true;
    }

    // Records all opaque actors into the scene targets. Returns the number of draws recorded.
    public int Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, ulong frameConstants, out int culled)
    {
        var frustum = new Frustum(camera.CullingViewProjection);
        var loddingSettings = lodding.Global;

        var api = context.Device.Api;
        VkPipeline boundPipeline = VkPipeline.Null;

        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var draws = 0;
        culled = 0;
        LastDirectTriangles = 0;

        // GPU-driven opaque buckets: the culling compute already appended the visible parts, so the
        // CPU issues one indirect draw per bucket (cull/no-cull) instead of one per part.
        if (gpuSubmitted)
        {
            for (var bucket = 0; bucket < 2; bucket++)
            {
                var bucketPipeline = bucket == 0 ? pipeline : pipelineNoCull;
                api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, bucketPipeline);
                context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

                var push = new ModelPushConstants
                {
                    Frame = frameConstants,
                    DrawData = gpuDraws.DrawDataAddress(bucket),
                };
                api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
                    0, (uint)sizeof(ModelPushConstants), &push);
                api.vkCmdDrawIndirectCount(commands, gpuDraws.BucketBuffer(bucket),
                    (ulong)GpuSceneDraws.CommandOffset, gpuDraws.BucketBuffer(bucket), 0,
                    gpuDraws.MaxParts, 16);
                draws++;
            }

            boundPipeline = pipelineNoCull;
        }

        foreach (var actor in actors.All)
        {
            if (!actor.Visible || (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) != 0)
                continue;

            var model = models.Get(actor.Model);
            if (model == null)
                continue;

            // Simple actors were fully recorded in RecordPrepare.
            if (preparedActors.Contains(actor.Id))
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

            var staticCount = 1;
            var world = default(Matrix);
            if (actor.StaticMatrices != null)
                staticCount = actor.StaticMatrices.Length;
            else
                world = BuildWorld(actors.GetWorldMatrix(actor), actor.Scale, camera.Position);

            // Camera-relative distance to the object origin, as MyLodStrategy uses it
            var cameraRelative = actor.StaticMatrices != null
                ? (Vector3)(actor.StaticTranslation - camera.Position)
                : world.Translation;
            var distance = cameraRelative.Length();

            // Local bounds: the instance range's AABB for cube grids, otherwise the model's union bounds
            var localBounds = actor.LocalAabb ?? model.Bounds;
            var worldBounds = actor.StaticMatrices == null ? localBounds.Transform(world) : localBounds;
            var radius = (worldBounds.Max - worldBounds.Min).Length() * 0.5f;

            if ((actor.Flags & RenderFlags.DrawOutsideViewDistance) == 0 &&
                actor.MaxViewDistance < float.MaxValue && distance - radius > actor.MaxViewDistance)
            {
                culled++;
                continue;
            }

            if (actor.StaticMatrices == null && !frustum.Intersects(worldBounds))
            {
                culled++;
                continue;
            }

            var lodModel = SelectLod(model, distance, actor.Scale, camera, loddingSettings, ref actor.Lod);

            // The game calls Render.Draw only for IDs reported visible; this is what makes skinned
            // entities send SetCharacterTransforms (and other per-frame render logic run).
            if (actor.VisibilityUpdates)
                MyRenderProxy.VisibleObjectsWrite.Add(actor.Id);

            var actorPipeline = (actor.Flags & RenderFlags.NoBackFaceCulling) != 0 ? pipelineNoCull : pipeline;
            if (actorPipeline != boundPipeline)
            {
                api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, actorPipeline);
                context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);
                boundPipeline = actorPipeline;
            }

            api.vkCmdBindIndexBuffer(commands, lodModel.Indices.Handle, 0, VkIndexType.Uint32);

            for (var instance = 0; instance < staticCount; instance++)
            {
                var matrix = staticCount == 1 ? world : BuildStaticWorld(actor, camera.Position, instance);
                var colorMask = new Vector4(actor.ColorMaskHsv, 0);

                foreach (var part in lodModel.Parts)
                {
                    if (part.IndexCount == 0)
                        continue;

                    // Screen areas and similar can hide/show a single material through UpdateModelProperties
                    var materialFlags = SceneActors.GetMaterialFlags(actor, part.MaterialName);
                    if ((materialFlags & RenderFlags.Visible) == 0)
                        continue;

                    // The skinning compute pass already applied the entity world matrix; a skinned
                    // model with a skeleton uses the deformed stream for all of its parts (the stock
                    // astronaut's parts are MESH too). Without skeleton data the static stream is
                    // used instead (armor plates).
                    var drawMatrix = matrix;
                    var verticesAddress = lodModel.Vertices.DeviceAddress;
                    if (lodModel.Skinned && actor.SkinnedVertexAddress != 0)
                    {
                        drawMatrix = Matrix.Identity;
                        verticesAddress = actor.SkinnedVertexAddress;
                    }

                    if (part.Technique.IsTransparent())
                    {
                        if (part.Transparent != null)
                        {
                            transparentDraws.Add(new TransparentDraw
                            {
                                Actor = actor,
                                Lod = lodModel,
                                Part = part,
                                World = drawMatrix,
                                Vertices = verticesAddress,
                                Instances = instances,
                                InstanceStart = instanceStart,
                                InstanceCount = instances != null ? instanceCount : 1,
                            });
                        }

                        continue;
                    }

                    if (!IsOpaque(part.Technique))
                        continue;

                    ResolveTextures(actor, part, out var cm, out var ng, out var ext, out var alpha);
                    var material = BuildMaterial(actor, part, lodModel, instances != null, cm, ng, ext, alpha,
                        (materialFlags & RenderFlags.MetalnessColorable) != 0);
                    var materialAllocation = context.Transient.Allocate<DrawMaterial>();
                    materialAllocation.As<DrawMaterial>() = material;

                    // Shader uses dot(position, row), so each float4 is a column of the XNA matrix
                    var worldT = Matrix.Transpose(drawMatrix);
                    var push = new ModelPushConstants
                    {
                        World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                        World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                        World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                        World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                        ColorMaskHsv = colorMask,
                        Frame = frameConstants,
                        Material = materialAllocation.Address,
                        Vertices = verticesAddress,
                        Instances = instances?.Buffer.DeviceAddress ?? 0,
                        InstanceStart = (uint)instanceStart,
                    };

                    api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
                        0, (uint)sizeof(ModelPushConstants), &push);
                    api.vkCmdDrawIndexed(commands, (uint)part.IndexCount, (uint)instanceCount,
                        (uint)part.IndexStart, 0, 0);
                    LastDirectTriangles += (uint)(part.IndexCount / 3 * instanceCount);
                    draws++;
                }
            }
        }

        return draws;
    }

    // Transparent pass into the OIT accumulation/reveal targets. Order-independent, so the CPU no
    // longer sorts; the composite pass resolves it over the opaque color.
    public int RecordTransparent(VkCommandBuffer commands, SceneTargets targets, ulong frameConstants)
    {
        if (transparentDraws.Count == 0)
            return 0;

        var api = context.Device.Api;
        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var draws = 0;
        var boundTransparent = VkPipeline.Null;

        foreach (var draw in transparentDraws)
        {
            var transparent = draw.Part.Transparent;
            var actorPipeline = transparent.TriangleFaceCulling
                ? pipelineTransparent
                : pipelineTransparentNoCull;
            if (actorPipeline != boundTransparent)
            {
                api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, actorPipeline);
                context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);
                boundTransparent = actorPipeline;
            }

            api.vkCmdBindIndexBuffer(commands, draw.Lod.Indices.Handle, 0, VkIndexType.Uint32);

            var colorTexture = draw.Part.TransparentColorTexture;
            var glossTexture = draw.Part.TransparentGlossTexture;
            if (SceneActors.TryGetTextureChange(draw.Actor, draw.Part.MaterialName, out var textureChange))
            {
                if (!string.IsNullOrEmpty(textureChange.ColorMetalFileName))
                    colorTexture = textures.Get(NormalizeAssetPath(textureChange.ColorMetalFileName),
                        waitTillLoaded: false, TextureCache.TextureKind.ColorMetal);
                if (!string.IsNullOrEmpty(textureChange.NormalGlossFileName))
                    glossTexture = textures.Get(NormalizeAssetPath(textureChange.NormalGlossFileName),
                        waitTillLoaded: false, TextureCache.TextureKind.NormalGloss);
            }

            var materialData = new DrawTransparentMaterial
            {
                Color = transparent.Color,
                ColorAdd = transparent.ColorAdd,
                LightMultiplier = transparent.LightMultiplier,
                Params = new Vector4(transparent.Reflectivity, transparent.Fresnel,
                    transparent.GlossTextureAdd, transparent.SpecularColorFactor),
                Params2 = new Vector4(transparent.Gloss, transparent.ReflectionShadow,
                    transparent.AlphaSaturation, 0f),
                ColorTexture = TextureCache.SlotOf(colorTexture),
                GlossTexture = TextureCache.SlotOf(glossTexture),
                Flags = (transparent.AlphaCutout ? 1u : 0u) | (draw.Instances != null ? 2u : 0u),
            };
            var materialAllocation = context.Transient.Allocate<DrawTransparentMaterial>();
            materialAllocation.As<DrawTransparentMaterial>() = materialData;

            var worldT = Matrix.Transpose(draw.World);
            var push = new TransparentPushConstants
            {
                World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                Frame = frameConstants,
                Material = materialAllocation.Address,
                Vertices = draw.Vertices,
                Instances = draw.Instances?.Buffer.DeviceAddress ?? 0,
                InstanceStart = (uint)draw.InstanceStart,
            };

            api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
                0, (uint)sizeof(TransparentPushConstants), &push);
            api.vkCmdDrawIndexed(commands, (uint)draw.Part.IndexCount, (uint)draw.InstanceCount,
                (uint)draw.Part.IndexStart, 0, 0);
            draws++;
        }

        transparentDraws.Clear();
        return draws;
    }

    private static GpuLod SelectLod(GpuModel model, float distance, float scale, CameraState camera,
        MyGlobalLoddingSettings settings, ref int actorLod)
    {
        var normalizedScale = scale > 0f ? scale : 1f;
        var lodDistance = (distance / normalizedScale * camera.LodCoefficient + settings.ObjectDistanceAdd)
                          * settings.ObjectDistanceMult;

        GpuLod lod;
        if (settings.EnableLodSelection)
        {
            actorLod = Math.Clamp(settings.LodSelection, 0, model.LodCount - 1);
            lod = model.Lods[actorLod];
        }
        else if (settings.IsUpdateEnabled)
        {
            lod = model.SelectLod(lodDistance, ref actorLod, settings.HisteresisRatio);
        }
        else
        {
            actorLod = Math.Clamp(actorLod, 0, model.LodCount - 1);
            lod = model.Lods[actorLod];
        }

        return lod;
    }

    private static Matrix BuildWorld(MatrixD worldD, float scale, Vector3D cameraPosition)
    {
        var world = (Matrix)worldD;
        world.Translation = (Vector3)(worldD.Translation - cameraPosition);

        if (scale != 1f)
        {
            world.M11 *= scale;
            world.M12 *= scale;
            world.M13 *= scale;
            world.M21 *= scale;
            world.M22 *= scale;
            world.M23 *= scale;
            world.M31 *= scale;
            world.M32 *= scale;
            world.M33 *= scale;
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

    // Texture slots including ChangeMaterialTexture replacements (cockpit/LCD screens).
    private void ResolveTextures(SceneActor actor, ModelPart part,
        out TextureCache.Entry colorMetal, out TextureCache.Entry normalGloss,
        out TextureCache.Entry extensions, out TextureCache.Entry alphamask)
    {
        colorMetal = part.ColorMetal;
        normalGloss = part.NormalGloss;
        extensions = part.Extensions;
        alphamask = part.Alphamask;

        if (!SceneActors.TryGetTextureChange(actor, part.MaterialName, out var change))
            return;


        if (!string.IsNullOrEmpty(change.ColorMetalFileName))
            colorMetal = textures.Get(NormalizeAssetPath(change.ColorMetalFileName), waitTillLoaded: false,
                TextureCache.TextureKind.ColorMetal);
        if (!string.IsNullOrEmpty(change.NormalGlossFileName))
            normalGloss = textures.Get(NormalizeAssetPath(change.NormalGlossFileName), waitTillLoaded: false,
                TextureCache.TextureKind.NormalGloss);
        if (!string.IsNullOrEmpty(change.ExtensionsFileName))
            extensions = textures.Get(NormalizeAssetPath(change.ExtensionsFileName), waitTillLoaded: false,
                TextureCache.TextureKind.Extensions);
        if (!string.IsNullOrEmpty(change.AlphamaskFileName))
            alphamask = textures.Get(NormalizeAssetPath(change.AlphamaskFileName), waitTillLoaded: false,
                TextureCache.TextureKind.Alphamask);
    }

    private static string NormalizeAssetPath(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static DrawMaterial BuildMaterial(SceneActor actor, ModelPart part, GpuLod lod, bool instanced,
        TextureCache.Entry colorMetal, TextureCache.Entry normalGloss,
        TextureCache.Entry extensions, TextureCache.Entry alphamask, bool metalnessColorable)
    {
        var flags = 0u;
        if (colorMetal != null) flags |= DrawMaterial.HasColorMetal;
        if (normalGloss != null) flags |= DrawMaterial.HasNormalGloss;
        if (extensions != null) flags |= DrawMaterial.HasExtensions;
        if (alphamask != null) flags |= DrawMaterial.HasAlphamask;
        if (lod.HasTangents) flags |= DrawMaterial.HasTangent;
        if (part.Technique is MyMeshDrawTechnique.ALPHA_MASKED
            or MyMeshDrawTechnique.ALPHA_MASKED_SINGLE_SIDED
            or MyMeshDrawTechnique.DECAL_CUTOUT)
            flags |= DrawMaterial.AlphaMasked;
        if (part.Technique == MyMeshDrawTechnique.DECAL_CUTOUT)
            flags |= DrawMaterial.Decal;
        if (instanced)
            flags |= DrawMaterial.Instanced;
        if (metalnessColorable)
            flags |= DrawMaterial.MetalnessColorable;
        // LCD/offscreen targets are written without an alpha channel we control (their alpha is the
        // panel background alpha). Never use that as metalness; stock screen materials are non-metal
        // and rely on their emissive add map, which is what keeps the display readable in shadow.
        if (colorMetal is { IsGenerated: true })
            flags |= DrawMaterial.Screen;

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
            ColorMetal = TextureCache.SlotOf(colorMetal),
            NormalGloss = TextureCache.SlotOf(normalGloss),
            Extensions = TextureCache.SlotOf(extensions),
            Alphamask = TextureCache.SlotOf(alphamask),
            Flags = flags,
            DepthBias = actor.DepthBias,
        };
    }

    private static bool IsOpaque(MyMeshDrawTechnique technique) =>
        technique is MyMeshDrawTechnique.MESH
            or MyMeshDrawTechnique.ALPHA_MASKED
            or MyMeshDrawTechnique.ALPHA_MASKED_SINGLE_SIDED
            or MyMeshDrawTechnique.DECAL_CUTOUT
            or MyMeshDrawTechnique.MESH_INSTANCED
            or MyMeshDrawTechnique.MESH_INSTANCED_GENERIC;

    public void Dispose()
    {
        var api = context.Device.Api;
        gpuDraws.Dispose();
        var cullHandle = pipeline;
        var noCullHandle = pipelineNoCull;
        var transparentCull = pipelineTransparent;
        var transparentNoCull = pipelineTransparentNoCull;
        var skinning = skinningPipeline;
        var palette = palettePipeline;
        context.Release(() =>
        {
            api.vkDestroyPipeline(cullHandle, null);
            api.vkDestroyPipeline(noCullHandle, null);
            api.vkDestroyPipeline(transparentCull, null);
            api.vkDestroyPipeline(transparentNoCull, null);
            api.vkDestroyPipeline(skinning, null);
            api.vkDestroyPipeline(palette, null);
        });
    }
}
