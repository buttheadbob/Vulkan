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
        public float Distance;
        public InstanceBuffer Instances;
        public int InstanceStart;
        public int InstanceCount;
    }

    private static readonly VkPipelineColorBlendAttachmentState PremultipliedBlend = new()
    {
        blendEnable = true,
        srcColorBlendFactor = VkBlendFactor.One,
        dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
        colorBlendOp = VkBlendOp.Add,
        srcAlphaBlendFactor = VkBlendFactor.One,
        dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
        alphaBlendOp = VkBlendOp.Add,
        colorWriteMask = VkColorComponentFlags.All,
    };

    private readonly GpuContext context;
    private readonly ModelCache models;
    private readonly InstanceBuffers instanceBuffers;
    private readonly TextureCache textures;
    private readonly List<TransparentDraw> transparentDraws = new();
    private readonly HashSet<uint> reportedSkinning = new();
    private readonly VkPipeline pipeline;
    private readonly VkPipeline pipelineNoCull;
    private readonly VkPipeline pipelineTransparent;
    private readonly VkPipeline pipelineTransparentNoCull;
    private readonly VkPipeline skinningPipeline;

    public ModelPass(GpuContext context, ModelCache models, InstanceBuffers instanceBuffers, TextureCache textures)
    {
        this.context = context;
        this.models = models;
        this.instanceBuffers = instanceBuffers;
        this.textures = textures;

        GraphicsPipelineBuilder Builder(VkCullModeFlags cullMode, bool transparent = false) => new()
        {
            Code = transparent ? ShaderBytecode.Transparent.Code.ToArray() : ShaderBytecode.Model.Code.ToArray(),
            VertexEntry = transparent ? ShaderBytecode.Transparent.VertexMain : ShaderBytecode.Model.VertexMain,
            FragmentEntry = transparent ? ShaderBytecode.Transparent.FragmentMain : ShaderBytecode.Model.FragmentMain,
            ColorFormats = [SceneTargets.ColorFormat],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = !transparent,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = cullMode,
            Blend = transparent ? PremultipliedBlend : GraphicsPipelineBuilder.Opaque,

            // D3D's default rasterizer state treats clockwise as front (IsFrontCounterClockwise=false).
            // The projection Y flip cancels Vulkan's opposite framebuffer Y direction, so the final
            // screen-space winding is still clockwise and Vulkan must be told the same.
            FrontFace = VkFrontFace.Clockwise,
        };

        pipeline = Builder(VkCullModeFlags.Back).Build(context, "model");
        pipelineNoCull = Builder(VkCullModeFlags.None).Build(context, "model-nocull");
        pipelineTransparent = Builder(VkCullModeFlags.Back, transparent: true).Build(context, "transparent");
        pipelineTransparentNoCull = Builder(VkCullModeFlags.None, transparent: true).Build(context, "transparent-nocull");
        skinningPipeline = BuildSkinningPipeline(context);
    }

    private static VkPipeline BuildSkinningPipeline(GpuContext context)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, ShaderBytecode.Skinning.Code);
        try
        {
            using var names = new VkStringArray([ShaderBytecode.Skinning.Main]);
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
                .CheckResult("vkCreateComputePipelines (skinning)");
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
            if (!lod.Skinned || lod.VertexCount == 0)
                continue;

            var indices = actor.SkeletonIndices;
            var skeleton = actor.SkeletonBones;
            var transforms = actor.BoneTransforms;
            var palette = actor.SkinPalette;
            if (indices == null || skeleton == null || transforms == null || palette == null)
            {
                if (reportedSkinning.Add(actor.Id))
                    RenderLog.Info($"Skinning: {model.Name} actor {actor.Id} has no skeleton data yet " +
                                   $"(bones {skeleton?.Length ?? 0}, indices {indices?.Length ?? 0}, " +
                                   $"transforms {transforms?.Length ?? 0}, palette {palette?.Length ?? 0})");
                continue;
            }

            var boneCount = Math.Min(indices.Length, palette.Length);
            if (boneCount == 0)
                continue;

            for (var i = 0; i < boneCount; i++)
            {
                var bone = indices[i];
                palette[i] = bone >= 0 && bone < skeleton.Length && bone < transforms.Length
                    ? skeleton[bone].SkinTransform * transforms[bone]
                    : Matrix.Identity;
            }

            var paletteAllocation = context.Transient.Allocate<Matrix>(boneCount);
            palette.AsSpan(0, boneCount).CopyTo(paletteAllocation.AsSpan<Matrix>(boneCount));

            var outputAllocation = context.Transient.Allocate((ulong)lod.VertexCount * 32, 16);

            var world = BuildWorld(worldD, actor.Scale, camera.Position);
            var worldT = Matrix.Transpose(world);
            var push = new SkinningPushConstants
            {
                World0 = new Vector4(worldT.M11, worldT.M12, worldT.M13, worldT.M14),
                World1 = new Vector4(worldT.M21, worldT.M22, worldT.M23, worldT.M24),
                World2 = new Vector4(worldT.M31, worldT.M32, worldT.M33, worldT.M34),
                World3 = new Vector4(worldT.M41, worldT.M42, worldT.M43, worldT.M44),
                Input = lod.Vertices.DeviceAddress,
                Output = outputAllocation.Address,
                Bones = paletteAllocation.Address,
                VertexCount = lod.VertexCount,
                BoneCount = (uint)boneCount,
            };

            api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, skinningPipeline);
            api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
                0, (uint)sizeof(SkinningPushConstants), &push);
            api.vkCmdDispatch(commands, (lod.VertexCount + 63) / 64, 1, 1);

            actor.SkinnedVertexAddress = outputAllocation.Address;
            any = true;
            if (reportedSkinning.Add(actor.Id))
                RenderLog.Info($"Skinning: {model.Name} actor {actor.Id}, {lod.VertexCount} vertices, {boneCount} bones");
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

    // Records all opaque actors into the scene targets. Returns the number of draws recorded.
    public int Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        MyNewLoddingSettings lodding, SceneActors actors, ulong frameConstants, out int culled)
    {
        var frustum = new Frustum(camera.CullingViewProjection);
        var loddingSettings = lodding.Global;

        var api = context.Device.Api;
        VkPipeline boundPipeline = VkPipeline.Null;
        transparentDraws.Clear();

        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var draws = 0;
        culled = 0;
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

                // The skinning compute pass already applied the entity world matrix
                var drawMatrix = matrix;
                var verticesAddress = lodModel.Vertices.DeviceAddress;
                if (lodModel.Skinned)
                {
                    if (actor.SkinnedVertexAddress == 0)
                        continue;
                    drawMatrix = Matrix.Identity;
                    verticesAddress = actor.SkinnedVertexAddress;
                }

                foreach (var part in lodModel.Parts)
                {
                    if (part.IndexCount == 0)
                        continue;

                    // Screen areas and similar can hide/show a single material through UpdateModelProperties
                    if ((SceneActors.GetMaterialFlags(actor, part.MaterialName) & RenderFlags.Visible) == 0)
                        continue;

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
                                Distance = matrix.Translation.Length(),
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
                    var material = BuildMaterial(actor, part, lodModel, instances != null, cm, ng, ext, alpha);
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
                    draws++;
                }
            }
        }

        // Transparent parts after the opaque pass: depth read-only, back-to-front, premultiplied blend
        if (transparentDraws.Count != 0)
        {
            transparentDraws.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));
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
        }

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
        TextureCache.Entry extensions, TextureCache.Entry alphamask)
    {
        var flags = 0u;
        if (colorMetal != null) flags |= DrawMaterial.HasColorMetal;
        if (normalGloss != null) flags |= DrawMaterial.HasNormalGloss;
        if (extensions != null) flags |= DrawMaterial.HasExtensions;
        if (alphamask != null) flags |= DrawMaterial.HasAlphamask;
        if (lod.HasTangents) flags |= DrawMaterial.HasTangent;
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
            or MyMeshDrawTechnique.MESH_INSTANCED
            or MyMeshDrawTechnique.MESH_INSTANCED_GENERIC;

    public void Dispose()
    {
        var api = context.Device.Api;
        var cullHandle = pipeline;
        var noCullHandle = pipelineNoCull;
        var transparentCull = pipelineTransparent;
        var transparentNoCull = pipelineTransparentNoCull;
        var skinning = skinningPipeline;
        context.Release(() =>
        {
            api.vkDestroyPipeline(cullHandle, null);
            api.vkDestroyPipeline(noCullHandle, null);
            api.vkDestroyPipeline(transparentCull, null);
            api.vkDestroyPipeline(transparentNoCull, null);
            api.vkDestroyPipeline(skinning, null);
        });
    }
}
