using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

// An optimally tiled image with a view covering all of it
internal sealed unsafe class GpuImage : IDisposable
{
    private readonly GpuContext context;

    public VkImage Handle { get; private set; }
    public VkImageView View { get; private set; }
    public GpuAllocation Allocation { get; private set; }
    public VkFormat Format { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint MipLevels { get; }
    public uint ArrayLayers { get; }
    public bool IsCube { get; }

    public VkImageSubresourceRange FullRange => new(AspectOf(Format), 0, MipLevels, 0, ArrayLayers);

    private GpuImage(GpuContext context, VkImage handle, VkImageView view, GpuAllocation allocation,
        VkFormat format, uint width, uint height, uint mipLevels, uint arrayLayers, bool isCube)
    {
        this.context = context;
        Handle = handle;
        View = view;
        Allocation = allocation;
        Format = format;
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        ArrayLayers = arrayLayers;
        IsCube = isCube;
    }

    public static GpuImage Create2D(GpuContext context, uint width, uint height, uint mipLevels, uint arrayLayers,
        VkFormat format, VkImageUsageFlags usage, bool isCube = false)
    {
        var api = context.Device.Api;

        var createInfo = new VkImageCreateInfo
        {
            flags = isCube ? VkImageCreateFlags.CubeCompatible : VkImageCreateFlags.None,
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D(width, height, 1),
            mipLevels = mipLevels,
            arrayLayers = arrayLayers,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };

        VkImage image;
        api.vkCreateImage(&createInfo, null, &image).CheckResult("vkCreateImage");

        VkMemoryRequirements requirements;
        api.vkGetImageMemoryRequirements(image, &requirements);
        var allocation = context.Memory.Allocate(requirements, VkMemoryPropertyFlags.DeviceLocal, VkMemoryPropertyFlags.None, linear: false);
        api.vkBindImageMemory(image, allocation.Memory, allocation.Offset).CheckResult("vkBindImageMemory");

        var viewType = isCube
            ? arrayLayers > 6 ? VkImageViewType.ImageCubeArray : VkImageViewType.ImageCube
            : arrayLayers > 1 ? VkImageViewType.Image2DArray : VkImageViewType.Image2D;

        var viewInfo = new VkImageViewCreateInfo
        {
            image = image,
            viewType = viewType,
            format = format,
            subresourceRange = new VkImageSubresourceRange(AspectOf(format), 0, mipLevels, 0, arrayLayers),
        };

        VkImageView view;
        api.vkCreateImageView(&viewInfo, null, &view).CheckResult("vkCreateImageView");

        return new GpuImage(context, image, view, allocation, format, width, height, mipLevels, arrayLayers, isCube);
    }

    public static VkImageAspectFlags AspectOf(VkFormat format) => format switch
    {
        VkFormat.D16Unorm or VkFormat.D32Sfloat or VkFormat.X8D24UnormPack32 => VkImageAspectFlags.Depth,
        VkFormat.D16UnormS8Uint or VkFormat.D24UnormS8Uint or VkFormat.D32SfloatS8Uint => VkImageAspectFlags.Depth | VkImageAspectFlags.Stencil,
        _ => VkImageAspectFlags.Color,
    };

    public void Dispose()
    {
        if (Handle.IsNull)
            return;

        var handle = Handle;
        var view = View;
        var allocation = Allocation;
        var device = context.Device;
        var memory = context.Memory;
        context.Release(() =>
        {
            device.Api.vkDestroyImageView(view, null);
            device.Api.vkDestroyImage(handle, null);
            memory.Free(allocation);
        });

        Handle = VkImage.Null;
        View = VkImageView.Null;
        Allocation = default;
    }
}
