using System;
using System.Collections.Generic;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Copies CPU data into images. Data is written into staging memory right away; the copy commands are recorded
// at the start of the next frame's command buffer, so the uploaded images are ready for everything rendered in
// that frame. Render thread only.
//
// Staging: inside an open frame (BeginFrame .. Record) the frame slot's ring is used; its previous contents are
// known to be consumed because the slot's fence was waited on. Outside a frame (state change messages processed
// while no frame is drawn) a dedicated buffer is used, released once the copies are recorded, so it lives exactly
// as long as the frame that reads it.
internal sealed unsafe class UploadQueue : IDisposable
{
    private const ulong RingSize = 32ul << 20;
    private const ulong Alignment = 16; // Covers every texel block size

    private sealed class PendingImageUpload
    {
        public GpuImage Image;
        public VkBuffer Source;
        public VkBufferImageCopy[] Regions;
        public bool GenerateMips;
        public GpuBuffer OwnedStaging;
    }

    private sealed class PendingBufferUpload
    {
        public VkBuffer Source;
        public GpuBuffer Destination;
        public VkBufferCopy[] Regions;
        public GpuBuffer OwnedStaging;
    }

    private readonly GpuContext context;
    private readonly GpuBuffer[] rings = new GpuBuffer[FrameLoop.FramesInFlight];
    private readonly List<PendingImageUpload> pending = new();
    private readonly List<PendingBufferUpload> pendingBuffers = new();
    private int slot;
    private ulong ringOffset;
    private bool frameOpen;

    public UploadQueue(GpuContext context)
    {
        this.context = context;
        for (var i = 0; i < rings.Length; i++)
            rings[i] = GpuBuffer.Create(context, RingSize, VkBufferUsageFlags.TransferSrc, hostVisible: true);
    }

    public bool HasPending => pending.Count != 0 || pendingBuffers.Count != 0;

    // The ring of this slot is free again once its fence was waited on
    public void BeginFrame(int frameSlot)
    {
        slot = frameSlot;
        ringOffset = 0;
        frameOpen = true;
    }

    // Queues a buffer copy; the destination is visible to shaders after the next Record.
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

    // Writes data into this frame's staging ring (or a one-off staging buffer when no frame is open or it is full)
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

        // Outside a frame, or too large for what is left of the ring: a one-off staging buffer
        var owned = GpuBuffer.Create(context, size, VkBufferUsageFlags.TransferSrc, hostVisible: true);
        data.CopyTo(new Span<byte>(owned.Mapped, data.Length));
        return (owned.Handle, 0, owned);
    }

    // Queues the upload of every subresource of the image. Region buffer offsets are relative to the start of data.
    // generateMips: the regions only cover mip level 0; the other levels are filled from it after the copy.
    // The image ends up in ShaderReadOnlyOptimal layout.
    public void UploadImage(GpuImage image, ReadOnlySpan<byte> data, VkBufferImageCopy[] regions, bool generateMips = false)
    {
        var (source, baseOffset, owned) = Stage(data);

        var copies = new VkBufferImageCopy[regions.Length];
        for (var i = 0; i < regions.Length; i++)
        {
            copies[i] = regions[i];
            copies[i].bufferOffset += baseOffset;
        }

        pending.Add(new PendingImageUpload
        {
            Image = image,
            Source = source,
            Regions = copies,
            GenerateMips = generateMips && image.MipLevels > 1,
            OwnedStaging = owned,
        });
    }

    // Records all queued copies, with the layout transitions around them
    public void Record(VkCommandBuffer commands)
    {
        frameOpen = false;
        if (pending.Count == 0 && pendingBuffers.Count == 0)
            return;

        var api = context.Device.Api;
        RecordBuffers(api, commands);
        if (pending.Count == 0)
            return;

        var barriers = new VkImageMemoryBarrier2[pending.Count];

        for (var i = 0; i < pending.Count; i++)
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
                image = pending[i].Image.Handle,
                subresourceRange = pending[i].Image.FullRange,
            };
        }

        PipelineBarrier(api, commands, barriers);

        foreach (var upload in pending)
        {
            fixed (VkBufferImageCopy* regions = upload.Regions)
                api.vkCmdCopyBufferToImage(commands, upload.Source, upload.Image.Handle, VkImageLayout.TransferDstOptimal, (uint)upload.Regions.Length, regions);
        }

        var post = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            if (pending[i].GenerateMips)
            {
                MipGenerator.Record(api, commands, pending[i].Image, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferWrite);
                continue;
            }

            barriers[post] = barriers[i];
            ref var barrier = ref barriers[post++];
            barrier.srcStageMask = VkPipelineStageFlags2.Copy;
            barrier.srcAccessMask = VkAccessFlags2.TransferWrite;
            barrier.dstStageMask = VkPipelineStageFlags2.AllGraphics | VkPipelineStageFlags2.ComputeShader;
            barrier.dstAccessMask = VkAccessFlags2.ShaderSampledRead;
            barrier.oldLayout = VkImageLayout.TransferDstOptimal;
            barrier.newLayout = VkImageLayout.ShaderReadOnlyOptimal;
        }

        if (post != 0)
            PipelineBarrier(api, commands, barriers.AsSpan(0, post).ToArray());

        // Released with this frame: destroyed once the slot's fence shows the copies are done
        foreach (var upload in pending)
            upload.OwnedStaging?.Dispose();

        pending.Clear();
    }

    // Copies vertex/index and other buffers, then makes them readable by every graphics stage
    private void RecordBuffers(VkDeviceApi api, VkCommandBuffer commands)
    {
        if (pendingBuffers.Count == 0)
            return;

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
                dstStageMask = VkPipelineStageFlags2.AllGraphics,
                dstAccessMask = VkAccessFlags2.ShaderRead
                               | VkAccessFlags2.VertexAttributeRead
                               | VkAccessFlags2.IndexRead
                               | VkAccessFlags2.UniformRead,
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

        foreach (var upload in pendingBuffers)
            upload.OwnedStaging?.Dispose();

        pendingBuffers.Clear();
    }

    private static void PipelineBarrier(VkDeviceApi api, VkCommandBuffer commands, VkImageMemoryBarrier2[] barriers)
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

    public void Dispose()
    {
        foreach (var ring in rings)
            ring?.Dispose();
        foreach (var upload in pending)
            upload.OwnedStaging?.Dispose();
        pending.Clear();
        foreach (var upload in pendingBuffers)
            upload.OwnedStaging?.Dispose();
        pendingBuffers.Clear();
    }
}
