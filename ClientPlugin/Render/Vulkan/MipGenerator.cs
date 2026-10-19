using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// Fills mip levels 1..n of a color image from level 0 with a chain of linear blits.
// All layers are processed. The whole image ends in ShaderReadOnlyOptimal.
internal static unsafe class MipGenerator
{
    // mip0Layout/Stage/Access: how level 0 was last written; the other levels' contents are discarded
    public static void Record(VkDeviceApi api, VkCommandBuffer commands, GpuImage image,
        VkImageLayout mip0Layout, VkPipelineStageFlags2 mip0Stage, VkAccessFlags2 mip0Access)
    {
        var layers = image.ArrayLayers;
        var barriers = stackalloc VkImageMemoryBarrier2[2];

        // Level 0 becomes the first blit source, the rest blit destinations
        barriers[0] = Barrier(image, 0, 1, mip0Stage, mip0Access, mip0Layout,
            VkPipelineStageFlags2.Blit, VkAccessFlags2.TransferRead, VkImageLayout.TransferSrcOptimal);
        var count = 1u;
        if (image.MipLevels > 1)
        {
            barriers[1] = Barrier(image, 1, image.MipLevels - 1, VkPipelineStageFlags2.None, VkAccessFlags2.None, VkImageLayout.Undefined,
                VkPipelineStageFlags2.Blit, VkAccessFlags2.TransferWrite, VkImageLayout.TransferDstOptimal);
            count = 2;
        }

        Submit(api, commands, barriers, count);

        for (var level = 1u; level < image.MipLevels; level++)
        {
            var srcWidth = Math.Max(1, (int)(image.Width >> (int)(level - 1)));
            var srcHeight = Math.Max(1, (int)(image.Height >> (int)(level - 1)));
            var dstWidth = Math.Max(1, (int)(image.Width >> (int)level));
            var dstHeight = Math.Max(1, (int)(image.Height >> (int)level));

            var blit = new VkImageBlit
            {
                srcSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, level - 1, 0, layers),
                dstSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, level, 0, layers),
            };
            blit.srcOffsets[1] = new VkOffset3D(srcWidth, srcHeight, 1);
            blit.dstOffsets[1] = new VkOffset3D(dstWidth, dstHeight, 1);
            api.vkCmdBlitImage(commands, image.Handle, VkImageLayout.TransferSrcOptimal, image.Handle, VkImageLayout.TransferDstOptimal,
                1, &blit, VkFilter.Linear);

            // This level is the source of the next blit
            barriers[0] = Barrier(image, level, 1, VkPipelineStageFlags2.Blit, VkAccessFlags2.TransferWrite, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags2.Blit, VkAccessFlags2.TransferRead, VkImageLayout.TransferSrcOptimal);
            Submit(api, commands, barriers, 1);
        }

        barriers[0] = Barrier(image, 0, image.MipLevels, VkPipelineStageFlags2.Blit, VkAccessFlags2.TransferRead, VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags2.AllCommands, VkAccessFlags2.ShaderSampledRead, VkImageLayout.ShaderReadOnlyOptimal);
        Submit(api, commands, barriers, 1);
    }

    private static VkImageMemoryBarrier2 Barrier(GpuImage image, uint baseMip, uint mipCount,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkImageLayout oldLayout,
        VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess, VkImageLayout newLayout) => new()
    {
        srcStageMask = srcStage,
        srcAccessMask = srcAccess,
        dstStageMask = dstStage,
        dstAccessMask = dstAccess,
        oldLayout = oldLayout,
        newLayout = newLayout,
        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        image = image.Handle,
        subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, baseMip, mipCount, 0, image.ArrayLayers),
    };

    private static void Submit(VkDeviceApi api, VkCommandBuffer commands, VkImageMemoryBarrier2* barriers, uint count)
    {
        var dependency = new VkDependencyInfo { imageMemoryBarrierCount = count, pImageMemoryBarriers = barriers };
        api.vkCmdPipelineBarrier2(commands, &dependency);
    }
}
