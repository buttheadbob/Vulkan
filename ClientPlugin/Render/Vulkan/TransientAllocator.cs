using System;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Per-frame scratch memory for data written by the CPU once per frame and read by shaders through buffer
// device addresses (frame constants, per-draw data, instance lists). Linear allocation from a host-visible
// buffer per frame in flight; reset when the frame slot comes around again. Render thread only.
internal sealed unsafe class TransientAllocator : IDisposable
{
    public readonly struct Allocation
    {
        public readonly byte* Pointer;
        public readonly ulong Address;
        public readonly ulong Size;

        public Allocation(byte* pointer, ulong address, ulong size)
        {
            Pointer = pointer;
            Address = address;
            Size = size;
        }

        public Span<T> AsSpan<T>(int count) where T : unmanaged => new(Pointer, count);
        public ref T As<T>() where T : unmanaged => ref *(T*)Pointer;
    }

    private const ulong InitialSize = 4ul << 20;

    private readonly GpuContext context;
    private readonly GpuBuffer[] buffers = new GpuBuffer[FrameLoop.FramesInFlight];
    private int slot;
    private ulong offset;

    public TransientAllocator(GpuContext context)
    {
        this.context = context;
    }

    public void BeginFrame(int frameSlot)
    {
        slot = frameSlot;
        offset = 0;
    }

    // When the frame's buffer is full a larger one replaces it; the old one stays alive until the frame has
    // finished (deferred release), so addresses handed out earlier in the frame remain valid.
    public Allocation Allocate(ulong size, ulong alignment = 16)
    {
        var buffer = buffers[slot];
        var aligned = (offset + alignment - 1) / alignment * alignment;
        if (buffer == null || aligned + size > buffer.Size)
        {
            var capacity = Math.Max(Math.Max(size, (buffer?.Size ?? 0) * 2), InitialSize);
            buffer?.Dispose();
            buffer = GpuBuffer.Create(context, capacity, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.UniformBuffer, hostVisible: true);
            buffers[slot] = buffer;
            aligned = 0;
        }

        offset = aligned + size;
        return new Allocation(buffer.Mapped + aligned, buffer.DeviceAddress + aligned, size);
    }

    public Allocation Allocate<T>(int count = 1) where T : unmanaged => Allocate((ulong)(sizeof(T) * count));

    public void Dispose()
    {
        foreach (var buffer in buffers)
            buffer?.Dispose();
    }
}
