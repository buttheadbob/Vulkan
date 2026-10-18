using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render.Sprites;

// Sprites and text: the screen GUI, and sprites rendered into generated textures (LCD screens, thumbnails),
// plus the fonts. Render thread only.
//
// Main target sprites are turned into instances while their messages are processed. Offscreen sprite messages
// (TargetTexture set) are kept per target, like MySpritesManager: messages of a newer message batch replace older
// ones. A RenderOffscreenTexture message takes the target's messages and queues a job; jobs are drawn before the
// main GUI pass of the next drawn frame.
internal sealed class GuiRenderer : IDisposable
{
    private const string DefaultTarget = "DefaultOffscreenTarget";
    private const int MaxOffscreenJobsPerFrame = 16;

    private sealed class Bucket
    {
        public int FrameId;
        public readonly List<MySpriteDrawRenderMessage> Messages = new();
    }

    private sealed class OffscreenJob
    {
        public string Target;
        public Color? Background;
        public Vector2 AspectRatio;
        public Bucket Sprites;
        public int Retries;
        public bool Warmed;
    }

    private readonly GpuContext context;
    private readonly TextureCache textures;
    private readonly Func<int> messageFrameId;
    private readonly SpriteRenderer spriteRenderer;
    private readonly SpriteBatcher mainBatcher = new();
    private readonly SpriteBatcher offscreenBatcher = new();
    private readonly Dictionary<string, Bucket> buckets = new();
    private readonly List<OffscreenJob> jobs = new();
    private readonly ConcurrentDictionary<int, RenderFont> fonts = new();
    private readonly Stopwatch time = Stopwatch.StartNew();
    private volatile RenderFont debugFont;
    private float mainViewportScale = 1f;

    public GuiRenderer(GpuContext context, TextureCache textures, Func<int> messageFrameId)
    {
        this.context = context;
        this.textures = textures;
        this.messageFrameId = messageFrameId;
        spriteRenderer = new SpriteRenderer(context);
    }

    // Called once per drawn frame before the messages are processed
    public void BeginFrame(int frameSlot)
    {
        mainBatcher.Clear();
        spriteRenderer.BeginFrame(frameSlot);
    }

    #region Messages

    // Returns true if the message was a GUI message
    public bool ProcessMessage(MyRenderMessageBase message)
    {
        switch (message.MessageType)
        {
            case MyRenderMessageEnum.CreateFont:
                CreateFont((MyRenderMessageCreateFont)message);
                return true;

            case MyRenderMessageEnum.SpriteMainViewportScale:
                mainViewportScale = ((MyRenderMessageSpriteMainViewportScale)message).ScaleFactor;
                return true;

            case MyRenderMessageEnum.RenderOffscreenTexture:
                QueueOffscreen((MyRenderMessageRenderOffscreenTexture)message);
                return true;

            case MyRenderMessageEnum.DrawSprite:
            case MyRenderMessageEnum.DrawSpriteExt:
            case MyRenderMessageEnum.DrawSpriteAtlas:
            case MyRenderMessageEnum.DrawString:
            case MyRenderMessageEnum.DrawStringAligned:
            case MyRenderMessageEnum.SpriteScissorPush:
            case MyRenderMessageEnum.SpriteScissorPop:
                var sprite = (MySpriteDrawRenderMessage)message;
                if (sprite.TargetTexture == null || sprite.TargetTexture == DefaultTarget)
                    AddSprite(mainBatcher, sprite);
                else
                    KeepOffscreenMessage(sprite);
                return true;

            default:
                return false;
        }
    }

    private void KeepOffscreenMessage(MySpriteDrawRenderMessage message)
    {
        var frameId = messageFrameId();
        if (!buckets.TryGetValue(message.TargetTexture, out var bucket))
        {
            bucket = new Bucket { FrameId = frameId };
            buckets.Add(message.TargetTexture, bucket);
        }

        if (bucket.FrameId != frameId)
        {
            Release(bucket);
            bucket.FrameId = frameId;
        }

        message.AddRef();
        bucket.Messages.Add(message);
    }

    // Like MyOffscreenRenderer.Add: a newer request for the same target replaces a pending one
    private void QueueOffscreen(MyRenderMessageRenderOffscreenTexture message)
    {
        buckets.Remove(message.OffscreenTexture, out var sprites);
        var job = new OffscreenJob
        {
            Target = message.OffscreenTexture,
            Background = message.BackgroundColor,
            AspectRatio = message.AspectRatio,
            Sprites = sprites,
        };

        var index = jobs.FindIndex(j => j.Target == job.Target);
        if (index >= 0)
        {
            Release(jobs[index].Sprites);
            jobs[index] = job;
        }
        else
        {
            jobs.Add(job);
        }
    }

    private static void Release(Bucket bucket)
    {
        if (bucket == null)
            return;

        foreach (var message in bucket.Messages)
            message.Dispose();
        bucket.Messages.Clear();
    }

    private void AddSprite(SpriteBatcher batcher, MySpriteDrawRenderMessage message)
    {
        switch (message.MessageType)
        {
            case MyRenderMessageEnum.SpriteScissorPush:
                batcher.PushScissor(((MyRenderMessageSpriteScissorPush)message).ScreenRectangle);
                break;

            case MyRenderMessageEnum.SpriteScissorPop:
                batcher.PopScissor();
                break;

            case MyRenderMessageEnum.DrawSprite:
            {
                var m = (MyRenderMessageDrawSprite)message;
                var texture = textures.Get(m.Texture, m.WaitTillLoaded);
                var mask = textures.Get(m.MaskTexture, m.WaitTillLoaded);

                var rotation = m.Rotation;
                if (m.RotationSpeed != 0)
                    rotation += m.RotationSpeed * (float)time.Elapsed.TotalSeconds;
                var tangent = rotation != 0 ? new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) : Vector2.UnitX;

                batcher.Add(TextureCache.SlotOf(texture), SizeOf(texture), m.Color, m.DestinationRectangle.Center, tangent,
                    m.SourceRectangle, m.DestinationRectangle, m.IgnoreBounds, 0, MaskSlot(mask));
                break;
            }

            case MyRenderMessageEnum.DrawSpriteExt:
            {
                var m = (MyRenderMessageDrawSpriteExt)message;
                var texture = textures.Get(m.Texture, m.WaitTillLoaded);
                var mask = textures.Get(m.MaskTexture, m.WaitTillLoaded);
                batcher.Add(TextureCache.SlotOf(texture), SizeOf(texture), m.Color, m.Origin, m.RightVector,
                    m.SourceRectangle, m.DestinationRectangle, m.IgnoreBounds, 0, MaskSlot(mask));
                break;
            }

            case MyRenderMessageEnum.DrawSpriteAtlas:
            {
                var m = (MyRenderMessageDrawSpriteAtlas)message;
                var texture = textures.Get(m.Texture, waitTillLoaded: true);
                var size = SizeOf(texture);
                var source = new Rectangle(
                    (int)(size.X * m.TextureOffset.X), (int)(size.Y * m.TextureOffset.Y),
                    (int)(size.X * m.TextureSize.X), (int)(size.Y * m.TextureSize.Y));
                var halfX = m.HalfSize.X * m.Scale.X;
                var halfY = m.HalfSize.Y * m.Scale.Y;
                var destination = new RectangleF(m.Position.X * m.Scale.X - halfX, m.Position.Y * m.Scale.Y - halfY, halfX * 2, halfY * 2);
                var origin = new Vector2(destination.X + halfX, destination.Y + halfY);
                batcher.Add(TextureCache.SlotOf(texture), size, m.Color, origin, m.RightVector, source, destination,
                    m.IgnoreBounds, 0, (uint)BindlessDescriptors.TextureSlot.White);
                break;
            }

            case MyRenderMessageEnum.DrawStringAligned:
            {
                var m = (MyRenderMessageDrawStringAligned)message;
                GetFont(m.FontIndex)?.DrawStringAligned(batcher, m.ScreenCoord, m.ColorMask, m.Text, m.ScreenScale, m.IgnoreBounds,
                    m.ScreenMaxWidth, m.TextureWidthInPx, m.Alignment);
                break;
            }

            case MyRenderMessageEnum.DrawString:
            {
                var m = (MyRenderMessageDrawString)message;
                GetFont(m.FontIndex)?.DrawString(batcher, m.ScreenCoord, m.ColorMask, m.Text, m.ScreenScale, m.IgnoreBounds, m.ScreenMaxWidth);
                break;
            }
        }
    }

    private static Vector2 SizeOf(TextureCache.Entry entry) => entry == null ? Vector2.One : new Vector2(entry.Size.X, entry.Size.Y);

    private static uint MaskSlot(TextureCache.Entry mask) => mask == null ? (uint)BindlessDescriptors.TextureSlot.White : TextureCache.SlotOf(mask);

    #endregion

    #region Fonts

    // Unknown fonts fall back to the debug font like MyRender11.GetFont; fonts still loading draw nothing
    private RenderFont GetFont(int id) => fonts.TryGetValue(id, out var font) ? font : debugFont;

    private void CreateFont(MyRenderMessageCreateFont message)
    {
        if (fonts.TryGetValue(message.FontId, out var existing) && SamePath(existing.Path, message.FontPath))
            return;

        var id = message.FontId;
        var path = message.FontPath;
        var colorMask = message.ColorMask ?? Color.White;
        var isDebugFont = message.IsDebugFont;

        // Fonts and their glyph pages are loaded synchronously on the render thread. LCD apps often
        // draw their static text once; if the pages were still loading then, that text never appeared.
        try
        {
            var font = new RenderFont(path, colorMask, new MyFont(path), textures);
            fonts[id] = font;
            if (isDebugFont)
                debugFont = font;
        }
        catch (Exception e)
        {
            RenderLog.Error($"Failed to load font {path}", e);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Recording

    // Renders queued offscreen targets. Must be recorded outside any rendering pass, before the passes sampling them.
    public unsafe void RecordOffscreen(VkCommandBuffer commands)
    {
        var count = Math.Min(jobs.Count, MaxOffscreenJobsPerFrame);
        var keep = new List<OffscreenJob>();
        for (var i = 0; i < count; i++)
        {
            var job = jobs[i];
            var found = textures.TryGetGenerated(job.Target, out var target);
            if (!job.Warmed)
            {
                job.Warmed = true;
                WarmJobTextures(job.Sprites);
                keep.Add(job);
                continue;
            }

            if (!found && ++job.Retries <= 10)
            {
                // The generated texture message can arrive after the first render request; keep the
                // sprites for a few frames instead of dropping the app content permanently.
                keep.Add(job);
                continue;
            }

            try
            {
                if (found)
                    RecordOffscreenJob(commands, job, target);
            }
            finally
            {
                Release(job.Sprites);
            }
        }

        jobs.RemoveRange(0, count);
        jobs.AddRange(keep);
    }

    // Starts loading every texture a job samples. The job itself renders on the next frame, after
    // this frame's uploads have been recorded, so a texture first used by the job is not sampled
    // before its upload (the stock renderer uploads immediately, our transfers are deferred).
    private void WarmJobTextures(Bucket sprites)
    {
        if (sprites == null)
            return;
        foreach (var message in sprites.Messages)
        {
            switch (message)
            {
                case MyRenderMessageDrawSprite m:
                    textures.Get(m.Texture, waitTillLoaded: true);
                    textures.Get(m.MaskTexture, waitTillLoaded: true);
                    break;
                case MyRenderMessageDrawSpriteExt m:
                    textures.Get(m.Texture, waitTillLoaded: true);
                    textures.Get(m.MaskTexture, waitTillLoaded: true);
                    break;
                case MyRenderMessageDrawSpriteAtlas m:
                    textures.Get(m.Texture, waitTillLoaded: true);
                    break;
            }
        }
    }

    private unsafe void RecordOffscreenJob(VkCommandBuffer commands, OffscreenJob job, TextureCache.Entry target)
    {
        var api = context.Device.Api;
        var image = target.Image;
        var extent = new VkExtent2D(image.Width, image.Height);

        offscreenBatcher.Clear();
        if (job.Sprites != null)
        {
            foreach (var message in job.Sprites.Messages)
                AddSprite(offscreenBatcher, message);
        }

        // Level 0 is rendered; generated textures rest in ShaderReadOnlyOptimal between uses
        var level0 = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1);
        Barrier(api, commands, image, level0,
            VkPipelineStageFlags2.AllGraphics | VkPipelineStageFlags2.ComputeShader, VkAccessFlags2.ShaderSampledRead, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal);

        // MyOffscreenRenderer clears to the background color converted to linear (or transparent black)
        var clear = job.Background.HasValue ? job.Background.Value.ToVector4().ToLinearRGB() : Vector4.Zero;
        var levelView = CreateLevel0View(image);
        try
        {
            var attachment = new VkRenderingAttachmentInfo
            {
                imageView = levelView,
                imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = VkAttachmentLoadOp.Clear,
                storeOp = VkAttachmentStoreOp.Store,
                clearValue = new VkClearValue { color = new VkClearColorValue(clear.X, clear.Y, clear.Z, clear.W) },
            };
            var renderingInfo = new VkRenderingInfo
            {
                renderArea = new VkRect2D(0, 0, extent.width, extent.height),
                layerCount = 1,
                colorAttachmentCount = 1,
                pColorAttachments = &attachment,
            };

            api.vkCmdBeginRendering(commands, &renderingInfo);
            var full = new MyViewport(0, 0, extent.width, extent.height);
            var coordinates = new Vector2(image.Width * job.AspectRatio.X, image.Height * job.AspectRatio.Y);
            spriteRenderer.Record(commands, offscreenBatcher, image.Format, SpriteBlend.PremultipliedColorOnly, coordinates, extent, full, full);
            api.vkCmdEndRendering(commands);
        }
        finally
        {
            var device = context.Device;
            context.Release(() => device.Api.vkDestroyImageView(levelView, null));
        }

        if (image.MipLevels > 1)
        {
            MipGenerator.Record(api, commands, image, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);
        }
        else
        {
            Barrier(api, commands, image, level0,
                VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite, VkImageLayout.ColorAttachmentOptimal,
                VkPipelineStageFlags2.AllGraphics | VkPipelineStageFlags2.ComputeShader, VkAccessFlags2.ShaderSampledRead, VkImageLayout.ShaderReadOnlyOptimal);
        }

        target.GeneratedReady = true;
    }

    // Color attachments need a single-level view; the image's own view covers every mip level
    private unsafe VkImageView CreateLevel0View(GpuImage image)
    {
        var viewInfo = new VkImageViewCreateInfo
        {
            image = image.Handle,
            viewType = VkImageViewType.Image2D,
            format = image.Format,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        VkImageView view;
        context.Device.Api.vkCreateImageView(&viewInfo, null, &view).CheckResult("vkCreateImageView (offscreen target)");
        return view;
    }

    private static unsafe void Barrier(VkDeviceApi api, VkCommandBuffer commands, GpuImage image, VkImageSubresourceRange range,
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
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            image = image.Handle,
            subresourceRange = range,
        };
        var dependency = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &barrier };
        api.vkCmdPipelineBarrier2(commands, &dependency);
    }

    // Records the screen GUI into the active dynamic rendering pass, over the whole framebuffer
    public void RecordMain(VkCommandBuffer commands, VkFormat targetFormat, Vector2I resolution, VkExtent2D framebuffer)
    {
        var full = new MyViewport(0, 0, framebuffer.width, framebuffer.height);
        var bound = new MyViewport(
            (framebuffer.width - framebuffer.width * mainViewportScale) / 2f,
            (framebuffer.height - framebuffer.height * mainViewportScale) / 2f,
            framebuffer.width * mainViewportScale,
            framebuffer.height * mainViewportScale);

        spriteRenderer.Record(commands, mainBatcher, targetFormat, SpriteBlend.Premultiplied,
            new Vector2(resolution.X, resolution.Y), framebuffer, bound, full);
    }

    #endregion

    public void Dispose()
    {
        foreach (var bucket in buckets.Values)
            Release(bucket);
        buckets.Clear();

        foreach (var job in jobs)
            Release(job.Sprites);
        jobs.Clear();

        spriteRenderer.Dispose();
        fonts.Clear();
    }
}
