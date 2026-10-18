using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Vulkan;
using VRage;
using VRageRender.Messages;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

internal sealed class InstanceBuffer
{
    public uint Id;
    public MyRenderInstanceBufferType Type;
    public GpuBuffer Buffer;
    public int Count;
    public int Stride;
}

// GPU instance buffers created with CreateRenderInstanceBuffer. Cube data is MyCubeInstanceData (64 bytes),
// generic data is MyInstanceData (32 bytes); both are uploaded raw and decoded by the shaders.
//
// Each update replaces the buffer with a fresh one, so a frame in flight that still reads the old contents is
// safe (the old buffer goes through deferred destruction).
internal sealed class InstanceBuffers : IDisposable
{
    public const int CubeStride = 64;

    private readonly GpuContext context;
    private readonly Dictionary<uint, InstanceBuffer> buffers = new();

    public int Count => buffers.Count;

    public int TotalInstances
    {
        get
        {
            var total = 0;
            foreach (var buffer in buffers.Values)
                total += buffer.Count;
            return total;
        }
    }

    public InstanceBuffers(GpuContext context)
    {
        this.context = context;
    }

    public void Create(MyRenderMessageCreateRenderInstanceBuffer message)
    {
        buffers[message.ID] = new InstanceBuffer
        {
            Id = message.ID,
            Type = message.Type,
        };
    }

    public InstanceBuffer Get(uint id) => buffers.TryGetValue(id, out var buffer) ? buffer : null;

    public void UpdateCube(MyRenderMessageUpdateRenderCubeInstanceBuffer message)
    {
        var buffer = Get(message.ID);
        if (buffer == null)
            return;

        var data = message.InstanceData;
        if (data.Count == 0)
        {
            Replace(buffer, null, CubeStride, 0);
            return;
        }

        var size = Marshal.SizeOf<MyCubeInstanceData>();
        if (size != CubeStride)
            throw new InvalidOperationException($"MyCubeInstanceData is {size} bytes, expected {CubeStride}");

        var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)).ToArray();
        Replace(buffer, bytes, CubeStride, data.Count);
    }

    public void UpdateGeneric(MyRenderMessageUpdateRenderInstanceBufferRange message)
    {
        var buffer = Get(message.ID);
        if (buffer == null)
            return;

        var data = message.InstanceData;
        if (data == null || data.Length == 0)
        {
            Replace(buffer, null, 32, 0);
            return;
        }

        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<MyInstanceData>(data)).ToArray();
        Replace(buffer, bytes, 32, data.Length);
    }

    private void Replace(InstanceBuffer buffer, byte[] data, int stride, int count)
    {
        var old = buffer.Buffer;
        buffer.Stride = stride;
        buffer.Count = count;
        buffer.Buffer = null;

        if (data != null)
        {
            var gpuBuffer = GpuBuffer.Create(context, (ulong)data.Length,
                VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst, hostVisible: false);
            gpuBuffer.DebugName = $"InstanceBuffer {buffer.Id} ({count} x {stride})";
            context.Uploads.UploadBuffer(gpuBuffer, data);
            buffer.Buffer = gpuBuffer;
        }

        old?.Dispose();
    }

    public void Remove(uint id)
    {
        if (!buffers.TryGetValue(id, out var buffer))
            return;

        buffers.Remove(id);
        buffer.Buffer?.Dispose();
    }

    public void Dispose()
    {
        foreach (var buffer in buffers.Values)
            buffer.Buffer?.Dispose();
        buffers.Clear();
    }
}
