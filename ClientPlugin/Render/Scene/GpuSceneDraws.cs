using System;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Scene;

// Persistent per-part record, referenced by the culling compute. Materials/vertices/indices are
// resolved when the actor/model/material changes, not every frame. Must match Culling.slang.
[StructLayout(LayoutKind.Sequential)]
internal struct GpuScenePart
{
    public DrawMaterial Material;
    public ulong Vertices;
    public ulong Indices;
    public uint IndexStart;
    public uint IndexCount;
    public uint Pad0;
    public uint Pad1;

    public const uint Size = 80;
}

// One entry per LOD of a cached actor: [start, start+count) in the persistent part buffer.
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLodRange
{
    public uint Start;
    public uint Count;
}

// Per-frame actor state consumed by Culling.slang. All float4s are first so the std430 layout is
// unambiguous; the LOD selection state lives in a separate persistent buffer (lodStateIndex).
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSceneActor
{
    public Vector4 World0;
    public Vector4 World1;
    public Vector4 World2;
    public Vector4 World3;
    public Vector4 ColorMaskHsv;
    public Vector4 Bounds;       // xyz: absolute bounds center, w: radius
    public Vector4 Origin;       // xyz: absolute origin, w: scale
    public Vector4 LodDistances0; // LOD1..4 switch distances
    public Vector4 LodDistances1; // LOD5..8 switch distances
    public ulong Instances;      // cube instance buffer address, 0 when not instanced
    public ulong SkinnedVertices; // deformed vertex buffer, when Flags has Skinned
    public uint InstanceStart;
    public uint InstanceCount;
    public uint Flags;           // 1: NoBackFaceCulling, 2: DrawOutsideViewDistance, 4: Visible, 8: Skinned
    public uint RangeBase;       // index into the persistent LOD range buffer
    public uint LodStateIndex;   // index into the persistent LOD state buffer
    public uint LodCount;
    public uint ForcedLod;       // skinned actors use the LOD the skinning pass produced
    public float MaxViewDistance;

    public const uint NoBackFaceCulling = 1u << 0;
    public const uint DrawOutsideViewDistance = 1u << 1;
    public const uint Visible = 1u << 2;
    public const uint Skinned = 1u << 3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CullParams
{
    public float LodCoefficient;
    public float ObjectDistanceAdd;
    public float ObjectDistanceMult;
    public float Hysteresis;
    public uint EnableLodSelection;
    public uint LodSelection;
    public uint LodUpdate;
    public uint ActorCount;
    public uint MaxParts;
    public uint Pad0;
    public ulong Bucket0;
    public ulong Bucket1;
    public ulong Bucket2;
    public ulong Bucket3;
    public ulong Diagnostics;
    public Vector4 CameraPosition;
    public Vector4 SkinnedOffset;   // main-camera-relative -> probe-camera-relative for skinned draws
    public uint DrawAll;
    public uint ShadowCulling;      // 1: append LOD0 shadow casters, 0: probe
}

// Must match Shaders/Scene/Culling.slang and Scene/Model.slang (184 bytes)
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDrawData
{
    public Vector4 World0;
    public Vector4 World1;
    public Vector4 World2;
    public Vector4 World3;
    public Vector4 ColorMaskHsv;
    public DrawMaterial Material;
    public ulong Vertices;
    public ulong Indices;
    public uint IndexStart;
    public uint IndexCount;
    public uint Pad0;
    public uint Pad1;
    public ulong Instances;
    public uint InstanceStart;
    public uint InstanceCount;
    public uint Pad2;
    public uint Pad3;
}

// Must match Shaders/Scene/Culling.slang
[StructLayout(LayoutKind.Sequential)]
internal struct CullPushConstants
{
    public Vector4 ViewProjection0;
    public Vector4 ViewProjection1;
    public Vector4 ViewProjection2;
    public Vector4 ViewProjection3;
    public ulong Actors;
    public ulong Parts;
    public ulong Ranges;
    public ulong LodStates;
    public ulong Params;
}

// GPU-driven opaque path. The persistent part/range/LOD-state buffers are owned by ModelPass and
// rebuilt only on render messages; this class owns the per-frame indirect buckets: forward cull and
// no-cull, plus LOD0 shadow caster cull and no-cull. The shadow buckets are always LOD0 so a ship
// switching to LOD1 does not lose its shadows.
internal sealed unsafe class GpuSceneDraws : IDisposable
{
    public const int CommandOffset = 64;
    public const int DrawStride = 184;
    private const int InitialCapacity = 8192;

    private readonly GpuContext context;
    private readonly GpuBuffer[,] bucketBuffers = new GpuBuffer[FrameLoop.FramesInFlight, 6];
    private readonly GpuBuffer[] diagnosticsBuffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly int[] capacities = new int[FrameLoop.FramesInFlight];
    private readonly VkPipeline pipeline;
    private int slot;

    public GpuDiagnosticsCounters Counters { get; private set; }

    public GpuSceneDraws(GpuContext context)
    {
        this.context = context;
        pipeline = BuildPipeline(context);
        for (var i = 0; i < diagnosticsBuffers.Length; i++)
            diagnosticsBuffers[i] = GpuBuffer.Create(context, (ulong)sizeof(GpuDiagnosticsCounters),
                VkBufferUsageFlags.StorageBuffer, hostVisible: true);
    }

    private static VkPipeline BuildPipeline(GpuContext context)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, ShaderBytecode.Culling.Code);
        try
        {
            using var names = new VkStringArray([ShaderBytecode.Culling.Main]);
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

            VkPipeline created;
            api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &createInfo, null, &created)
                .CheckResult("vkCreateComputePipelines (culling)");
            return created;
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }

    public void Begin(int frameSlot, int maxDraws)
    {
        slot = frameSlot;
        // The fence for this slot was waited on, so the previous use's counters are complete.
        Counters = *(GpuDiagnosticsCounters*)diagnosticsBuffers[slot].Mapped;
        *(GpuDiagnosticsCounters*)diagnosticsBuffers[slot].Mapped = default;
        EnsureCapacity(maxDraws);
        for (var bucket = 0; bucket < 4; bucket++)
            *(uint*)bucketBuffers[slot, bucket].Mapped = 0;
    }

    // The culling shader derives its planes from these columns, replacing the old CPU Frustum.
    private static void GetViewProjectionColumns(Matrix matrix,
        out Vector4 c0, out Vector4 c1, out Vector4 c2, out Vector4 c3)
    {
        c0 = new Vector4(matrix.M11, matrix.M21, matrix.M31, matrix.M41);
        c1 = new Vector4(matrix.M12, matrix.M22, matrix.M32, matrix.M42);
        c2 = new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
        c3 = new Vector4(matrix.M14, matrix.M24, matrix.M34, matrix.M44);
    }

    public void EnsureProbeBuckets(VkCommandBuffer commands)
    {
        // Whole-cube capture records several probe faces into one command buffer, so the CPU-side
        // counter reset in Begin() would happen before the GPU executes any of them. Reset the
        // counters on the GPU instead, between faces.
        var api = context.Device.Api;
        var waitForPreviousFace = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.DrawIndirect | VkPipelineStageFlags2.VertexShader,
            srcAccessMask = VkAccessFlags2.IndirectCommandRead | VkAccessFlags2.ShaderRead,
            dstStageMask = VkPipelineStageFlags2.Transfer | VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.TransferWrite | VkAccessFlags2.ShaderRead |
                            VkAccessFlags2.ShaderWrite,
        };
        var waitInfo = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &waitForPreviousFace };
        api.vkCmdPipelineBarrier2(commands, &waitInfo);

        for (var bucket = 4; bucket < 6; bucket++)
            api.vkCmdFillBuffer(commands, bucketBuffers[slot, bucket].Handle, 0, sizeof(uint), 0);
    }

    private void EnsureCapacity(int needed)
    {
        var capacity = capacities[slot];
        if (capacity >= needed && bucketBuffers[slot, 0] != null)
            return;

        var newCapacity = Math.Max(Math.Max(needed, InitialCapacity), capacity * 2);
        var bucketSize = (ulong)(CommandOffset + newCapacity * 16 + newCapacity * DrawStride);
        for (var bucket = 0; bucket < 6; bucket++)
        {
            bucketBuffers[slot, bucket]?.Dispose();
            bucketBuffers[slot, bucket] = GpuBuffer.Create(context, bucketSize,
                VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer |
                VkBufferUsageFlags.TransferDst, hostVisible: true);
            *(uint*)bucketBuffers[slot, bucket].Mapped = 0;
        }

        capacities[slot] = newCapacity;
    }

    public void Dispatch(VkCommandBuffer commands, Matrix cullingViewProjection, Vector3D cameraPosition,
        float lodCoefficient, ulong actors, uint actorCount, ulong parts, ulong ranges, ulong lodStates,
        MyNewLoddingSettings lodding)
    {
        if (actorCount == 0)
            return;

        var api = context.Device.Api;
        var hostBarrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.Host,
            srcAccessMask = VkAccessFlags2.HostWrite,
            dstStageMask = VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.ShaderRead | VkAccessFlags2.ShaderWrite,
        };
        var hostDependency = new VkDependencyInfo
        {
            memoryBarrierCount = 1,
            pMemoryBarriers = &hostBarrier,
        };
        api.vkCmdPipelineBarrier2(commands, &hostDependency);

        GetViewProjectionColumns(cullingViewProjection,
            out var vp0, out var vp1, out var vp2, out var vp3);

        var settings = MyGlobalLoddingSettings.Default;
        var parameters = new CullParams
        {
            LodCoefficient = lodCoefficient,
            ObjectDistanceAdd = settings.ObjectDistanceAdd,
            ObjectDistanceMult = settings.ObjectDistanceMult,
            Hysteresis = settings.HisteresisRatio,
            EnableLodSelection = settings.EnableLodSelection ? 1u : 0u,
            LodSelection = (uint)Math.Clamp(settings.LodSelection, 0, 7),
            LodUpdate = settings.IsUpdateEnabled ? 1u : 0u,
            ActorCount = actorCount,
            MaxParts = (uint)capacities[slot],
            Bucket0 = bucketBuffers[slot, 0].DeviceAddress,
            Bucket1 = bucketBuffers[slot, 1].DeviceAddress,
            Bucket2 = bucketBuffers[slot, 2].DeviceAddress,
            Bucket3 = bucketBuffers[slot, 3].DeviceAddress,
            Diagnostics = diagnosticsBuffers[slot].DeviceAddress,
            CameraPosition = new Vector4((Vector3)cameraPosition, 0),
            SkinnedOffset = Vector4.Zero,
            DrawAll = 0,
            ShadowCulling = 1u,
        };
        var paramsAllocation = context.Transient.Allocate<CullParams>();
        paramsAllocation.As<CullParams>() = parameters;

        var push = new CullPushConstants
        {
            ViewProjection0 = vp0,
            ViewProjection1 = vp1,
            ViewProjection2 = vp2,
            ViewProjection3 = vp3,
            Actors = actors,
            Parts = parts,
            Ranges = ranges,
            LodStates = lodStates,
            Params = paramsAllocation.Address,
        };

        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, pipeline);
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
            0, (uint)sizeof(CullPushConstants), &push);
        api.vkCmdDispatch(commands, (actorCount + 63) / 64, 1, 1);

        var drawBarrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.ShaderWrite,
            dstStageMask = VkPipelineStageFlags2.VertexShader | VkPipelineStageFlags2.DrawIndirect,
            dstAccessMask = VkAccessFlags2.ShaderRead | VkAccessFlags2.IndirectCommandRead,
        };
        var drawDependency = new VkDependencyInfo
        {
            memoryBarrierCount = 1,
            pMemoryBarriers = &drawBarrier,
        };
        api.vkCmdPipelineBarrier2(commands, &drawDependency);
    }

    public void DispatchProbe(VkCommandBuffer commands, Matrix cullingViewProjection, Vector3D cameraPosition,
        float lodCoefficient, ulong actors, uint actorCount, ulong parts, ulong ranges, ulong lodStates,
        MyNewLoddingSettings lodding, Vector3 skinnedOffset)
    {
        if (actorCount == 0)
            return;

        var api = context.Device.Api;
        EnsureProbeBuckets(commands);
        var hostBarrier = new VkMemoryBarrier2
        {
            // Compute is included so the main view's LOD-state writes are visible to the probe
            // cull dispatches (the probe reads that state with LodUpdate = 0).
            srcStageMask = VkPipelineStageFlags2.Host | VkPipelineStageFlags2.Transfer |
                           VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.HostWrite | VkAccessFlags2.TransferWrite |
                            VkAccessFlags2.ShaderWrite,
            dstStageMask = VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.ShaderRead | VkAccessFlags2.ShaderWrite,
        };
        var hostDependency = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &hostBarrier };
        api.vkCmdPipelineBarrier2(commands, &hostDependency);

        GetViewProjectionColumns(cullingViewProjection,
            out var vp0, out var vp1, out var vp2, out var vp3);
        var settings = MyGlobalLoddingSettings.Default;
        var parameters = new CullParams
        {
            LodCoefficient = lodCoefficient,
            ObjectDistanceAdd = settings.ObjectDistanceAdd,
            ObjectDistanceMult = settings.ObjectDistanceMult,
            Hysteresis = settings.HisteresisRatio,
            EnableLodSelection = settings.EnableLodSelection ? 1u : 0u,
            LodSelection = (uint)Math.Clamp(settings.LodSelection, 0, 7),
            // The probe uses the main view's LOD state: re-selecting from a different capture
            // position would both corrupt the main LOD state and mismatch skinned vertex buffers.
            LodUpdate = 0,
            ActorCount = actorCount,
            MaxParts = (uint)capacities[slot],
            Bucket0 = bucketBuffers[slot, 4].DeviceAddress,
            Bucket1 = bucketBuffers[slot, 5].DeviceAddress,
            Bucket2 = bucketBuffers[slot, 4].DeviceAddress,
            Bucket3 = bucketBuffers[slot, 5].DeviceAddress,
            Diagnostics = diagnosticsBuffers[slot].DeviceAddress,
            CameraPosition = new Vector4((Vector3)cameraPosition, 0),
            SkinnedOffset = new Vector4(skinnedOffset, 0),
            DrawAll = 0,
            ShadowCulling = 0,
        };
        var paramsAllocation = context.Transient.Allocate<CullParams>();
        paramsAllocation.As<CullParams>() = parameters;
        var push = new CullPushConstants
        {
            ViewProjection0 = vp0, ViewProjection1 = vp1, ViewProjection2 = vp2, ViewProjection3 = vp3,
            Actors = actors, Parts = parts, Ranges = ranges, LodStates = lodStates,
            Params = paramsAllocation.Address,
        };
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, pipeline);
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
            0, (uint)sizeof(CullPushConstants), &push);
        api.vkCmdDispatch(commands, (actorCount + 63) / 64, 1, 1);
        var drawBarrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.ShaderWrite,
            dstStageMask = VkPipelineStageFlags2.VertexShader | VkPipelineStageFlags2.DrawIndirect,
            dstAccessMask = VkAccessFlags2.ShaderRead | VkAccessFlags2.IndirectCommandRead,
        };
        var drawDependency = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &drawBarrier };
        api.vkCmdPipelineBarrier2(commands, &drawDependency);
    }

    public VkBuffer BucketBuffer(int bucket) => bucketBuffers[slot, bucket].Handle;

    public ulong DrawDataAddress(int bucket) =>
        bucketBuffers[slot, bucket].DeviceAddress + (ulong)(CommandOffset + capacities[slot] * 16);

    public uint MaxParts => (uint)capacities[slot];

    public void Dispose()
    {
        var handle = pipeline;
        var api = context.Device.Api;
        context.Release(() => api.vkDestroyPipeline(handle, null));

        foreach (var buffer in bucketBuffers)
            buffer?.Dispose();
        foreach (var buffer in diagnosticsBuffers)
            buffer?.Dispose();
    }
}
