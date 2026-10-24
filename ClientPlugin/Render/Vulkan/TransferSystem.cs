using System;
using System.Collections.Generic;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Asynchronous asset uploads. CPU data is staged into host-visible memory and copied by a
// dedicated transfer queue when one exists. The transfer batch signals a timeline semaphore;
// the graphics submission waits on that value, so the GPU (not the CPU) performs the wait and
// the copy overlaps the previous frame. Without a separate queue the copies are recorded into
// the graphics command buffer, exactly like the old path.
//
// Render thread only. Staging rings are per frame slot and safe to reuse after the slot fence,
// because the graphics frame always waits for this frame's transfer before it can complete.
internal sealed unsafe class TransferSystem : IDisposable
{
    private const ulong RingSize = 32ul << 20;
    private const ulong Alignment = 16;

    private sealed class PendingImageUpload
    {
        public GpuImage Image;
        public VkBuffer Source;
        public VkBufferImageCopy[] Regions;
        public bool GenerateMips;
        public GpuBuffer OwnedStaging;
    }

    internal sealed class PendingBufferUpload
    {
        public VkBuffer Source;
        public GpuBuffer Destination;
        public VkBufferCopy[] Regions;
        public GpuBuffer OwnedStaging;
    }

    private readonly GpuContext context;
    private readonly bool dedicated;
    private readonly GpuBuffer[] rings = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly VkCommandPool[] pools = new VkCommandPool[FrameLoop.FramesInFlight];
    private readonly VkCommandBuffer[] commandBuffers = new VkCommandBuffer[FrameLoop.FramesInFlight];
    private readonly List<PendingImageUpload> pendingImages = new();
    private readonly List<PendingBufferUpload> pendingBuffers = new();
    private readonly VkSemaphore timeline;
    private ulong submittedValue;
    private ulong ringOffset;
    private int slot;
    private bool frameOpen;

    public bool HasDedicatedQueue => dedicated;
    public VkSemaphore Timeline => timeline;

    public TransferSystem(GpuContext context)
    {
        this.context = context;
        dedicated = context.Device.HasDedicatedTransfer;

        for (var i = 0; i < rings.Length; i++)
            rings[i] = GpuBuffer.Create(context, RingSize, VkBufferUsageFlags.TransferSrc, hostVisible: true);

        if (!dedicated)
            return;

        var api = context.Device.Api;
        for (var i = 0; i < pools.Length; i++)
        {
            var poolInfo = new VkCommandPoolCreateInfo
            {
                flags = VkCommandPoolCreateFlags.Transient,
                queueFamilyIndex = context.Device.TransferQueueFamily,
            };
            api.vkCreateCommandPool(&poolInfo, null, out pools[i]).CheckResult("vkCreateCommandPool (transfer)");

            var allocInfo = new VkCommandBufferAllocateInfo
            {
                commandPool = pools[i],
                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer commandBuffer;
            api.vkAllocateCommandBuffers(&allocInfo, &commandBuffer).CheckResult("vkAllocateCommandBuffers (transfer)");
            commandBuffers[i] = commandBuffer;
        }

        var typeInfo = new VkSemaphoreTypeCreateInfo
        {
            semaphoreType = VkSemaphoreType.Timeline,
            initialValue = 0,
        };
        var semaphoreInfo = new VkSemaphoreCreateInfo { pNext = &typeInfo };
        api.vkCreateSemaphore(&semaphoreInfo, null, out timeline).CheckResult("vkCreateSemaphore (transfer timeline)");
    }

    // The slot's transfer command buffer and staging ring are free once the graphics fence for
    // that slot has been waited on.
    public void BeginFrame(int frameSlot)
    {
        slot = frameSlot;
        ringOffset = 0;
        frameOpen = true;

        if (dedicated)
            context.Device.Api.vkResetCommandPool(pools[slot], VkCommandPoolResetFlags.None).CheckResult("vkResetCommandPool (transfer)");
    }

    public void UploadBuffer(GpuBuffer destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
    {
        var (source, baseOffset, owned) = Stage(data);

        pendingBuffers.Add(new PendingBufferUpload
        {
            Source = source,
            Destination = destination,
            Regions =
            [
                new VkBufferCopy
                {
                    srcOffset = baseOffset,
                    dstOffset = destinationOffset,
                    size = (ulong)data.Length,
                },
            ],
            OwnedStaging = owned,
        });
    }

    // Worker-thread safe: the staging buffer is allocated on the calling thread. The result is
    // handed to AddPrepared on the render thread, which records and submits it.
    public PendingBufferUpload PrepareBufferUpload(GpuBuffer destination, ReadOnlySpan<byte> data, ulong destinationOffset = 0)
    {
        var staging = GpuBuffer.Create(context, (ulong)data.Length, VkBufferUsageFlags.TransferSrc, hostVisible: true);
        data.CopyTo(new Span<byte>(staging.Mapped, data.Length));

        return new PendingBufferUpload
        {
            Source = staging.Handle,
            Destination = destination,
            Regions =
            [
                new VkBufferCopy
                {
                    srcOffset = 0,
                    dstOffset = destinationOffset,
                    size = (ulong)data.Length,
                },
            ],
            OwnedStaging = staging,
        };
    }

    // Render thread: adds uploads prepared on worker threads to this frame's batch.
    public void AddPrepared(IEnumerable<PendingBufferUpload> uploads)
    {
        pendingBuffers.AddRange(uploads);
    }

    public void UploadImage(GpuImage image, ReadOnlySpan<byte> data, VkBufferImageCopy[] regions, bool generateMips = false)
    {
        var (source, baseOffset, owned) = Stage(data);

        var copies = new VkBufferImageCopy[regions.Length];
        for (var i = 0; i < regions.Length; i++)
        {
            copies[i] = regions[i];
            copies[i].bufferOffset += baseOffset;
        }

        pendingImages.Add(new PendingImageUpload
        {
            Image = image,
            Source = source,
            Regions = copies,
            GenerateMips = generateMips && image.MipLevels > 1,
            OwnedStaging = owned,
        });
    }

    private (VkBuffer Source, ulong BaseOffset, GpuBuffer Owned) Stage(ReadOnlySpan<byte> data)
    {
        var size = (ulong)data.Length;
        var aligned = (ringOffset + Alignment - 1) / Alignment * Alignment;
        if (frameOpen && aligned + size <= RingSize)
        {
            var ring = rings[slot];
            data.CopyTo(new Span<byte>(ring.Mapped + aligned, data.Length));
            ringOffset = aligned + size;
            return (ring.Handle, aligned, null);
        }

        var owned = GpuBuffer.Create(context, size, VkBufferUsageFlags.TransferSrc, hostVisible: true);
        data.CopyTo(new Span<byte>(owned.Mapped, data.Length));
        return (owned.Handle, 0, owned);
    }

    // Records the queued transfers and submits them to the copy engine. Returns the timeline
    // value the graphics submission must wait on, or 0 when the copies were recorded directly
    // into the graphics command buffer.
    public ulong Submit(VkCommandBuffer graphicsCommands)
    {
        frameOpen = false;
        if (pendingImages.Count == 0 && pendingBuffers.Count == 0)
            return 0;

        if (!dedicated)
        {
            Record(graphicsCommands);
            ReleaseStaging();
            Clear();
            return 0;
        }

        var api = context.Device.Api;
        var commands = commandBuffers[slot];
        var begin = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        api.vkBeginCommandBuffer(commands, &begin).CheckResult("vkBeginCommandBuffer (transfer)");
        Record(commands);
        api.vkEndCommandBuffer(commands).CheckResult("vkEndCommandBuffer (transfer)");

        var value = ++submittedValue;
        var signal = new VkSemaphoreSubmitInfo
        {
            semaphore = timeline,
            value = value,
            stageMask = VkPipelineStageFlags2.AllTransfer,
        };
        var bufferInfo = new VkCommandBufferSubmitInfo { commandBuffer = commands };
        var submit = new VkSubmitInfo2
        {
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &bufferInfo,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signal,
        };

        lock (context.Device.QueueLock)
        {
            var result = api.vkQueueSubmit2(context.Device.TransferQueue, 1, &submit, VkFence.Null);
            if (result == VkResult.ErrorDeviceLost)
                context.Device.LogDeviceFault();
            result.CheckResult("vkQueueSubmit2 (transfer)");
        }

        ReleaseStaging();
        Clear();
        return value;
    }

    private void Record(VkCommandBuffer commands)
    {
        RecordBuffers(commands);
        RecordImages(commands);
    }

    private void RecordBuffers(VkCommandBuffer commands)
    {
        if (pendingBuffers.Count == 0)
            return;

        var api = context.Device.Api;
        foreach (var upload in pendingBuffers)
        {
            fixed (VkBufferCopy* regions = upload.Regions)
                api.vkCmdCopyBuffer(commands, upload.Source, upload.Destination.Handle, (uint)upload.Regions.Length, regions);
        }

        var barriers = new VkBufferMemoryBarrier2[pendingBuffers.Count];
        for (var i = 0; i < pendingBuffers.Count; i++)
        {
            barriers[i] = new VkBufferMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.Copy,
                srcAccessMask = VkAccessFlags2.TransferWrite,
                dstStageMask = dedicated ? VkPipelineStageFlags2.AllTransfer : VkPipelineStageFlags2.AllGraphics,
                dstAccessMask = dedicated
                    ? VkAccessFlags2.None
                    : VkAccessFlags2.ShaderRead | VkAccessFlags2.VertexAttributeRead |
                      VkAccessFlags2.IndexRead | VkAccessFlags2.UniformRead,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                buffer = pendingBuffers[i].Destination.Handle,
                offset = 0,
                size = VK_WHOLE_SIZE,
            };
        }

        fixed (VkBufferMemoryBarrier2* p = barriers)
        {
            var dependency = new VkDependencyInfo
            {
                bufferMemoryBarrierCount = (uint)barriers.Length,
                pBufferMemoryBarriers = p,
            };
            api.vkCmdPipelineBarrier2(commands, &dependency);
        }
    }

    private void RecordImages(VkCommandBuffer commands)
    {
        if (pendingImages.Count == 0)
            return;

        var api = context.Device.Api;
        var barriers = new VkImageMemoryBarrier2[pendingImages.Count];

        for (var i = 0; i < pendingImages.Count; i++)
        {
            barriers[i] = new VkImageMemoryBarrier2
            {
                srcStageMask = VkPipelineStageFlags2.None,
                srcAccessMask = VkAccessFlags2.None,
                dstStageMask = VkPipelineStageFlags2.Copy,
                dstAccessMask = VkAccessFlags2.TransferWrite,
                oldLayout = VkImageLayout.Undefined,
                newLayout = VkImageLayout.TransferDstOptimal,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                image = pendingImages[i].Image.Handle,
                subresourceRange = pendingImages[i].Image.FullRange,
            };
        }

        Submit(api, commands, barriers);

        foreach (var upload in pendingImages)
        {
            fixed (VkBufferImageCopy* regions = upload.Regions)
                api.vkCmdCopyBufferToImage(commands, upload.Source, upload.Image.Handle, VkImageLayout.TransferDstOptimal, (uint)upload.Regions.Length, regions);
        }

        var post = 0;
        for (var i = 0; i < pendingImages.Count; i++)
        {
            if (pendingImages[i].GenerateMips)
            {
                MipGenerator.Record(api, commands, pendingImages[i].Image, VkImageLayout.TransferDstOptimal,
                    VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite, transferQueue: dedicated);
                continue;
            }

            barriers[post] = barriers[i];
            ref var barrier = ref barriers[post++];
            barrier.srcStageMask = VkPipelineStageFlags2.Copy;
            barrier.srcAccessMask = VkAccessFlags2.TransferWrite;
            barrier.dstStageMask = dedicated
                ? VkPipelineStageFlags2.AllTransfer
                : VkPipelineStageFlags2.AllGraphics;
            barrier.dstAccessMask = dedicated ? VkAccessFlags2.None : VkAccessFlags2.ShaderSampledRead;
            barrier.oldLayout = VkImageLayout.TransferDstOptimal;
            barrier.newLayout = VkImageLayout.ShaderReadOnlyOptimal;
        }

        if (post != 0)
            Submit(api, commands, barriers.AsSpan(0, post));
    }

    private static void Submit(VkDeviceApi api, VkCommandBuffer commands, Span<VkImageMemoryBarrier2> barriers)
    {
        fixed (VkImageMemoryBarrier2* p = barriers)
        {
            var dependency = new VkDependencyInfo
            {
                imageMemoryBarrierCount = (uint)barriers.Length,
                pImageMemoryBarriers = p,
            };
            api.vkCmdPipelineBarrier2(commands, &dependency);
        }
    }

    private void ReleaseStaging()
    {
        foreach (var upload in pendingImages)
            upload.OwnedStaging?.Dispose();
        foreach (var upload in pendingBuffers)
            upload.OwnedStaging?.Dispose();
    }

    private void Clear()
    {
        pendingImages.Clear();
        pendingBuffers.Clear();
    }

    public void Dispose()
    {
        foreach (var ring in rings)
            ring?.Dispose();

        ReleaseStaging();
        Clear();

        if (!dedicated)
            return;

        var api = context.Device.Api;
        lock (context.Device.QueueLock)
            api.vkQueueWaitIdle(context.Device.TransferQueue).CheckResult("vkQueueWaitIdle (transfer)");

        if (!timeline.IsNull)
            api.vkDestroySemaphore(timeline, null);
        for (var i = 0; i < pools.Length; i++)
        {
            if (!pools[i].IsNull)
                api.vkDestroyCommandPool(pools[i], null);
        }
    }
}
