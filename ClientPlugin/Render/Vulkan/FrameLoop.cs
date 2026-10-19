using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Frames in flight: per-frame command buffer, fence and acquire semaphore, swapchain image
// acquisition, submission and presentation. IMyRender splits a frame into Draw and Present,
// so recording/submission and presentation are separate calls here too.
internal sealed unsafe class FrameLoop : IDisposable
{
    public const int FramesInFlight = 2;

    private struct Frame
    {
        public VkCommandPool Pool;
        public VkCommandBuffer Commands;
        public VkFence Fence;
        public VkSemaphore ImageAvailable;
    }

    private readonly VulkanDevice device;
    private readonly VulkanSwapchain swapchain;
    private readonly Frame[] frames = new Frame[FramesInFlight];

    private int frameIndex;
    private uint imageIndex;
    private bool recording;
    private bool submitted;

    // Set when the swapchain no longer matches the surface (resize, mode change)
    public bool SwapchainOutdated { get; private set; }

    public VkCommandBuffer Commands => frames[frameIndex].Commands;
    public int FrameSlot => frameIndex;
    public VkImage BackbufferImage => swapchain.Images[imageIndex];
    public VkImageView BackbufferView => swapchain.Views[imageIndex];
    public VkExtent2D BackbufferExtent => swapchain.Extent;
    public ulong FrameNumber { get; private set; }

    public FrameLoop(VulkanDevice device, VulkanSwapchain swapchain)
    {
        this.device = device;
        this.swapchain = swapchain;

        for (var i = 0; i < FramesInFlight; i++)
        {
            var poolInfo = new VkCommandPoolCreateInfo
            {
                flags = VkCommandPoolCreateFlags.Transient,
                queueFamilyIndex = device.QueueFamily,
            };
            device.Api.vkCreateCommandPool(&poolInfo, null, out frames[i].Pool).CheckResult("vkCreateCommandPool");

            var allocInfo = new VkCommandBufferAllocateInfo
            {
                commandPool = frames[i].Pool,
                level = VkCommandBufferLevel.Primary,
                commandBufferCount = 1,
            };
            VkCommandBuffer commands;
            device.Api.vkAllocateCommandBuffers(&allocInfo, &commands).CheckResult("vkAllocateCommandBuffers");
            frames[i].Commands = commands;

            var fenceInfo = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
            device.Api.vkCreateFence(&fenceInfo, null, out frames[i].Fence).CheckResult("vkCreateFence");

            var semaphoreInfo = new VkSemaphoreCreateInfo();
            device.Api.vkCreateSemaphore(&semaphoreInfo, null, out frames[i].ImageAvailable).CheckResult("vkCreateSemaphore");
        }
    }

    // Waits for the frame slot, acquires a backbuffer image and starts recording.
    // Returns false if no image is available this frame (swapchain outdated or minimized).
    public bool TryBeginFrame()
    {
        if (swapchain.Handle.IsNull || SwapchainOutdated)
            return false;

        ref var frame = ref frames[frameIndex];
        var fence = frame.Fence;
        device.Api.vkWaitForFences(1, &fence, true, ulong.MaxValue).CheckResult("vkWaitForFences");

        uint index;
        var result = device.Api.vkAcquireNextImageKHR(swapchain.Handle, ulong.MaxValue, frame.ImageAvailable, VkFence.Null, &index);
        if (result == VkResult.ErrorOutOfDateKHR)
        {
            SwapchainOutdated = true;
            return false;
        }

        if (result == VkResult.SuboptimalKHR)
            SwapchainOutdated = true; // Still usable for this frame, recreate afterwards
        else
            result.CheckResult("vkAcquireNextImageKHR");

        imageIndex = index;
        device.Api.vkResetFences(1, &fence).CheckResult("vkResetFences");
        device.Api.vkResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None).CheckResult("vkResetCommandPool");

        var beginInfo = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        device.Api.vkBeginCommandBuffer(frame.Commands, &beginInfo).CheckResult("vkBeginCommandBuffer");

        recording = true;
        return true;
    }

    // Ends recording and submits. The backbuffer must already be in PresentSrcKHR layout.
    // transferValue > 0 makes the graphics queue wait for that timeline value, so uploads
    // recorded on the transfer queue are visible before any command in this frame runs.
    public void EndFrame(ulong transferValue = 0, VkSemaphore transferTimeline = default)
    {
        if (!recording)
            return;

        recording = false;
        ref var frame = ref frames[frameIndex];
        device.Api.vkEndCommandBuffer(frame.Commands).CheckResult("vkEndCommandBuffer");

        var waits = stackalloc VkSemaphoreSubmitInfo[2];
        waits[0] = new VkSemaphoreSubmitInfo
        {
            semaphore = frame.ImageAvailable,
            stageMask = VkPipelineStageFlags2.ColorAttachmentOutput | VkPipelineStageFlags2.Transfer,
        };

        var waitCount = 1u;
        if (transferValue > 0)
        {
            waits[1] = new VkSemaphoreSubmitInfo
            {
                semaphore = transferTimeline,
                value = transferValue,
                stageMask = VkPipelineStageFlags2.AllCommands,
            };
            waitCount = 2;
        }

        var signal = new VkSemaphoreSubmitInfo
        {
            semaphore = swapchain.RenderFinished[imageIndex],
            stageMask = VkPipelineStageFlags2.AllCommands,
        };
        var commands = new VkCommandBufferSubmitInfo { commandBuffer = frame.Commands };
        var submit = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = waitCount,
            pWaitSemaphoreInfos = waits,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &commands,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signal,
        };

        lock (device.QueueLock)
            device.Api.vkQueueSubmit2(device.Queue, 1, &submit, frame.Fence).CheckResult("vkQueueSubmit2");

        submitted = true;
    }

    public void Present()
    {
        if (!submitted)
            return;

        submitted = false;
        var swapchainHandle = swapchain.Handle;
        var waitSemaphore = swapchain.RenderFinished[imageIndex];
        var index = imageIndex;
        var presentInfo = new VkPresentInfoKHR
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &waitSemaphore,
            swapchainCount = 1,
            pSwapchains = &swapchainHandle,
            pImageIndices = &index,
        };

        VkResult result;
        lock (device.QueueLock)
            result = device.Api.vkQueuePresentKHR(device.Queue, &presentInfo);

        if (result == VkResult.ErrorOutOfDateKHR || result == VkResult.SuboptimalKHR)
            SwapchainOutdated = true;
        else
            result.CheckResult("vkQueuePresentKHR");

        frameIndex = (frameIndex + 1) % FramesInFlight;
        FrameNumber++;
    }

    // Call after the swapchain was recreated
    public void OnSwapchainRecreated()
    {
        SwapchainOutdated = false;
    }

    // Transitions the acquired backbuffer to TransferDst, clears it, then transitions it to PresentSrcKHR
    public void ClearBackbuffer(VkClearColorValue color)
    {
        var range = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);

        Barrier(BackbufferImage, range,
            VkPipelineStageFlags2.None, VkAccessFlags2.None, VkImageLayout.Undefined,
            VkPipelineStageFlags2.Clear, VkAccessFlags2.TransferWrite, VkImageLayout.TransferDstOptimal);

        device.Api.vkCmdClearColorImage(Commands, BackbufferImage, VkImageLayout.TransferDstOptimal, &color, 1, &range);

        Barrier(BackbufferImage, range,
            VkPipelineStageFlags2.Clear, VkAccessFlags2.TransferWrite, VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags2.None, VkAccessFlags2.None, VkImageLayout.PresentSrcKHR);
    }

    public void Barrier(
        VkImage image, VkImageSubresourceRange range,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkImageLayout oldLayout,
        VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess, VkImageLayout newLayout)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = range,
        };
        var dependency = new VkDependencyInfo
        {
            imageMemoryBarrierCount = 1,
            pImageMemoryBarriers = &barrier,
        };
        device.Api.vkCmdPipelineBarrier2(Commands, &dependency);
    }

    public void Dispose()
    {
        device.WaitIdle();
        foreach (var frame in frames)
        {
            if (frame.Pool.IsNotNull)
                device.Api.vkDestroyCommandPool(frame.Pool, null);
            if (frame.Fence.IsNotNull)
                device.Api.vkDestroyFence(frame.Fence, null);
            if (frame.ImageAvailable.IsNotNull)
                device.Api.vkDestroySemaphore(frame.ImageAvailable, null);
        }
    }
}
