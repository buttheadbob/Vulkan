using System;
using System.Diagnostics;
using System.IO;
using ClientPlugin.Render.Vulkan;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.Vulkan;
using VRage.Render.Image;
using VRageMath;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render;

// Development aid: saves one presented frame as PNG, so rendering can be checked without looking at the screen.
// Enabled by environment variables: VULKAN_SCREENSHOT=<file.png> [VULKAN_SCREENSHOT_DELAY=<seconds>, default 20]
internal sealed unsafe class FrameCapture : IDisposable
{
    private readonly GpuContext context;
    private readonly string path;
    private readonly double delaySeconds;
    private readonly Stopwatch time = Stopwatch.StartNew();
    private GpuBuffer readback;
    private VkExtent2D extent;
    private VkFormat format;
    private bool done;

    public bool Pending { get; private set; }

    private FrameCapture(GpuContext context, string path, double delaySeconds)
    {
        this.context = context;
        this.path = path;
        this.delaySeconds = delaySeconds;
    }

    public static FrameCapture FromEnvironment(GpuContext context)
    {
        var path = Environment.GetEnvironmentVariable("VULKAN_SCREENSHOT");
        if (string.IsNullOrEmpty(path))
            return null;

        var delay = double.TryParse(Environment.GetEnvironmentVariable("VULKAN_SCREENSHOT_DELAY"), out var d) ? d : 20;
        RenderLog.Info($"Frame capture enabled: {path} after {delay} s");
        return new FrameCapture(context, path, delay);
    }

    public bool ShouldCapture => !done && !Pending && time.Elapsed.TotalSeconds >= delaySeconds;

    // Records a copy of the backbuffer, which must be in ColorAttachmentOptimal layout and is left in it
    public void Record(VkCommandBuffer commands, FrameLoop frameLoop, VkFormat backbufferFormat)
    {
        extent = frameLoop.BackbufferExtent;
        format = backbufferFormat;
        var size = (ulong)extent.width * extent.height * 4;
        readback = GpuBuffer.Create(context, size, VkBufferUsageFlags.TransferDst, hostVisible: true);

        var image = frameLoop.BackbufferImage;
        var range = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);
        frameLoop.Barrier(image, range,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferRead, VkImageLayout.TransferSrcOptimal);

        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D(extent.width, extent.height, 1),
        };
        context.Device.Api.vkCmdCopyImageToBuffer(commands, image, VkImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);

        frameLoop.Barrier(image, range,
            VkPipelineStageFlags2.Copy, VkAccessFlags2.TransferRead, VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);

        Pending = true;
    }

    // After the frame was submitted: waits for it and writes the file
    public void Complete()
    {
        if (!Pending)
            return;

        Pending = false;
        done = true;
        context.Device.WaitIdle();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using var stream = File.Create(path);
            var size = new Vector2I((int)extent.width, (int)extent.height);
            var pitch = (int)extent.width * 4;
            if (format is VkFormat.B8G8R8A8Srgb or VkFormat.B8G8R8A8Unorm)
                MyImage.Save<Bgra32>(stream, MyImage.FileFormat.Png, (IntPtr)readback.Mapped, pitch, size, 4);
            else
                MyImage.Save<Rgba32>(stream, MyImage.FileFormat.Png, (IntPtr)readback.Mapped, pitch, size, 4);

            RenderLog.Info($"Frame captured to {path}");
        }
        catch (Exception e)
        {
            RenderLog.Error("Frame capture failed", e);
        }
        finally
        {
            readback.Dispose();
            readback = null;
        }
    }

    public void Dispose()
    {
        readback?.Dispose();
    }
}
