using Vortice.Vulkan;

namespace ClientPlugin.Render.Textures;

// Texture contents in CPU memory, laid out for upload: for each array layer (cube face), each mip level, tightly packed
internal sealed class TextureData
{
    public VkFormat Format;
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArrayLayers;
    public bool IsCube;
    public byte[] Data;
    public VkBufferImageCopy[] Regions;

    // Uncompressed 8-bit RGBA/BGRA data, which the GUI premultiplies on load (block compressed GUI textures are authored premultiplied)
    public bool IsRawRgba => Format is VkFormat.R8G8B8A8Unorm or VkFormat.R8G8B8A8Srgb or VkFormat.B8G8R8A8Unorm or VkFormat.B8G8R8A8Srgb;
}
