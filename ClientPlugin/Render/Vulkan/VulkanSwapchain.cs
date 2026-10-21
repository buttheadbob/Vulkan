using System;
using System.Linq;
using Vortice.Vulkan;
using VRageMath;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Vulkan;

internal sealed unsafe class VulkanSwapchain : IDisposable
{
    private readonly VulkanInstance instance;
    private readonly VulkanDevice device;
    private readonly VkSurfaceKHR surface;

    public VkSwapchainKHR Handle { get; private set; }
    public VkFormat Format { get; private set; }
    public VkExtent2D Extent { get; private set; }
    public VkImage[] Images { get; private set; } = [];
    public VkImageView[] Views { get; private set; } = [];

    // One per swapchain image: a present may still be waiting on it when its frame-in-flight slot is reused
    public VkSemaphore[] RenderFinished { get; private set; } = [];

    public VulkanSwapchain(VulkanInstance instance, VulkanDevice device, VkSurfaceKHR surface)
    {
        this.instance = instance;
        this.device = device;
        this.surface = surface;
    }

    // Creates or recreates the swapchain. The requested size is used when the window system leaves it to us
    // (Wayland), otherwise the surface's current extent wins.
    public void Recreate(Vector2I requestedSize, bool vsync)
    {
        device.WaitIdle();

        VkSurfaceCapabilitiesKHR capabilities;
        instance.Api.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device.PhysicalDevice, surface, &capabilities).CheckResult("vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        var extent = capabilities.currentExtent;
        if (extent.width == uint.MaxValue)
        {
            extent = new VkExtent2D(
                Math.Clamp((uint)Math.Max(requestedSize.X, 1), capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
                Math.Clamp((uint)Math.Max(requestedSize.Y, 1), capabilities.minImageExtent.height, capabilities.maxImageExtent.height));
        }

        if (extent.width == 0 || extent.height == 0)
            return; // Minimized, keep the old swapchain until the window has a size again

        var (format, colorSpace) = ChooseFormat();
        var presentMode = ChoosePresentMode(vsync);

        var imageCount = Math.Max(capabilities.minImageCount, 3u);
        if (capabilities.maxImageCount != 0)
            imageCount = Math.Min(imageCount, capabilities.maxImageCount);

        var oldSwapchain = Handle;
        var createInfo = new VkSwapchainCreateInfoKHR
        {
            surface = surface,
            minImageCount = imageCount,
            imageFormat = format,
            imageColorSpace = colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = presentMode,
            clipped = true,
            oldSwapchain = oldSwapchain,
        };

        VkSwapchainKHR swapchain;
        device.Api.vkCreateSwapchainKHR(&createInfo, null, &swapchain).CheckResult("vkCreateSwapchainKHR");

        DestroyImageResources();
        if (oldSwapchain.IsNotNull)
            device.Api.vkDestroySwapchainKHR(oldSwapchain, null);

        Handle = swapchain;
        Format = format;
        Extent = extent;
        CreateImageResources();

    }

    private (VkFormat, VkColorSpaceKHR) ChooseFormat()
    {
        uint count;
        instance.Api.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, &count, null).CheckResult("vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new VkSurfaceFormatKHR[count];
        fixed (VkSurfaceFormatKHR* p = formats)
            instance.Api.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, &count, p).CheckResult("vkGetPhysicalDeviceSurfaceFormatsKHR");

        // sRGB: rendering and blending happen in linear space and are encoded on write, like the stock
        // renderer's sRGB backbuffer views. HDR output will add other formats later.
        VkFormat[] preferred = [VkFormat.B8G8R8A8Srgb, VkFormat.R8G8B8A8Srgb];
        foreach (var wanted in preferred)
        {
            var match = formats.FirstOrDefault(f => f.format == wanted && f.colorSpace == VkColorSpaceKHR.SrgbNonLinear);
            if (match.format == wanted)
                return (match.format, match.colorSpace);
        }

        return (formats[0].format, formats[0].colorSpace);
    }

    private VkPresentModeKHR ChoosePresentMode(bool vsync)
    {
        if (vsync)
            return VkPresentModeKHR.Fifo; // Always supported

        uint count;
        instance.Api.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, &count, null).CheckResult("vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new VkPresentModeKHR[count];
        fixed (VkPresentModeKHR* p = modes)
            instance.Api.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, &count, p).CheckResult("vkGetPhysicalDeviceSurfacePresentModesKHR");

        if (modes.Contains(VkPresentModeKHR.Mailbox))
            return VkPresentModeKHR.Mailbox;
        if (modes.Contains(VkPresentModeKHR.Immediate))
            return VkPresentModeKHR.Immediate;
        return VkPresentModeKHR.Fifo;
    }

    private void CreateImageResources()
    {
        uint count;
        device.Api.vkGetSwapchainImagesKHR(Handle, &count, null).CheckResult("vkGetSwapchainImagesKHR");
        var images = new VkImage[count];
        fixed (VkImage* p = images)
            device.Api.vkGetSwapchainImagesKHR(Handle, &count, p).CheckResult("vkGetSwapchainImagesKHR");

        var views = new VkImageView[count];
        var semaphores = new VkSemaphore[count];
        for (var i = 0; i < count; i++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                image = images[i],
                viewType = VkImageViewType.Image2D,
                format = Format,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            device.Api.vkCreateImageView(&viewInfo, null, out views[i]).CheckResult("vkCreateImageView");

            var semaphoreInfo = new VkSemaphoreCreateInfo();
            device.Api.vkCreateSemaphore(&semaphoreInfo, null, out semaphores[i]).CheckResult("vkCreateSemaphore");
        }

        Images = images;
        Views = views;
        RenderFinished = semaphores;
    }

    private void DestroyImageResources()
    {
        foreach (var view in Views)
            device.Api.vkDestroyImageView(view, null);
        foreach (var semaphore in RenderFinished)
            device.Api.vkDestroySemaphore(semaphore, null);

        Images = [];
        Views = [];
        RenderFinished = [];
    }

    public void Dispose()
    {
        if (Handle.IsNull)
            return;

        device.WaitIdle();
        DestroyImageResources();
        device.Api.vkDestroySwapchainKHR(Handle, null);
        Handle = VkSwapchainKHR.Null;
    }
}
