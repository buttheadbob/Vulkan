using System;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// Render targets of the 3D scene: HDR color and reverse-Z depth. Kept separate from the swapchain so the scene
// can later render at a lower internal resolution (FSR) and go through post-processing.
internal sealed class SceneTargets : IDisposable
{
    public const VkFormat ColorFormat = VkFormat.R16G16B16A16Sfloat;
    public const VkFormat DepthFormat = VkFormat.D32Sfloat;
    public const VkFormat OitAccumFormat = VkFormat.R16G16B16A16Sfloat;
    public const VkFormat OitRevealFormat = VkFormat.R16Sfloat;

    private readonly GpuContext context;

    public GpuImage Color { get; private set; }
    public GpuImage Depth { get; private set; }
    public GpuImage OitAccum { get; private set; }
    public GpuImage OitReveal { get; private set; }
    public uint ColorSlot { get; private set; }
    public uint DepthSlot { get; private set; }
    public uint OitAccumSlot { get; private set; }
    public uint OitRevealSlot { get; private set; }
    public VkExtent2D Extent { get; private set; }

    // Layouts the targets were left in by the last frame (Undefined after creation)
    public VkImageLayout ColorLayout = VkImageLayout.Undefined;
    public VkImageLayout DepthLayout = VkImageLayout.Undefined;
    public VkImageLayout OitAccumLayout = VkImageLayout.Undefined;
    public VkImageLayout OitRevealLayout = VkImageLayout.Undefined;

    public SceneTargets(GpuContext context)
    {
        this.context = context;
    }

    // (Re)creates the targets if the size changed. Old images and slots are released once no frame uses them.
    public void EnsureSize(uint width, uint height)
    {
        if (Color != null && Extent.width == width && Extent.height == height)
            return;

        Release();

        Extent = new VkExtent2D(width, height);
        Color = GpuImage.Create2D(context, width, height, 1, 1, ColorFormat,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.Storage | VkImageUsageFlags.TransferSrc);
        Depth = GpuImage.Create2D(context, width, height, 1, 1, DepthFormat,
            VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled);
        OitAccum = GpuImage.Create2D(context, width, height, 1, 1, OitAccumFormat,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);
        OitReveal = GpuImage.Create2D(context, width, height, 1, 1, OitRevealFormat,
            VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled);

        ColorSlot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(ColorSlot, Color.View);
        DepthSlot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(DepthSlot, Depth.View);
        OitAccumSlot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(OitAccumSlot, OitAccum.View);
        OitRevealSlot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(OitRevealSlot, OitReveal.View);

        ColorLayout = VkImageLayout.Undefined;
        DepthLayout = VkImageLayout.Undefined;
        OitAccumLayout = VkImageLayout.Undefined;
        OitRevealLayout = VkImageLayout.Undefined;
    }

    private void Release()
    {
        if (Color == null)
            return;

        Color.Dispose();
        Depth.Dispose();
        OitAccum.Dispose();
        OitReveal.Dispose();
        context.Bindless.FreeTextureSlot(ColorSlot);
        context.Bindless.FreeTextureSlot(DepthSlot);
        context.Bindless.FreeTextureSlot(OitAccumSlot);
        context.Bindless.FreeTextureSlot(OitRevealSlot);
        Color = null;
        Depth = null;
        OitAccum = null;
        OitReveal = null;
    }

    public void Dispose() => Release();
}
