using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

internal sealed unsafe class GpuBuffer : IDisposable
{
    private readonly GpuContext context;

    public VkBuffer Handle { get; private set; }
    public GpuAllocation Allocation { get; private set; }
    public ulong Size { get; }

    // Address for shader access through pointers (buffer device address)
    public ulong DeviceAddress { get; }

    // Persistently mapped, null if not host visible
    public byte* Mapped => Allocation.Mapped;

    private GpuBuffer(GpuContext context, VkBuffer handle, GpuAllocation allocation, ulong size, ulong deviceAddress)
    {
        this.context = context;
        Handle = handle;
        Allocation = allocation;
        Size = size;
        DeviceAddress = deviceAddress;
    }

    // hostVisible: CPU-written every frame or staging (host visible + coherent, device local if available, as with resizable BAR)
    public static GpuBuffer Create(GpuContext context, ulong size, VkBufferUsageFlags usage, bool hostVisible)
    {
        var api = context.Device.Api;
        usage |= VkBufferUsageFlags.ShaderDeviceAddress;

        var createInfo = new VkBufferCreateInfo
        {
            size = size,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
        };

        VkBuffer buffer;
        api.vkCreateBuffer(&createInfo, null, &buffer).CheckResult("vkCreateBuffer");

        VkMemoryRequirements requirements;
        api.vkGetBufferMemoryRequirements(buffer, &requirements);

        var allocation = hostVisible
            ? context.Memory.Allocate(requirements, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent, VkMemoryPropertyFlags.None, linear: true)
            : context.Memory.Allocate(requirements, VkMemoryPropertyFlags.DeviceLocal, VkMemoryPropertyFlags.None, linear: true);

        api.vkBindBufferMemory(buffer, allocation.Memory, allocation.Offset).CheckResult("vkBindBufferMemory");

        var addressInfo = new VkBufferDeviceAddressInfo { buffer = buffer };
        var address = api.vkGetBufferDeviceAddress(&addressInfo);

        return new GpuBuffer(context, buffer, allocation, size, address);
    }

    public void Dispose()
    {
        if (Handle.IsNull)
            return;

        var handle = Handle;
        var allocation = Allocation;
        var device = context.Device;
        var memory = context.Memory;
        context.Release(() =>
        {
            device.Api.vkDestroyBuffer(handle, null);
            memory.Free(allocation);
        });

        Handle = VkBuffer.Null;
        Allocation = default;
    }
}
