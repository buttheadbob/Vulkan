using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ClientPlugin.Render;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Render.Scene;

// Camera-facing transparent billboards. The game's thruster animator and the shared particle
// billboards feed this pass; results are written into the existing OIT targets.
internal sealed unsafe class BillboardPass : IDisposable
{
    private const int MaxBillboards = 16384;
    private const uint FlagAlphaCutout = 1u;
    private const uint FlagSingleChannel = 2u;
    private const uint FlagPointGlow = 4u;
    private const uint FlagLine = 8u;

    [StructLayout(LayoutKind.Sequential)]
    private struct BillboardInstance
    {
        public Vector4 P0;
        public Vector4 P1;
        public Vector4 P2;
        public Vector4 P3;
        public Vector4 Color;
        public Vector4 UvRect;
        public uint Texture;
        public uint Flags;
        public float AlphaCutout;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public ulong Instances;
        public ulong Frame;
        public uint Count;
        public uint Pad;
    }

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

    private static readonly VkPipelineColorBlendAttachmentState ProbeBlend = new()
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

    private readonly GpuContext context;
    private readonly TextureCache textures;
    private readonly VkPipeline pipeline;
    private readonly VkPipeline probePipeline;
    private readonly List<BillboardInstance> instances = new();

    public BillboardPass(GpuContext context, TextureCache textures)
    {
        this.context = context;
        this.textures = textures;
        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Billboard.Code.ToArray(),
            VertexEntry = ShaderBytecode.Billboard.VertexMain,
            FragmentEntry = ShaderBytecode.Billboard.FragmentMain,
            ColorFormats = [SceneTargets.OitAccumFormat, SceneTargets.OitRevealFormat],
            BlendStates = [OitAccumBlend, OitRevealBlend],
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = VkCullModeFlags.None,
            Topology = VkPrimitiveTopology.TriangleList,
        }.Build(context, "billboard");

        probePipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Billboard.Code.ToArray(),
            VertexEntry = ShaderBytecode.Billboard.VertexMain,
            FragmentEntry = ShaderBytecode.Billboard.FragmentProbe,
            ColorFormats = [SceneTargets.ColorFormat],
            Blend = ProbeBlend,
            DepthFormat = SceneTargets.DepthFormat,
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = VkCompareOp.GreaterOrEqual, // Reverse Z
            CullMode = VkCullModeFlags.None,
            Topology = VkPrimitiveTopology.TriangleList,
        }.Build(context, "billboard-probe");
    }

    public void Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera, SceneActors actors,
        ulong frameConstants, MySharedData sharedData, ThrusterEffects thrusterEffects)
    {
        BuildInstances(camera, actors, sharedData, thrusterEffects, includeSharedBillboards: true);
        DrawList(commands, targets, pipeline, instances, frameConstants);
    }

    // Renders the same billboards into one face of the reflection probe. Camera-facing quads are
    // rebuilt for the probe camera, and only the HDR color target is written.
    public void RecordProbe(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        SceneActors actors, ulong frameConstants, MySharedData sharedData, ThrusterEffects thrusterEffects)
    {
        // The probe only needs the thruster/emissive billboards. Generic space dust and tiny
        // particles turned into sparkle when reflected by the low-resolution cube.
        BuildInstances(camera, actors, sharedData, thrusterEffects, includeSharedBillboards: false);
        DrawList(commands, targets, probePipeline, instances, frameConstants);
    }

    private void BuildInstances(CameraState camera, SceneActors actors, MySharedData sharedData,
        ThrusterEffects thrusterEffects, bool includeSharedBillboards)
    {
        instances.Clear();
        cameraPosition = camera.Position;

        if (includeSharedBillboards)
        {
            var batch = sharedData?.Billboards.Read;
            if (batch?.Billboards != null)
            {
                foreach (var billboard in batch.Billboards)
                    Append(billboard, camera, actors);
            }
        }

        thrusterEffects?.Append(this, camera, actors);
    }

    private void DrawList(VkCommandBuffer commands, SceneTargets targets, VkPipeline pipeline,
        List<BillboardInstance> source, ulong frameConstants)
    {
        if (source.Count == 0)
            return;

        var count = Math.Min(source.Count, MaxBillboards);
        var allocation = context.Transient.Allocate<BillboardInstance>(count);
        var output = allocation.AsSpan<BillboardInstance>(count);
        for (var i = 0; i < count; i++)
            output[i] = source[i];

        var api = context.Device.Api;
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, pipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);

        var push = new PushConstants
        {
            Instances = allocation.Address,
            Frame = frameConstants,
            Count = (uint)count,
        };
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.All,
            0, (uint)sizeof(PushConstants), &push);
        api.vkCmdDraw(commands, 6, (uint)count, 0, 0);
    }

    // AddPointBillboard/AddLineBillboard mirror MyBillboardsHelper11, including material colour.
    public void AddPointBillboard(MyStringId materialId, Vector4 color, float intensity, Vector3D origin,
        float radius, float angle)
    {
        if (instances.Count >= MaxBillboards || radius <= 0f)
            return;

        var view = (Vector3)(cameraPosition - origin);
        if (view.LengthSquared() < 1e-12f)
            return;
        view.Normalize();

        var right = Vector3.Cross(Vector3.Up, view);
        if (right.LengthSquared() < 1e-8f)
            right = Vector3.Cross(Vector3.Forward, view);
        right.Normalize();
        var up = Vector3.Cross(view, right);

        var c = MathF.Cos(angle);
        var s = MathF.Sin(angle);
        var rx = right * c + up * s;
        var ry = up * c - right * s;

        var p0 = origin - rx * radius - ry * radius;
        var p1 = origin + rx * radius - ry * radius;
        var p2 = origin + rx * radius + ry * radius;
        var p3 = origin - rx * radius + ry * radius;
        AddWorld(instances, materialId, color, intensity, p0, p1, p2, p3, Vector2.Zero, Vector2.One, 0f,
            FlagPointGlow);
    }

    public void AddLineBillboard(MyStringId materialId, Vector4 color, float intensity, Vector3D origin,
        Vector3 direction, float length, float thickness)
    {
        if (instances.Count >= MaxBillboards || length <= 0f || thickness <= 0f)
            return;

        var dir = direction;
        if (dir.LengthSquared() < 1e-12f)
            return;
        dir.Normalize();

        var view = (Vector3)(cameraPosition - origin);
        if (view.LengthSquared() < 1e-12f)
            return;
        view.Normalize();

        var side = Vector3.Cross(dir, view);
        if (side.LengthSquared() < 1e-8f)
            side = Vector3.Cross(Vector3.Up, view);
        if (side.LengthSquared() < 1e-8f)
            side = Vector3.Right;
        side.Normalize();
        side *= thickness;

        var toCamera = cameraPosition - origin;
        toCamera.Normalize();
        var alignment = Math.Abs(Vector3.Dot(toCamera, dir));
        var attenuation = (1f - MathF.Pow(1f - alignment, 30f)) * 0.5f;
        color = new Vector4(color.X * attenuation, color.Y * attenuation, color.Z * attenuation, color.W);

        var end = origin + dir * length;
        var p0 = origin - side;
        var p1 = origin + side;
        var p2 = end + side;
        var p3 = end - side;
        AddWorld(instances, materialId, color, intensity, p0, p1, p2, p3, Vector2.Zero, Vector2.One, 0f,
            FlagLine);
    }

    // Absolute world position at the time of the call; the camera position is assigned by Record.
    private Vector3D cameraPosition;

    private void AddWorld(List<BillboardInstance> target, MyStringId materialId, Vector4 color, float intensity,
        Vector3D p0, Vector3D p1, Vector3D p2, Vector3D p3, Vector2 uvOffset, Vector2 uvSize, float alphaCutout,
        uint extraFlags = 0)
    {
        if (target.Count >= MaxBillboards)
            return;

        var material = MyTransparentMaterials.GetMaterial(materialId);
        var texture = GetTexture(material);
        var singleChannel = texture is { Status: TextureCache.State.Ready } &&
                            texture.Image.Format is VkFormat.Bc4UnormBlock or VkFormat.Bc4SnormBlock or VkFormat.R8Unorm;

        var c = color;
        c.X *= intensity;
        c.Y *= intensity;
        c.Z *= intensity;

        var flags = extraFlags;
        if (material.AlphaCutout)
            flags |= FlagAlphaCutout;
        if (singleChannel)
            flags |= FlagSingleChannel;

        var camera = cameraPosition;
        target.Add(new BillboardInstance
        {
            P0 = new Vector4((Vector3)(p0 - camera), 1f),
            P1 = new Vector4((Vector3)(p1 - camera), 1f),
            P2 = new Vector4((Vector3)(p2 - camera), 1f),
            P3 = new Vector4((Vector3)(p3 - camera), 1f),
            Color = c,
            UvRect = new Vector4(
                material.UVOffset.X + uvOffset.X,
                material.UVOffset.Y + uvOffset.Y,
                material.UVSize.X * uvSize.X,
                material.UVSize.Y * uvSize.Y),
            Texture = TextureCache.SlotOf(texture),
            Flags = flags,
            AlphaCutout = alphaCutout,
        });
    }

    private void Append(MyBillboard billboard, CameraState camera, SceneActors actors)
    {
        if (billboard == null || billboard.BlendType != MyBillboard.BlendTypeEnum.Standard ||
            billboard is MyTriangleBillboard)
            return;

        var p0 = billboard.Position0;
        var p1 = billboard.Position1;
        var p2 = billboard.Position2;
        var p3 = billboard.Position3;
        var direction = billboard.Position1;

        if (billboard.ParentID != uint.MaxValue)
        {
            var parent = actors.Find(billboard.ParentID);
            if (parent != null)
            {
                var world = actors.GetWorldMatrix(parent);
                if (billboard.LocalType == MyBillboard.LocalTypeEnum.Line)
                {
                    p0 = Vector3D.Transform(p0, world);
                    direction = Vector3D.TransformNormal(direction, world);
                }
                else if (billboard.LocalType == MyBillboard.LocalTypeEnum.Point)
                {
                    p0 = Vector3D.Transform(p0, world);
                }
                else
                {
                    p0 = Vector3D.Transform(p0, world);
                    p1 = Vector3D.Transform(p1, world);
                    p2 = Vector3D.Transform(p2, world);
                    p3 = Vector3D.Transform(p3, world);
                }
            }
        }

        var color = billboard.Color;
        color.X *= billboard.ColorIntensity;
        color.Y *= billboard.ColorIntensity;
        color.Z *= billboard.ColorIntensity;

        if (billboard.LocalType == MyBillboard.LocalTypeEnum.Point)
        {
            AddPointBillboard(billboard.Material, color, 1f, p0, (float)p2.X, (float)p2.Y);
            return;
        }

        if (billboard.LocalType == MyBillboard.LocalTypeEnum.Line)
        {
            AddLineBillboard(billboard.Material, color, 1f, p0, direction, (float)p2.X, (float)p2.Y);
            return;
        }

        AddWorld(instances, billboard.Material, color, 1f, p0, p1, p2, p3, billboard.UVOffset, billboard.UVSize,
            billboard.AlphaCutout);
    }

    private TextureCache.Entry GetTexture(MyTransparentMaterial material)
    {
        var path = material?.Texture;
        if (string.IsNullOrEmpty(path) || path.EndsWith("FAKE.dds", StringComparison.OrdinalIgnoreCase))
            path = "Textures\\Particles\\EngineThrustMiddle.dds";

        return textures.Get(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
            waitTillLoaded: false, TextureCache.TextureKind.ColorMetal);
    }

    // Camera is set by Record before appending shared/generated billboards.
    public void SetCamera(Vector3D position) => cameraPosition = position;

    public void Dispose()
    {
        var api = context.Device.Api;
        var handle = pipeline;
        context.Release(() => api.vkDestroyPipeline(handle, null));
    }
}
