using System;
using System.Collections.Generic;
using System.Diagnostics;
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

// Must match Shaders/Scene/Model.slang (main and shadow entry points)
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
        public bool HasTransparent;
        public int PartStart;
        public int PartCount;
        public bool Dead;
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
    private readonly bool shadowsEnabled;
    private readonly ShadowMap[] shadowMaps;
    private readonly VkPipeline shadowPipeline;
    private readonly VkPipeline shadowPipelineNoCull;
    private readonly Matrix[] shadowViewProjections = new Matrix[ShadowMap.Count];
    private readonly List<SceneActor> directActors = new();
    private readonly List<TransparentDraw> transparentDraws = new();

    private readonly List<CachedActor> cachedActors = new();
    private readonly List<GpuScenePart> cachedParts = new();
    private readonly List<GpuLodRange> cachedRanges = new();
    private readonly List<CachedActor> transparentActors = new();
    private readonly Dictionary<uint, List<int>> cachedByActor = new();
    private GpuSceneActor[] actorStates = [];
    private Matrix[] actorWorlds = [];
    private long instanceRevisionSeen = -1;
    private long materialTextureGeneration;
    private long allMaterialTargetGeneration;
    private bool allMaterialRefresh;
    private int allMaterialRefreshIndex;
    private readonly GpuBuffer[] partBuffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly GpuBuffer[] rangeBuffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly GpuBuffer[] lodStateBuffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly long[] slotCacheVersion = new long[FrameLoop.FramesInFlight];
    private readonly long[] slotPartsVersion = new long[FrameLoop.FramesInFlight];
    private long cacheVersion;
    private long partsVersion;
    private bool forceCacheRebuild;
    private long cachedFullRevision = -1;
    private int lastTriedModelVersion = -1;
    private readonly List<SceneActor> deferredActors = new();

    // Full rebuilds are only needed for removals/model/skeleton changes. New actors are appended
    // incrementally; the deferred timer prevents a rebuild storm while a grid is still streaming.
    private long pendingFullRevision = long.MinValue;
    private long pendingSince;
    private long lastRebuild;
    private const double MaxDeferSeconds = 1.5;
    private const double MinRebuildIntervalSeconds = 0.75;
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
        Array.Fill(slotCacheVersion, -1L);
        Array.Fill(slotPartsVersion, -1L);
        this.context = context;
        this.models = models;
        this.instanceBuffers = instanceBuffers;
        this.textures = textures;
        gpuDraws = new GpuSceneDraws(context);

        // Opt-in from the settings dialog (restart required). VULKAN_SHADOWS explicitly overrides it
        // for automated tests: "1" force on, "0" force off, unset use the settings dialog.
        // The lighting model changes (stock colourisation/IBL) are independent and always active.
        var shadowOverride = Environment.GetEnvironmentVariable("VULKAN_SHADOWS");
        shadowsEnabled = shadowOverride == "1"
                         || (shadowOverride != "0" && ClientPlugin.Config.Current.Shadows);
        if (shadowsEnabled)
        {
            shadowMaps = new ShadowMap[ShadowMap.Count];
            for (var i = 0; i < shadowMaps.Length; i++)
            {
                shadowMaps[i] = new ShadowMap(context, ShadowMap.Extents[i]);
                shadowMaps[i].Ensure();
            }

            GraphicsPipelineBuilder ShadowBuilder(VkCullModeFlags cullMode) => new()
            {
                Code = ShaderBytecode.Model.Code.ToArray(),
                VertexEntry = ShaderBytecode.Model.ShadowMain,
                FragmentEntry = ShaderBytecode.Model.ShadowFragment,
                ColorFormats = [],
                DepthFormat = SceneTargets.DepthFormat,
                DepthTest = true,
                DepthWrite = true,
                DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
                CullMode = cullMode,
                FrontFace = VkFrontFace.Clockwise,
            };
            shadowPipeline = ShadowBuilder(VkCullModeFlags.Back).Build(context, "shadow");
            shadowPipelineNoCull = ShadowBuilder(VkCullModeFlags.None).Build(context, "shadow-nocull");
        }

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
        var profile = Stopwatch.StartNew();
        var api = context.Device.Api;
        var settings = lodding.Global;
        var any = false;

        foreach (var actor in actors.Skinned)
            actor.SkinnedVertexAddress = 0;

        foreach (var actor in actors.Skinned)
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
                // stream through the GPU path.
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
        }

        if (!any)
        {
            LastSkinningMs = profile.Elapsed.TotalMilliseconds;
            return;
        }

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
        LastSkinningMs = profile.Elapsed.TotalMilliseconds;
    }

    public int GpuPartCount => cachedPartCount;
    public static int ShadowCascadeCount => ShadowMap.Count;
    public static float[] ShadowSplitEnds => ShadowMap.SplitEnds;
    public uint ShadowSlot => shadowMaps?[0].Slot ?? 0;
    public uint ShadowSlotAt(int cascade) => shadowMaps?[cascade].Slot ?? 0;
    public Matrix ShadowViewProjectionAt(int cascade) => shadowViewProjections[cascade];
    public float ShadowExtentAt(int cascade) => ShadowMap.Extents[cascade];
    public bool ShadowsReady
    {
        get
        {
            if (!shadowsEnabled || shadowMaps == null)
                return false;
            foreach (var map in shadowMaps)
            {
                if (!map.Ready)
                    return false;
            }
            return true;
        }
    }

    // True only when the depth map was actually recorded this frame. The frame constants must not
    // enable shadow sampling on frames where the map is still in an undefined/unwritten state
    // (for example before the first opaque actor exists).
    public bool ShadowPassRecorded { get; private set; }

    public GpuDiagnosticsCounters Diagnostics => gpuDraws.Counters;

    // CPU profiling for the diagnostics log.
    public double LastSkinningMs { get; private set; }
    public double LastPrepareMs { get; private set; }
    public double LastEnsureMs { get; private set; }
    public double LastActorLoopMs { get; private set; }
    public double LastRecordMs { get; private set; }
    public double LastTransparentMs { get; private set; }
    public uint LastDirectTriangles { get; private set; }

    // Rebuilds the persistent opaque parts when actors/models/materials change. The per-frame path then
    // uploads only actor world/colour/visibility state; the GPU selects the LOD and appends part draws.
    private void EnsureCache(SceneActors actors)
    {
        var forced = forceCacheRebuild;
        forceCacheRebuild = false;

        var fullRevision = actors.FullRevision;
        if (!forced && fullRevision == cachedFullRevision)
        {
            ProcessRemovedActors(actors);
            ProcessRecreatedActors(actors);
            ProcessCreatedActors(actors);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!forced && fullRevision != pendingFullRevision)
        {
            if (pendingSince == 0)
                pendingSince = now;
            pendingFullRevision = fullRevision;

            var elapsed = (now - pendingSince) / (double)Stopwatch.Frequency;
            if (elapsed < MaxDeferSeconds)
                return;
        }
        else if (!forced)
        {
            // Wait for model builds to settle and avoid rebuilding on every streamed removal.
            if (models.HasPendingUploads)
                return;

            var sinceRebuild = (now - lastRebuild) / (double)Stopwatch.Frequency;
            if (sinceRebuild < MinRebuildIntervalSeconds)
                return;
        }

        pendingSince = 0;
        lastRebuild = now;
        cachedFullRevision = fullRevision;
        var rebuildWatch = Stopwatch.StartNew();
        cachedActors.Clear();
        cachedParts.Clear();
        cachedRanges.Clear();
        directActors.Clear();
        transparentActors.Clear();
        cachedByActor.Clear();
        deferredActors.Clear();
        lastTriedModelVersion = models.Version;

        foreach (var actor in actors.All)
        {
            if (AddActorToCache(actors, actor))
                continue;

            if (!string.IsNullOrEmpty(actor.Model) && models.Get(actor.Model) == null &&
                !deferredActors.Contains(actor))
            {
                deferredActors.Add(actor);
            }
        }

        cachedPartCount = cachedParts.Count;
        cacheVersion++;
        partsVersion++;
        instanceRevisionSeen = instanceBuffers.Revision;
        actors.ClearDirty();
        actors.ClearMaterialDirty();
        actors.ClearCreatedActors();
        actors.ClearRemovedActors();
        actors.ClearRecreatedActors();

        if (rebuildWatch.Elapsed.TotalMilliseconds > 50)
            RenderLog.Warning($"Part cache rebuild took {rebuildWatch.Elapsed.TotalMilliseconds:F1} ms " +
                              $"({cachedActors.Count} actors, {cachedPartCount} parts; {actors.RevisionCounters})");
    }

    private GpuScenePart BuildGpuPart(SceneActor actor, ModelPart part, GpuLod lod, bool instanced,
        RenderFlags materialFlags, bool visible)
    {
        ResolveTextures(actor, part, out var cm, out var ng, out var ext, out var alpha);

        var material = BuildMaterial(actor, part, lod, instanced, cm, ng, ext, alpha,
            (materialFlags & RenderFlags.MetalnessColorable) != 0);

        return new GpuScenePart
        {
            Material = material,
            Vertices = lod.Vertices.DeviceAddress,
            Indices = lod.Indices.DeviceAddress,
            IndexStart = (uint)part.IndexStart,
            // Hidden parts keep their slot (stable part indices) but draw nothing.
            IndexCount = visible ? (uint)part.IndexCount : 0u,
        };
    }

    // Rebuilds one actor's cached part/materials in place. Part count/order must stay unchanged;
    // otherwise the cache is rebuilt structurally on the next frame.
    private void RefreshActorMaterial(CachedActor cached)
    {
        if (cached.Dead)
            return;

        var actor = cached.Actor;
        var model = cached.Model;
        var partIndex = cached.PartStart;
        var end = cached.PartStart + cached.PartCount;
        var instanced = actor.HasInstances;

        foreach (var list in cached.TransparentByLod)
            list.Clear();

        for (var lodIndex = 0; lodIndex < model.LodCount; lodIndex++)
        {
            var lod = model.Lods[lodIndex];
            var transparent = cached.TransparentByLod[lodIndex];
            foreach (var part in lod.Parts)
            {
                if (part.IndexCount == 0)
                    continue;

                var materialFlags = SceneActors.GetMaterialFlags(actor, part.MaterialName);
                var visible = (materialFlags & RenderFlags.Visible) != 0;

                if (part.Technique.IsTransparent())
                {
                    if (visible && part.Transparent != null)
                        transparent.Add(new CachedTransparentPart { Lod = lod, Part = part });
                    continue;
                }

                if (!IsOpaque(part.Technique))
                    continue;

                if (partIndex >= end)
                {
                    forceCacheRebuild = true;
                    return;
                }

                cachedParts[partIndex++] = BuildGpuPart(actor, part, lod, instanced, materialFlags, visible);
            }
        }

        if (partIndex != end)
        {
            forceCacheRebuild = true;
            return;
        }

        var hasTransparent = false;
        foreach (var list in cached.TransparentByLod)
        {
            if (list.Count > 0)
            {
                hasTransparent = true;
                break;
            }
        }

        if (hasTransparent != cached.HasTransparent)
        {
            cached.HasTransparent = hasTransparent;
            if (hasTransparent)
            {
                if (!transparentActors.Contains(cached))
                    transparentActors.Add(cached);
            }
            else
            {
                transparentActors.Remove(cached);
            }
        }

        partsVersion++;
    }

    private void UpdateDirtyMaterials(SceneActors actors)
    {
        if (cachedActors.Count == 0)
        {
            actors.ClearMaterialDirty();
            return;
        }

        var watch = Stopwatch.StartNew();

        // Textures finish decoding asynchronously. Part records store bindless slot indices, so a
        // part built while a texture was still loading keeps the placeholder forever unless the
        // material is rebuilt. Refresh all cached materials (budgeted) when new textures appear.
        if (!allMaterialRefresh && textures.ReadyGeneration != materialTextureGeneration)
        {
            allMaterialRefresh = true;
            allMaterialRefreshIndex = 0;
            allMaterialTargetGeneration = textures.ReadyGeneration;
        }

        if (allMaterialRefresh)
        {
            while (allMaterialRefreshIndex < cachedActors.Count &&
                   watch.Elapsed.TotalMilliseconds < 2.0)
            {
                RefreshActorMaterial(cachedActors[allMaterialRefreshIndex++]);
            }

            if (allMaterialRefreshIndex >= cachedActors.Count)
            {
                allMaterialRefresh = false;
                allMaterialRefreshIndex = 0;
                materialTextureGeneration = allMaterialTargetGeneration;
            }
        }

        var processed = 0;
        foreach (var actor in actors.MaterialDirtyActors)
        {
            if (forceCacheRebuild || watch.Elapsed.TotalMilliseconds >= 2.0)
                break;

            processed++;
            if (!cachedByActor.TryGetValue(actor.Id, out var indices))
                continue;

            foreach (var index in indices)
                RefreshActorMaterial(cachedActors[index]);
        }

        actors.TrimMaterialDirty(processed);
    }

    // Marks an actor's cached records invisible without touching the part buffer. The parts stay
    // until a compaction pass; this makes removals and model replacements O(1).
    private void KillCachedActor(SceneActor actor)
    {
        if (!cachedByActor.TryGetValue(actor.Id, out var indices))
            return;

        foreach (var index in indices)
        {
            actorStates[index].Flags &= ~GpuSceneActor.Visible;
            cachedActors[index].Dead = true;
            transparentActors.Remove(cachedActors[index]);
        }

        cachedByActor.Remove(actor.Id);
    }

    private void ProcessRemovedActors(SceneActors actors)
    {
        var removed = actors.RemovedActorIds;
        if (removed.Count == 0)
            return;

        var watch = Stopwatch.StartNew();
        var processed = 0;
        foreach (var id in removed)
        {
            if (watch.Elapsed.TotalMilliseconds >= 2.0)
                break;

            processed++;
            if (cachedByActor.TryGetValue(id, out var indices))
            {
                foreach (var index in indices)
                {
                    actorStates[index].Flags &= ~GpuSceneActor.Visible;
                    cachedActors[index].Dead = true;
                    transparentActors.Remove(cachedActors[index]);
                }
                cachedByActor.Remove(id);
            }

            directActors.RemoveAll(a => a.Id == id);
            deferredActors.RemoveAll(a => a.Id == id);
        }

        actors.TrimRemovedActors(processed);
    }

    private void ProcessRecreatedActors(SceneActors actors)
    {
        var recreated = actors.RecreatedActors;
        if (recreated.Count == 0)
            return;

        var watch = Stopwatch.StartNew();
        var processed = 0;
        var changed = false;
        foreach (var actor in recreated)
        {
            if (watch.Elapsed.TotalMilliseconds >= 3.0)
                break;

            processed++;
            KillCachedActor(actor);
            if (AddActorToCache(actors, actor))
            {
                changed = true;
            }
            else if (!string.IsNullOrEmpty(actor.Model) && models.Get(actor.Model) == null &&
                     !deferredActors.Contains(actor))
            {
                // The replacement model is still loading; retry when the model version changes.
                deferredActors.Add(actor);
            }
        }

        actors.TrimRecreatedActors(processed);
        if (changed)
        {
            cachedPartCount = cachedParts.Count;
            cacheVersion++;
            partsVersion++;
        }
    }

    // Uploads the persistent part/range/LOD-state data to the current frame slot's buffers. Only
    // the slot whose fence was waited on is written, so the other in-flight slot is untouched.
    private void UploadPartData(int frameSlot)
    {
        if (cachedPartCount == 0)
            return;

        var partsStale = slotPartsVersion[frameSlot] != partsVersion;
        var cacheStale = slotCacheVersion[frameSlot] != cacheVersion;
        if (!partsStale && !cacheStale)
            return;

        var partBytes = (ulong)(cachedPartCount * GpuScenePart.Size);
        if (partBuffers[frameSlot] == null || partBuffers[frameSlot].Size < partBytes)
        {
            partBuffers[frameSlot]?.Dispose();
            partBuffers[frameSlot] = GpuBuffer.Create(context, partBytes,
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
        }

        CollectionsMarshal.AsSpan(cachedParts).CopyTo(
            new Span<GpuScenePart>(partBuffers[frameSlot].Mapped, cachedPartCount));

        if (cacheStale)
        {
            var rangeBytes = (ulong)(cachedRanges.Count * sizeof(GpuLodRange));
            if (rangeBuffers[frameSlot] == null || rangeBuffers[frameSlot].Size < rangeBytes)
            {
                rangeBuffers[frameSlot]?.Dispose();
                rangeBuffers[frameSlot] = GpuBuffer.Create(context, rangeBytes,
                    VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            }
            CollectionsMarshal.AsSpan(cachedRanges).CopyTo(
                new Span<GpuLodRange>(rangeBuffers[frameSlot].Mapped, cachedRanges.Count));

            var lodBytes = (ulong)(cachedActors.Count * sizeof(uint));
            if (lodStateBuffers[frameSlot] == null || lodStateBuffers[frameSlot].Size < lodBytes)
            {
                lodStateBuffers[frameSlot]?.Dispose();
                lodStateBuffers[frameSlot] = GpuBuffer.Create(context, lodBytes,
                    VkBufferUsageFlags.StorageBuffer, hostVisible: true);
            }

            var states = new Span<uint>(lodStateBuffers[frameSlot].Mapped, cachedActors.Count);
            for (var i = 0; i < cachedActors.Count; i++)
                states[i] = (uint)Math.Clamp(cachedActors[i].Actor.Lod, 0, cachedActors[i].Model.LodCount - 1);

            slotCacheVersion[frameSlot] = cacheVersion;
        }

        slotPartsVersion[frameSlot] = partsVersion;
    }

    private void EnsureActorStateCapacity(int count)
    {
        if (actorStates.Length >= count)
            return;

        var capacity = Math.Max(count, Math.Max(1024, actorStates.Length * 2));
        Array.Resize(ref actorStates, capacity);
        Array.Resize(ref actorWorlds, capacity);
    }

    private void AddCachedActor(SceneActors actors, SceneActor actor, GpuModel model, uint rangeBase,
        int staticIndex, List<CachedTransparentPart>[] transparentByLod, bool hasTransparent,
        int partStart, int partCount)
    {
        var index = cachedActors.Count;
        var cached = new CachedActor
        {
            Actor = actor,
            Model = model,
            RangeBase = rangeBase,
            LodStateIndex = (uint)index,
            StaticIndex = staticIndex,
            TransparentByLod = transparentByLod,
            HasTransparent = hasTransparent,
            PartStart = partStart,
            PartCount = partCount,
        };

        cachedActors.Add(cached);
        EnsureActorStateCapacity(index + 1);
        BuildActorState(actors, cached, index);

        if (hasTransparent)
            transparentActors.Add(cached);

        if (!cachedByActor.TryGetValue(actor.Id, out var indices))
            cachedByActor[actor.Id] = indices = new List<int>(1);
        indices.Add(index);
    }

    // Appends one actor to the persistent cache. Returns false for skinned/not-ready actors, which
    // stay on the direct/deferred paths.
    private bool AddActorToCache(SceneActors actors, SceneActor actor)
    {
        if (cachedByActor.ContainsKey(actor.Id))
            return false;

        if (string.IsNullOrEmpty(actor.Model))
        {
            if (!directActors.Contains(actor))
                directActors.Add(actor);
            return false;
        }

        var model = models.Get(actor.Model);
        if (model == null || model.LodCount == 0)
        {
            if (!directActors.Contains(actor))
                directActors.Add(actor);
            return false;
        }

        var skinned = false;
        foreach (var lod in model.Lods)
            skinned |= lod.Skinned;
        var hasSkeleton = actor.SkinTransforms != null && actor.SkeletonIndexUInts != null;
        if (skinned && hasSkeleton)
        {
            if (!directActors.Contains(actor))
                directActors.Add(actor);
            return false;
        }

        var rangeBase = (uint)cachedRanges.Count;
        var partStart = cachedParts.Count;
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
                var visible = (materialFlags & RenderFlags.Visible) != 0;

                if (part.Technique.IsTransparent())
                {
                    if (visible && part.Transparent != null)
                        transparent.Add(new CachedTransparentPart { Lod = lod, Part = part });
                    continue;
                }

                if (!IsOpaque(part.Technique))
                    continue;

                cachedParts.Add(BuildGpuPart(actor, part, lod, actor.HasInstances, materialFlags, visible));
            }

            cachedRanges.Add(new GpuLodRange
            {
                Start = (uint)firstPart,
                Count = (uint)(cachedParts.Count - firstPart),
            });
        }

        var hasTransparent = false;
        foreach (var list in transparentByLod)
        {
            if (list.Count > 0)
            {
                hasTransparent = true;
                break;
            }
        }

        var partCount = cachedParts.Count - partStart;
        var staticCount = actor.StaticMatrices?.Length ?? 0;
        if (staticCount > 0)
        {
            for (var staticIndex = 0; staticIndex < staticCount; staticIndex++)
            {
                AddCachedActor(actors, actor, model, rangeBase, staticIndex, transparentByLod,
                    hasTransparent, partStart, partCount);
            }
        }
        else
        {
            AddCachedActor(actors, actor, model, rangeBase, -1, transparentByLod,
                hasTransparent, partStart, partCount);
        }

        directActors.Remove(actor);
        return true;
    }

    private void ProcessCreatedActors(SceneActors actors)
    {
        var created = actors.CreatedActors;
        var modelsChanged = lastTriedModelVersion != models.Version;
        if (created.Count == 0 && deferredActors.Count == 0 && !modelsChanged)
            return;

        var changed = false;
        var watch = Stopwatch.StartNew();
        var processed = 0;
        for (var i = 0; i < created.Count; i++)
        {
            if (watch.Elapsed.TotalMilliseconds >= 4.0)
                break;

            var actor = created[i];
            processed++;
            if (cachedByActor.ContainsKey(actor.Id))
                continue;

            if (string.IsNullOrEmpty(actor.Model))
            {
                if (!directActors.Contains(actor))
                    directActors.Add(actor);
                continue;
            }

            if (AddActorToCache(actors, actor))
            {
                changed = true;
            }
            else if (models.Get(actor.Model) == null && !deferredActors.Contains(actor))
            {
                deferredActors.Add(actor);
            }
        }
        actors.TrimCreatedActors(processed);

        // Deferred models can become ready at any time; retry under the same budget.
        if (watch.Elapsed.TotalMilliseconds < 4.0)
        {
            for (var i = deferredActors.Count - 1; i >= 0 && watch.Elapsed.TotalMilliseconds < 4.0; i--)
            {
                var actor = deferredActors[i];
                if (models.Get(actor.Model) == null)
                    continue;

                if (AddActorToCache(actors, actor))
                {
                    deferredActors.RemoveAt(i);
                    changed = true;
                }
            }
        }
        lastTriedModelVersion = models.Version;

        if (changed)
        {
            cachedPartCount = cachedParts.Count;
            cacheVersion++;
            partsVersion++;
        }
    }

    // Absolute (world-space) actor state. The culling compute subtracts the camera position, so
    // camera movement does not invalidate any record.
    private void BuildActorState(SceneActors actors, CachedActor cached, int index)
    {
        var actor = cached.Actor;
        var model = cached.Model;

        Matrix world;
        if (cached.StaticIndex >= 0)
        {
            world = actor.StaticMatrices[cached.StaticIndex];
            world.Translation += (Vector3)actor.StaticTranslation;
        }
        else
        {
            world = BuildAbsoluteWorld(actors.GetWorldMatrix(actor), actor.Scale);
        }
        actorWorlds[index] = world;

        var worldT = Matrix.Transpose(world);
        var localBounds = actor.LocalAabb ?? model.Bounds;
        var worldBounds = localBounds.Transform(world);
        var center = (worldBounds.Min + worldBounds.Max) * 0.5f;
        var radius = (worldBounds.Max - worldBounds.Min).Length() * 0.5f;

        var visible = actor.Visible && (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) == 0;
        var instances = (InstanceBuffer)null;
        var instanceStart = 0;
        var instanceCount = 1;
        var instanceAddress = 0ul;
        if (actor.HasInstances)
        {
            instances = instanceBuffers.Get(actor.InstanceBufferId);
            if (instances?.Buffer == null || instances.Type != MyRenderInstanceBufferType.Cube)
            {
                visible = false;
            }
            else
            {
                instanceStart = actor.InstanceStart;
                instanceCount = Math.Min(actor.InstanceCount, instances.Count - instanceStart);
                if (instanceCount <= 0)
                    visible = false;
                else
                    instanceAddress = instances.Buffer.DeviceAddress;
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

        var flags = (actor.Flags & RenderFlags.NoBackFaceCulling) != 0 ? GpuSceneActor.NoBackFaceCulling : 0u;
        if ((actor.Flags & RenderFlags.DrawOutsideViewDistance) != 0)
            flags |= GpuSceneActor.DrawOutsideViewDistance;
        if (visible)
            flags |= GpuSceneActor.Visible;

        actorStates[index] = new GpuSceneActor
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
            Instances = instanceAddress,
            InstanceStart = (uint)instanceStart,
            InstanceCount = (uint)instanceCount,
            Flags = flags,
            RangeBase = cached.RangeBase,
            LodStateIndex = cached.LodStateIndex,
            LodCount = (uint)lodCount,
            MaxViewDistance = (actor.Flags & RenderFlags.DrawOutsideViewDistance) != 0
                ? float.MaxValue
                : actor.MaxViewDistance,
            Pad0 = 0,
        };
    }

    // Refreshes only records the game changed plus actors whose instance buffer was replaced.
    private void UpdateDirtyActorStates(SceneActors actors)
    {
        if (cachedActors.Count == 0)
        {
            actors.ClearDirty();
            return;
        }

        if (instanceRevisionSeen != instanceBuffers.Revision)
        {
            instanceRevisionSeen = instanceBuffers.Revision;
            for (var i = 0; i < cachedActors.Count; i++)
            {
                if (!cachedActors[i].Dead && cachedActors[i].Actor.HasInstances)
                    BuildActorState(actors, cachedActors[i], i);
            }
        }

        var watch = Stopwatch.StartNew();
        var processed = 0;
        foreach (var actor in actors.DirtyActors)
        {
            if (watch.Elapsed.TotalMilliseconds >= 2.0)
                break;

            processed++;
            if (!cachedByActor.TryGetValue(actor.Id, out var indices))
                continue;

            foreach (var index in indices)
                BuildActorState(actors, cachedActors[index], index);
        }

        actors.TrimDirty(processed);
    }

    // Builds per-frame actor state and dispatches the GPU culling/LOD compute. Parts/materials are persistent.
    public void RecordPrepare(VkCommandBuffer commands, int frameSlot, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, Vector3D sunDirection)
    {
        ShadowPassRecorded = false;
        var profile = Stopwatch.StartNew();
        EnsureCache(actors);
        LastEnsureMs = profile.Elapsed.TotalMilliseconds;
        if (shadowsEnabled)
        {
            var cameraForward = -new Vector3(camera.ViewAt0.M31, camera.ViewAt0.M32, camera.ViewAt0.M33);
            if (cameraForward.LengthSquared() < 1e-8f)
                cameraForward = -Vector3.Forward;
            cameraForward.Normalize();
            for (var cascade = 0; cascade < ShadowMap.Count; cascade++)
            {
                var near = cascade == 0 ? 0f : ShadowMap.SplitEnds[cascade - 1];
                var far = ShadowMap.SplitEnds[cascade];
                var center = cameraForward * ((near + far) * 0.5f);
                shadowViewProjections[cascade] =
                    ShadowMap.BuildViewProjection(sunDirection, center, ShadowMap.Extents[cascade]);
            }
        }
        var loopStart = profile.Elapsed;
        UpdateDirtyActorStates(actors);
        UpdateDirtyMaterials(actors);
        if (forceCacheRebuild)
        {
            EnsureCache(actors);
            UpdateDirtyActorStates(actors);
        }

        var settings = lodding.Global;
        var frustum = new Frustum(camera.CullingViewProjection);
        transparentDraws.Clear();
        gpuSubmitted = false;
        gpuDraws.Begin(frameSlot, Math.Max(cachedPartCount, 1));

        // The game calls Render.Draw only for IDs reported visible; publish them once per frame.
        foreach (var actor in actors.VisibilityUpdates)
        {
            if (actor.Visible && (actor.Flags & (RenderFlags.SkipInMainView | RenderFlags.SkipInForward)) == 0)
                MyRenderProxy.VisibleObjectsWrite.Add(actor.Id);
        }

        // Transparent parts keep the direct path and still need a CPU LOD/cull selection.
        foreach (var cached in transparentActors)
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

            var world = actorWorlds[cached.LodStateIndex];
            world.Translation -= (Vector3)camera.Position;
            var distance = world.Translation.Length();
            var localBounds = actor.LocalAabb ?? model.Bounds;
            var worldBounds = localBounds.Transform(world);
            var center = (worldBounds.Min + worldBounds.Max) * 0.5f;
            var radius = (worldBounds.Max - worldBounds.Min).Length() * 0.5f;
            var maxViewDistance = (actor.Flags & RenderFlags.DrawOutsideViewDistance) != 0
                ? float.MaxValue
                : actor.MaxViewDistance;
            var cpuVisible = distance - radius <= maxViewDistance && frustum.Intersects(worldBounds);

            var lodModel = SelectLod(model, distance, actor.Scale, camera, settings, ref actor.Lod);
            if (!cpuVisible)
                continue;

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

        LastActorLoopMs = (profile.Elapsed - loopStart).TotalMilliseconds;
        UploadPartData(frameSlot);
        LastPrepareMs = profile.Elapsed.TotalMilliseconds;

        if (cachedActors.Count == 0 || cachedPartCount == 0 || partBuffers[frameSlot] == null ||
            rangeBuffers[frameSlot] == null || lodStateBuffers[frameSlot] == null)
            return;

        var actorCount = cachedActors.Count;
        var allocation = context.Transient.Allocate<GpuSceneActor>(actorCount);
        actorStates.AsSpan(0, actorCount).CopyTo(allocation.AsSpan<GpuSceneActor>(actorCount));

        gpuDraws.Dispatch(commands, camera.CullingViewProjection, camera.Position, camera.LodCoefficient,
            allocation.Address, (uint)actorCount, partBuffers[frameSlot].DeviceAddress,
            rangeBuffers[frameSlot].DeviceAddress, lodStateBuffers[frameSlot].DeviceAddress, lodding);
        gpuSubmitted = true;
    }

    // Records the sun depth map. Reuses the opaque pass draw data (same culled set, LOD0 caster
    // bucket), so it costs one extra vertex-only pass per cascade and no CPU-side draw loop.
    public unsafe void RecordShadow(VkCommandBuffer commands, CameraState camera)
    {
        if (!shadowsEnabled || !gpuSubmitted || !ShadowsReady)
            return;

        var api = context.Device.Api;
        var viewport = new VkViewport(0, 0, ShadowMap.Size, ShadowMap.Size, 0, 1);
        var scissor = new VkRect2D(0, 0, ShadowMap.Size, ShadowMap.Size);

        for (var cascade = 0; cascade < shadowMaps.Length; cascade++)
        {
            var map = shadowMaps[cascade];
            var image = map.Image;
            var firstUse = map.Layout == VkImageLayout.Undefined;
            var toAttachment = new VkImageMemoryBarrier2
            {
                srcStageMask = firstUse ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.FragmentShader,
                srcAccessMask = firstUse ? VkAccessFlags2.None : VkAccessFlags2.ShaderSampledRead,
                dstStageMask = VkPipelineStageFlags2.EarlyFragmentTests,
                dstAccessMask = VkAccessFlags2.DepthStencilAttachmentWrite,
                oldLayout = map.Layout,
                newLayout = VkImageLayout.DepthAttachmentOptimal,
                image = image.Handle,
                subresourceRange = image.FullRange,
                srcQueueFamilyIndex = uint.MaxValue,
                dstQueueFamilyIndex = uint.MaxValue,
            };
            var toAttachmentInfo = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &toAttachment };
            api.vkCmdPipelineBarrier2(commands, &toAttachmentInfo);

            var depthAttachment = new VkRenderingAttachmentInfo
            {
                imageView = image.View,
                imageLayout = VkImageLayout.DepthAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(0f, 0) },
            };
            var renderingInfo = new VkRenderingInfo
            {
                renderArea = new VkRect2D(0, 0, ShadowMap.Size, ShadowMap.Size),
                layerCount = 1,
                colorAttachmentCount = 0,
                pDepthAttachment = &depthAttachment,
            };
            api.vkCmdBeginRendering(commands, &renderingInfo);
            api.vkCmdSetViewport(commands, 0, 1, &viewport);
            api.vkCmdSetScissor(commands, 0, 1, &scissor);

            var vp = Matrix.Transpose(shadowViewProjections[cascade]);
            for (var bucket = 2; bucket < 4; bucket++)
            {
                api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics,
                    bucket == 2 ? shadowPipeline : shadowPipelineNoCull);
                context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);
                var push = new ModelPushConstants
                {
                    World0 = new Vector4(vp.M11, vp.M12, vp.M13, vp.M14),
                    World1 = new Vector4(vp.M21, vp.M22, vp.M23, vp.M24),
                    World2 = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34),
                    World3 = new Vector4(vp.M41, vp.M42, vp.M43, vp.M44),
                    DrawData = gpuDraws.DrawDataAddress(bucket),
                };
                api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
                    0, (uint)sizeof(ModelPushConstants), &push);
                api.vkCmdDrawIndirectCount(commands, gpuDraws.BucketBuffer(bucket),
                    (ulong)GpuSceneDraws.CommandOffset, gpuDraws.BucketBuffer(bucket), 0,
                    gpuDraws.MaxParts, 16);
            }

            api.vkCmdEndRendering(commands);

            var toSampled = new VkImageMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.LateFragmentTests,
                srcAccessMask = VkAccessFlags2.DepthStencilAttachmentWrite,
                dstStageMask = VkPipelineStageFlags2.FragmentShader,
                dstAccessMask = VkAccessFlags2.ShaderSampledRead,
                oldLayout = VkImageLayout.DepthAttachmentOptimal,
                newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                image = image.Handle,
                subresourceRange = image.FullRange,
                srcQueueFamilyIndex = uint.MaxValue,
                dstQueueFamilyIndex = uint.MaxValue,
            };
            var toSampledInfo = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &toSampled };
            api.vkCmdPipelineBarrier2(commands, &toSampledInfo);
            map.Layout = VkImageLayout.ShaderReadOnlyOptimal;
        }

        ShadowPassRecorded = true;
    }

    // Records all opaque actors into the scene targets. Returns the number of draws recorded.
    public int Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, ulong frameConstants, out int culled)
    {
        var profile = Stopwatch.StartNew();
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

        foreach (var actor in directActors)
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
        {
            LastTransparentMs = 0;
            return 0;
        }

        var profile = Stopwatch.StartNew();
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
        LastTransparentMs = profile.Elapsed.TotalMilliseconds;
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

    private static Matrix BuildAbsoluteWorld(MatrixD worldD, float scale)
    {
        var world = (Matrix)worldD;
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
        foreach (var buffer in partBuffers)
            buffer?.Dispose();
        foreach (var buffer in rangeBuffers)
            buffer?.Dispose();
        foreach (var buffer in lodStateBuffers)
            buffer?.Dispose();

        var api = context.Device.Api;
        gpuDraws.Dispose();
        if (shadowMaps != null)
        {
            foreach (var map in shadowMaps)
                map.Dispose();
        }
        if (shadowPipeline != VkPipeline.Null)
            context.Release(() => context.Device.Api.vkDestroyPipeline(shadowPipeline, null));
        if (shadowPipelineNoCull != VkPipeline.Null)
            context.Release(() => context.Device.Api.vkDestroyPipeline(shadowPipelineNoCull, null));
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
