using System;
using System.Collections.Generic;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// A sub-allocation of device memory
internal readonly unsafe struct GpuAllocation
{
    internal readonly GpuMemory.Block Block;
    public readonly ulong Offset;
    public readonly ulong Size;

    internal GpuAllocation(GpuMemory.Block block, ulong offset, ulong size)
    {
        Block = block;
        Offset = offset;
        Size = size;
    }

    public bool IsNull => Block == null;
    public VkDeviceMemory Memory => Block.Memory;

    // Persistently mapped pointer to the allocation, null if the memory is not host visible
    public byte* Mapped => Block.Mapped == null ? null : Block.Mapped + Offset;
}

// Device memory allocator. Large blocks per memory type are sub-allocated with a first-fit free list.
// Buffers and optimally tiled images never share a block, so bufferImageGranularity needs no attention.
// Host-visible blocks are mapped once for their whole lifetime. Thread safe.
internal sealed unsafe class GpuMemory : IDisposable
{
    private const ulong DefaultBlockSize = 64ul << 20;

    // Allocations at least this large get their own VkDeviceMemory
    private const ulong DedicatedThreshold = 32ul << 20;

    internal sealed class Block
    {
        public VkDeviceMemory Memory;
        public ulong Size;
        public byte* Mapped;
        public uint MemoryType;
        public bool Linear;
        public bool Dedicated;

        // Free ranges, sorted by offset, never adjacent (merged on free)
        public readonly List<(ulong Offset, ulong Size)> Free = new();
    }

    private readonly VulkanDevice device;
    private readonly VkPhysicalDeviceMemoryProperties properties;
    private readonly List<Block> blocks = new();
    private readonly object sync = new();

    public ulong AllocatedBytes { get; private set; }
    public ulong DeviceLocalAllocatedBytes { get; private set; }
    public ulong DeviceLocalTotalBytes { get; }

    public GpuMemory(VulkanInstance instance, VulkanDevice device)
    {
        this.device = device;
        VkPhysicalDeviceMemoryProperties memoryProperties;
        instance.Api.vkGetPhysicalDeviceMemoryProperties(device.PhysicalDevice, &memoryProperties);
        properties = memoryProperties;
        for (var i = 0; i < properties.memoryHeapCount; i++)
        {
            var heap = properties.memoryHeaps[(int)i];
            if ((heap.flags & VkMemoryHeapFlags.DeviceLocal) != 0)
                DeviceLocalTotalBytes += heap.size;
        }
    }

    // linear: true for buffers and linear images, false for optimally tiled images
    public GpuAllocation Allocate(VkMemoryRequirements requirements, VkMemoryPropertyFlags required, VkMemoryPropertyFlags preferred, bool linear)
    {
        var memoryType = FindMemoryType(requirements.memoryTypeBits, required | preferred)
                         ?? FindMemoryType(requirements.memoryTypeBits, required)
                         ?? throw new InvalidOperationException($"No memory type with {required} for type bits 0x{requirements.memoryTypeBits:X}");

        lock (sync)
        {
            if (requirements.size >= DedicatedThreshold)
            {
                var dedicated = CreateBlock(memoryType, requirements.size, linear, dedicated: true);
                dedicated.Free.Clear();
                return new GpuAllocation(dedicated, 0, requirements.size);
            }

            foreach (var block in blocks)
            {
                if (block.Dedicated || block.MemoryType != memoryType || block.Linear != linear)
                    continue;

                if (TryAllocateFrom(block, requirements.size, requirements.alignment, out var offset))
                    return new GpuAllocation(block, offset, requirements.size);
            }

            var newBlock = CreateBlock(memoryType, Math.Max(DefaultBlockSize, requirements.size), linear, dedicated: false);
            if (!TryAllocateFrom(newBlock, requirements.size, requirements.alignment, out var newOffset))
                throw new InvalidOperationException("Allocation does not fit into a new memory block");

            return new GpuAllocation(newBlock, newOffset, requirements.size);
        }
    }

    public void Free(GpuAllocation allocation)
    {
        if (allocation.IsNull)
            return;

        lock (sync)
        {
            var block = allocation.Block;
            if (block.Dedicated)
            {
                DestroyBlock(block);
                return;
            }

            var free = block.Free;
            var index = 0;
            while (index < free.Count && free[index].Offset < allocation.Offset)
                index++;

            var offset = allocation.Offset;
            var size = allocation.Size;

            // Merge with the following range
            if (index < free.Count && offset + size == free[index].Offset)
            {
                size += free[index].Size;
                free.RemoveAt(index);
            }

            // Merge with the preceding range
            if (index > 0 && free[index - 1].Offset + free[index - 1].Size == offset)
            {
                offset = free[index - 1].Offset;
                size += free[index - 1].Size;
                free.RemoveAt(--index);
            }

            free.Insert(index, (offset, size));

            // Release completely empty blocks, but keep one per kind to avoid churn
            if (free.Count == 1 && free[0].Size == block.Size && CountBlocksLike(block) > 1)
                DestroyBlock(block);
        }
    }

    private static bool TryAllocateFrom(Block block, ulong size, ulong alignment, out ulong offset)
    {
        var free = block.Free;
        for (var i = 0; i < free.Count; i++)
        {
            var (rangeOffset, rangeSize) = free[i];
            var aligned = (rangeOffset + alignment - 1) / alignment * alignment;
            var padding = aligned - rangeOffset;
            if (padding + size > rangeSize)
                continue;

            free.RemoveAt(i);

            // Keep the remainder after the allocation, and the alignment padding before it
            var end = aligned + size;
            var remainder = rangeOffset + rangeSize - end;
            if (remainder > 0)
                free.Insert(i, (end, remainder));
            if (padding > 0)
                free.Insert(i, (rangeOffset, padding));

            offset = aligned;
            return true;
        }

        offset = 0;
        return false;
    }

    private uint? FindMemoryType(uint typeBits, VkMemoryPropertyFlags flags)
    {
        for (uint i = 0; i < properties.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (properties.memoryTypes[(int)i].propertyFlags & flags) == flags)
                return i;
        }

        return null;
    }

    private Block CreateBlock(uint memoryType, ulong size, bool linear, bool dedicated)
    {
        // Every allocation may back a buffer whose device address is taken
        var flagsInfo = new VkMemoryAllocateFlagsInfo { flags = VkMemoryAllocateFlags.DeviceAddress };
        var allocateInfo = new VkMemoryAllocateInfo
        {
            pNext = &flagsInfo,
            allocationSize = size,
            memoryTypeIndex = memoryType,
        };

        VkDeviceMemory memory;
        device.Api.vkAllocateMemory(&allocateInfo, null, &memory).CheckResult("vkAllocateMemory");

        byte* mapped = null;
        if ((properties.memoryTypes[(int)memoryType].propertyFlags & VkMemoryPropertyFlags.HostVisible) != 0)
        {
            void* pointer;
            device.Api.vkMapMemory(memory, 0, VK_WHOLE_SIZE, 0, &pointer).CheckResult("vkMapMemory");
            mapped = (byte*)pointer;
        }

        var block = new Block
        {
            Memory = memory,
            Size = size,
            Mapped = mapped,
            MemoryType = memoryType,
            Linear = linear,
            Dedicated = dedicated,
        };
        block.Free.Add((0, size));
        blocks.Add(block);
        AllocatedBytes += size;
        if (IsDeviceLocal(memoryType))
            DeviceLocalAllocatedBytes += size;
        return block;
    }

    private void DestroyBlock(Block block)
    {
        if (block.Mapped != null)
            device.Api.vkUnmapMemory(block.Memory);

        device.Api.vkFreeMemory(block.Memory, null);
        blocks.Remove(block);
        AllocatedBytes -= block.Size;
        if (IsDeviceLocal(block.MemoryType))
            DeviceLocalAllocatedBytes -= block.Size;
    }

    private bool IsDeviceLocal(uint memoryType)
    {
        var heapIndex = properties.memoryTypes[(int)memoryType].heapIndex;
        return (properties.memoryHeaps[(int)heapIndex].flags & VkMemoryHeapFlags.DeviceLocal) != 0;
    }

    private int CountBlocksLike(Block block)
    {
        var count = 0;
        foreach (var b in blocks)
        {
            if (!b.Dedicated && b.MemoryType == block.MemoryType && b.Linear == block.Linear)
                count++;
        }

        return count;
    }

    public void Dispose()
    {
        lock (sync)
        {
            while (blocks.Count != 0)
                DestroyBlock(blocks[^1]);
        }
    }
}
