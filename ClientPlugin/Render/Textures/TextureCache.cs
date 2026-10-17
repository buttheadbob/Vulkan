using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRage.FileSystem;
using VRage.Render.Image;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render.Textures;

// File textures by name, as referenced by render messages (paths relative to the game's Content folder, or absolute).
// Files are read and decoded on worker threads; images are created and uploaded on the render thread.
internal sealed class TextureCache : IDisposable
{
    public enum State
    {
        Loading,
        Decoded,
        Ready,
        Failed,
    }

    // How the stock renderer treats the texture: GUI textures are sRGB and premultiplied on load,
    // model ColorMetal/Extensions are sRGB, normal maps and alphamasks stay linear (MyFileTexture).
    public enum TextureKind
    {
        Gui,
        ColorMetal,
        NormalGloss,
        Extensions,
        Alphamask,
        Skybox, // sRGB cubemap
        Data,   // linear data texture (e.g. the ambient BRDF LUT)
    }

    public sealed class Entry
    {
        public readonly string Name;
        public readonly string Path;
        public readonly TextureKind Kind;
        public volatile State Status = State.Loading;
        public uint Slot;
        public uint CubeSlot;
        public bool IsCube;
        public GpuImage Image;
        public Vector2I Size;
        public TextureData Pending;
        public int LoadStarted;

        // Generated textures (CreateGeneratedTexture): created by the game, written by uploads or offscreen sprite rendering
        public bool IsGenerated;
        public volatile bool GeneratedReady;

        public Entry(string name, string path = null, TextureKind kind = TextureKind.Gui)
        {
            Name = name;
            Path = path ?? name;
            Kind = kind;
        }
    }

    private readonly GpuContext context;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Vector2I> sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Entry> decoded = new();

    public TextureCache(GpuContext context)
    {
        this.context = context;
    }

    // Render thread. Starts loading on first use. waitTillLoaded makes the texture usable in this frame.
    // Non-GUI kinds get their own entry, because the same file may be sampled with different color rules.
    public Entry Get(string name, bool waitTillLoaded, TextureKind kind = TextureKind.Gui)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        // Model materials can be swapped to a generated offscreen texture (cockpit/LCD screens).
        // Those live in the plain-name entries created by CreateGenerated.
        if (entries.TryGetValue(name, out var generated) && generated.IsGenerated)
            return generated;
        if (MyRenderProxy.IsValidGeneratedTextureName(name))
            return null;

        var key = kind == TextureKind.Gui ? name : $"{(int)kind}:{name}";
        var entry = entries.GetOrAdd(key, _ => new Entry(key, name, kind));
        if (Interlocked.Exchange(ref entry.LoadStarted, 1) == 0)
        {
            if (waitTillLoaded)
                Decode(entry);
            else
                Task.Run(() => Decode(entry));
        }

        if (waitTillLoaded)
        {
            SpinWait.SpinUntil(() => entry.Status != State.Loading);
            if (entry.Status == State.Decoded)
                Finish(entry);
        }

        return entry;
    }

    // Bindless slot to sample: the texture once ready, transparent while loading, magenta/black if it failed
    public static uint SlotOf(Entry entry)
    {
        if (entry == null)
            return (uint)BindlessDescriptors.TextureSlot.White;

        return entry.Status switch
        {
            State.Ready => entry.Slot,
            State.Failed => (uint)BindlessDescriptors.TextureSlot.Missing,
            _ => (uint)BindlessDescriptors.TextureSlot.Transparent,
        };
    }

    // Cubemap slot to sample; 0 is the fixed black cube (also used while loading or on failure)
    public static uint CubeSlotOf(Entry entry) =>
        entry is { Status: State.Ready, IsCube: true } ? entry.CubeSlot : 0;

    public static uint MipLevelsOf(Entry entry) =>
        entry is { Status: State.Ready, IsCube: true } ? entry.Image.MipLevels : 1;

    // Render thread, once per frame before rendering: creates and uploads the textures decoded since the last frame
    public void ProcessDecoded()
    {
        while (decoded.TryDequeue(out var entry))
        {
            if (entry.Status == State.Decoded)
                Finish(entry);
        }
    }

    // Any thread. Size in pixels from the file header, without loading the texture (IMyRender.GetTextureSize).
    public Vector2 GetSize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return Vector2.Zero;

        if (entries.TryGetValue(name, out var entry) && entry.Status is State.Decoded or State.Ready)
            return entry.Size;

        return sizes.GetOrAdd(name, static n =>
        {
            try
            {
                using var stream = MyFileSystem.OpenRead(ResolvePath(n));
                if (stream == null)
                    return Vector2I.Zero;

                if (DdsLoader.TryReadSize(stream, out var size))
                    return size;

                stream.Position = 0;
                return MyImage.Load(stream, oneChannel: false, headerOnly: true)?.Size ?? Vector2I.Zero;
            }
            catch (Exception)
            {
                return Vector2I.Zero;
            }
        });
    }

    // Any thread (IMyRender.IsTextureLoaded)
    public bool IsLoaded(string name)
    {
        if (string.IsNullOrEmpty(name) || !entries.TryGetValue(name, out var entry))
            return false;

        return entry.IsGenerated ? entry.GeneratedReady : entry.Status is State.Ready or State.Failed;
    }

    // Render thread. Like MyFileTextureManager.CreateGeneratedTexture: an existing generated texture is kept as is.
    public Entry CreateGenerated(string name, int width, int height, MyGeneratedTextureType type, bool generateMipmaps, byte[] data, bool immediatelyReady)
    {
        if (string.IsNullOrEmpty(name) || width <= 0 || height <= 0)
            return null;

        if (entries.TryGetValue(name, out var existing))
        {
            if (existing.IsGenerated)
                return existing;

            Destroy(existing);
        }

        var format = type switch
        {
            MyGeneratedTextureType.RGBA => VkFormat.R8G8B8A8Srgb,
            MyGeneratedTextureType.RGBA_Linear => VkFormat.R8G8B8A8Unorm,
            _ => VkFormat.R8Unorm, // Alphamask
        };

        var mips = generateMipmaps ? (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1 : 1;
        var image = GpuImage.Create2D(context, (uint)width, (uint)height, mips, 1, format,
            VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc | VkImageUsageFlags.ColorAttachment);

        var entry = new Entry(name)
        {
            IsGenerated = true,
            GeneratedReady = immediatelyReady,
            Image = image,
            Size = new Vector2I(width, height),
            LoadStarted = 1,
        };

        UploadGenerated(entry, data);

        entry.Slot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(entry.Slot, image.View);
        entry.Status = State.Ready;
        entries[name] = entry;
        sizes.TryRemove(name, out _);
        return entry;
    }

    // Render thread. Replaces the contents of a generated texture (MyRenderMessageResetGeneratedTexture).
    public void ResetGenerated(string name, byte[] data)
    {
        if (entries.TryGetValue(name, out var entry) && entry.IsGenerated)
            UploadGenerated(entry, data);
    }

    public void DestroyGenerated(string name)
    {
        if (entries.TryGetValue(name, out var entry) && entry.IsGenerated)
            Destroy(entry);
    }

    public bool TryGetGenerated(string name, out Entry entry) =>
        entries.TryGetValue(name, out entry) && entry.IsGenerated && entry.Status == State.Ready;

    // Level 0 from data (or cleared to zero when there is none), the other levels generated from it
    private void UploadGenerated(Entry entry, byte[] data)
    {
        var image = entry.Image;
        var bytesPerPixel = image.Format == VkFormat.R8Unorm ? 1 : 4;
        var size = (int)(image.Width * image.Height) * bytesPerPixel;
        var pixels = data != null && data.Length >= size ? data : new byte[size];

        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
            imageExtent = new VkExtent3D(image.Width, image.Height, 1),
        };
        context.Uploads.UploadImage(image, pixels.AsSpan(0, size), [region], generateMips: true);
    }

    private void Destroy(Entry entry)
    {
        entries.TryRemove(entry.Name, out _);
        entry.Image?.Dispose();
        entry.Image = null;
        if (entry.Status == State.Ready)
        {
            if (entry.IsCube)
                context.Bindless.FreeCubeSlot(entry.CubeSlot);
            else
                context.Bindless.FreeTextureSlot(entry.Slot);
        }

        entry.Status = State.Failed;
    }

    public static string ResolvePath(string name) => Path.IsPathRooted(name) ? name : Path.Combine(MyFileSystem.ContentPath, name);

    private void Decode(Entry entry)
    {
        try
        {
            byte[] file;
            using (var stream = MyFileSystem.OpenRead(ResolvePath(entry.Path)))
            {
                if (stream == null)
                    throw new FileNotFoundException("Texture not found", entry.Path);

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                file = memory.ToArray();
            }

            var data = IsDds(file) ? DdsLoader.Load(file, entry.Path) : LoadImage(file);
            PrepareForKind(data, entry.Kind);

            entry.Pending = data;
            entry.Size = new Vector2I((int)data.Width, (int)data.Height);
            entry.Status = State.Decoded;
            decoded.Enqueue(entry);
        }
        catch (Exception e)
        {
            RenderLog.Warning($"Failed to load texture {entry.Path}: {e.Message}");
            entry.Status = State.Failed;
        }
    }

    private void Finish(Entry entry)
    {
        lock (entry)
        {
            if (entry.Status != State.Decoded)
                return;

            var data = entry.Pending;
            entry.Pending = null;

            var image = GpuImage.Create2D(context, data.Width, data.Height, data.MipLevels, data.ArrayLayers, data.Format,
                VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, data.IsCube);
            context.Uploads.UploadImage(image, data.Data, data.Regions);

            entry.Image = image;
            entry.IsCube = data.IsCube;
            if (data.IsCube)
            {
                entry.CubeSlot = context.Bindless.AllocateCubeSlot();
                context.Bindless.WriteCube(entry.CubeSlot, image.View);
            }
            else
            {
                entry.Slot = context.Bindless.AllocateTextureSlot();
                context.Bindless.WriteTexture(entry.Slot, image.View);
            }

            entry.Status = State.Ready;
        }
    }

    private static void PrepareForKind(TextureData data, TextureKind kind)
    {
        switch (kind)
        {
            case TextureKind.Gui:
                // Stock GUI textures: sRGB, uncompressed RGBA premultiplied on load
                // (block compressed GUI textures are authored with premultiplied alpha)
                var raw = data.IsRawRgba;
                data.Format = DdsLoader.MakeSrgb(data.Format);
                if (raw)
                    PremultiplyAlpha(data.Data);
                break;

            case TextureKind.ColorMetal:
            case TextureKind.Extensions:
                // Stock MyGlobalResources: ColorMetal and Extension textures are BC7/BC3 sRGB
                data.Format = DdsLoader.MakeSrgb(data.Format);
                break;

            case TextureKind.Skybox:
                // BackgroundCube and BackgroundCubeIndirect are BC7 sRGB cubemaps
                data.Format = DdsLoader.MakeSrgb(data.Format);
                break;

            case TextureKind.Data:
                // Linear data (ambient BRDF LUT)
                break;

            // NormalGloss and Alphamask are linear (MyGlobalResources: BC7_UNorm, R8_UNorm)
        }
    }

    // Premultiplies in gamma space like MyFileTexture.PremultiplyAlpha: c' = (c^2.2 * a)^(1/2.2)
    private static readonly byte[] premultiplyTable = BuildPremultiplyTable();

    private static byte[] BuildPremultiplyTable()
    {
        var table = new byte[256 * 256];
        for (var c = 0; c < 256; c++)
        for (var a = 0; a < 256; a++)
            table[c * 256 + a] = (byte)(Math.Pow(Math.Pow(c / 255.0, 2.2) * (a / 255.0), 1 / 2.2) * 255.0);
        return table;
    }

    private static void PremultiplyAlpha(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            pixels[i] = premultiplyTable[pixels[i] * 256 + a];
            pixels[i + 1] = premultiplyTable[pixels[i + 1] * 256 + a];
            pixels[i + 2] = premultiplyTable[pixels[i + 2] * 256 + a];
        }
    }

    private static bool IsDds(byte[] file) => file.Length >= 4 && file[0] == 'D' && file[1] == 'D' && file[2] == 'S' && file[3] == ' ';

    // PNG, JPG, ... through the game's ImageSharp based loader, as RGBA8
    private static TextureData LoadImage(byte[] file)
    {
        using var stream = new MemoryStream(file);
        var image = MyImage.Load(stream, oneChannel: false);
        var width = (uint)image.Size.X;
        var height = (uint)image.Size.Y;
        var pixels = new byte[width * height * 4];

        switch (image)
        {
            case MyImage<uint> rgba:
                Buffer.BlockCopy(rgba.Data, 0, pixels, 0, pixels.Length);
                break;
            case MyImage<byte> gray:
                for (var i = 0; i < gray.Data.Length; i++)
                {
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray.Data[i];
                    pixels[i * 4 + 3] = 0xFF;
                }
                break;
            case MyImage<ushort> gray16:
                for (var i = 0; i < gray16.Data.Length; i++)
                {
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = (byte)(gray16.Data[i] >> 8);
                    pixels[i * 4 + 3] = 0xFF;
                }
                break;
            default:
                throw new NotSupportedException("Unsupported image format");
        }

        return new TextureData
        {
            Format = VkFormat.R8G8B8A8Unorm,
            Width = width,
            Height = height,
            MipLevels = 1,
            ArrayLayers = 1,
            Data = pixels,
            Regions =
            [
                new VkBufferImageCopy
                {
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                    imageExtent = new VkExtent3D(width, height, 1),
                },
            ],
        };
    }

    public void Dispose()
    {
        foreach (var entry in entries.Values)
        {
            entry.Image?.Dispose();
            if (entry.Status != State.Ready)
                continue;
            if (entry.IsCube)
                context.Bindless.FreeCubeSlot(entry.CubeSlot);
            else
                context.Bindless.FreeTextureSlot(entry.Slot);
        }

        entries.Clear();
    }
}
