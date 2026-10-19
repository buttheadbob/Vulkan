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

    // The game sends many growing updates while a grid loads. Keep only the newest one per
    // buffer and upload it once per frame instead of copying/recreating the whole buffer for
    // every message.
    public MyRenderMessageUpdateRenderCubeInstanceBuffer PendingCube;
    public MyRenderMessageUpdateRenderInstanceBufferRange PendingGeneric;
    public bool PendingDirty;
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

    // Bumped whenever a buffer's contents/address change, so actor state that points at it refreshes.
    public long Revision { get; private set; }

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

        // Hold the newest message until the frame's upload point. The queue processor disposes
        // messages after the frame, so keep a reference until ProcessPending has copied the data.
        message.AddRef();
        buffer.PendingCube?.Dispose();
        buffer.PendingCube = message;
        buffer.PendingGeneric?.Dispose();
        buffer.PendingGeneric = null;
        buffer.PendingDirty = true;
    }

    public void UpdateGeneric(MyRenderMessageUpdateRenderInstanceBufferRange message)
    {
        var buffer = Get(message.ID);
        if (buffer == null)
            return;

        message.AddRef();
        buffer.PendingGeneric?.Dispose();
        buffer.PendingGeneric = message;
        buffer.PendingCube?.Dispose();
        buffer.PendingCube = null;
        buffer.PendingDirty = true;
    }

    // Render thread, once per frame after messages are processed: uploads the newest contents of
    // every changed instance buffer exactly once.
    public void ProcessPending()
    {
        foreach (var buffer in buffers.Values)
        {
            if (!buffer.PendingDirty)
                continue;

            buffer.PendingDirty = false;
            var cube = buffer.PendingCube;
            var generic = buffer.PendingGeneric;
            buffer.PendingCube = null;
            buffer.PendingGeneric = null;

            try
            {
                if (cube != null)
                {
                    var data = cube.InstanceData;
                    if (data.Count == 0)
                    {
                        Replace(buffer, null, CubeStride, 0);
                        continue;
                    }

                    var size = Marshal.SizeOf<MyCubeInstanceData>();
                    if (size != CubeStride)
                        throw new InvalidOperationException($"MyCubeInstanceData is {size} bytes, expected {CubeStride}");

                    var bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data)).ToArray();
                    Replace(buffer, bytes, CubeStride, data.Count);
                }
                else if (generic != null)
                {
                    var data = generic.InstanceData;
                    if (data == null || data.Length == 0)
                    {
                        Replace(buffer, null, 32, 0);
                        continue;
                    }

                    var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<MyInstanceData>(data)).ToArray();
                    Replace(buffer, bytes, 32, data.Length);
                }
            }
            finally
            {
                cube?.Dispose();
                generic?.Dispose();
            }
        }
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
            context.Transfer.UploadBuffer(gpuBuffer, data);
            buffer.Buffer = gpuBuffer;
        }

        old?.Dispose();
        Revision++;
    }

    public void Remove(uint id)
    {
        if (!buffers.TryGetValue(id, out var buffer))
            return;

        buffers.Remove(id);
        buffer.Buffer?.Dispose();
        buffer.PendingCube?.Dispose();
        buffer.PendingGeneric?.Dispose();
        buffer.PendingCube = null;
        buffer.PendingGeneric = null;
    }

    public void Dispose()
    {
        foreach (var buffer in buffers.Values)
        {
            buffer.Buffer?.Dispose();
            buffer.PendingCube?.Dispose();
            buffer.PendingGeneric?.Dispose();
        }
        buffers.Clear();
    }
}
