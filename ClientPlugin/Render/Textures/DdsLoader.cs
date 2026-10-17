using System;
using System.Buffers.Binary;
using System.IO;
using Vortice.Vulkan;
using VRageMath;

namespace ClientPlugin.Render.Textures;

// Reads DirectDraw Surface files: DX10 headers with DXGI formats and the legacy FourCC/bit mask formats.
// Formats without a Vulkan equivalent (24-bit RGB, L8, A8, X8 variants) are converted to RGBA8 on load.
internal static class DdsLoader
{
    private const uint Magic = 0x20534444; // "DDS "
    private const int HeaderSize = 124;
    private const int Dx10HeaderSize = 20;

    // DDS_HEADER.dwFlags / DDS_PIXELFORMAT.dwFlags / dwCaps2
    private const uint PixelFormatAlphaPixels = 0x1;
    private const uint PixelFormatFourCC = 0x4;
    private const uint PixelFormatRgb = 0x40;
    private const uint PixelFormatLuminance = 0x20000;
    private const uint PixelFormatAlpha = 0x2;
    private const uint PixelFormatPalette8 = 0x20;
    private const int PaletteSize = 256 * 4;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2Volume = 0x200000;

    private const uint Dx10MiscTextureCube = 0x4;

    private enum Conversion
    {
        None,
        Bgr24ToBgra,
        BgrxToBgra,     // Force alpha to 255
        RgbxToRgba,
        L8ToRgba,
        A8ToRgba,
        L8A8ToRgba,
        Palette8ToRgba,
    }

    // Reads only the header, for the texture size queries of the GUI
    public static bool TryReadSize(Stream stream, out Vector2I size)
    {
        size = default;
        Span<byte> header = stackalloc byte[4 + HeaderSize];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            return false;

        var height = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16));
        size = new Vector2I((int)width, (int)height);
        return true;
    }

    public static TextureData Load(byte[] file, string name)
    {
        if (file.Length < 4 + HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
            throw new InvalidDataException($"{name}: not a DDS file");

        var header = file.AsSpan(4, HeaderSize);
        var height = Read(header, 8);
        var width = Read(header, 12);
        var depth = Read(header, 20);
        var mipCount = Math.Max(1u, Read(header, 24));
        var pfFlags = Read(header, 76);
        var fourCC = Read(header, 80);
        var rgbBitCount = Read(header, 84);
        var rMask = Read(header, 88);
        var gMask = Read(header, 92);
        var bMask = Read(header, 96);
        var aMask = Read(header, 100);
        var caps2 = Read(header, 108);

        if ((caps2 & Caps2Volume) != 0 && depth > 1)
            throw new NotSupportedException($"{name}: volume textures are not supported");

        var dataOffset = 4 + HeaderSize;
        var arrayLayers = 1u;
        var isCube = (caps2 & Caps2Cubemap) != 0;
        VkFormat format;
        var conversion = Conversion.None;

        if ((pfFlags & PixelFormatFourCC) != 0 && fourCC == FourCC("DX10"))
        {
            var dx10 = file.AsSpan(dataOffset, Dx10HeaderSize);
            var dxgiFormat = Read(dx10, 0);
            var miscFlags = Read(dx10, 8);
            arrayLayers = Math.Max(1u, Read(dx10, 12));
            isCube = (miscFlags & Dx10MiscTextureCube) != 0;
            dataOffset += Dx10HeaderSize;

            (format, conversion) = FromDxgi(dxgiFormat);
            if (format == VkFormat.Undefined)
                throw new NotSupportedException($"{name}: DXGI format {dxgiFormat} is not supported");
        }
        else if ((pfFlags & PixelFormatFourCC) != 0)
        {
            format = FromFourCC(fourCC);
            if (format == VkFormat.Undefined)
                throw new NotSupportedException($"{name}: FourCC 0x{fourCC:X8} is not supported");
        }
        else if ((pfFlags & PixelFormatPalette8) != 0 && rgbBitCount == 8)
        {
            // 256 RGBX palette entries (PALETTEENTRY) follow the header, then one index per pixel
            format = VkFormat.R8G8B8A8Unorm;
            conversion = Conversion.Palette8ToRgba;
        }
        else
        {
            (format, conversion) = FromMasks(pfFlags, rgbBitCount, rMask, gMask, bMask, aMask);
            if (format == VkFormat.Undefined)
                throw new NotSupportedException($"{name}: pixel format ({rgbBitCount} bpp, masks {rMask:X}/{gMask:X}/{bMask:X}/{aMask:X}) is not supported");
        }

        if (isCube)
            arrayLayers *= 6;

        var sourceBytesPerPixel = conversion switch
        {
            Conversion.Bgr24ToBgra => 3,
            Conversion.L8ToRgba or Conversion.A8ToRgba or Conversion.Palette8ToRgba => 1,
            Conversion.L8A8ToRgba => 2,
            _ => 0,
        };

        // Compute the layout, converting rows where needed
        var (blockWidth, blockHeight, blockBytes) = BlockInfo(format);
        var regions = new VkBufferImageCopy[arrayLayers * mipCount];
        var totalSize = 0ul;
        for (uint layer = 0, r = 0; layer < arrayLayers; layer++)
        {
            for (uint mip = 0; mip < mipCount; mip++, r++)
            {
                var w = Math.Max(1u, width >> (int)mip);
                var h = Math.Max(1u, height >> (int)mip);
                regions[r] = new VkBufferImageCopy
                {
                    bufferOffset = totalSize,
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, mip, layer, 1),
                    imageExtent = new VkExtent3D(w, h, 1),
                };
                totalSize += (ulong)((w + blockWidth - 1) / blockWidth * ((h + blockHeight - 1) / blockHeight) * blockBytes);
            }
        }

        byte[] data;
        if (conversion == Conversion.None)
        {
            if ((ulong)(file.Length - dataOffset) < totalSize)
                throw new InvalidDataException($"{name}: file is truncated");

            data = file.AsSpan(dataOffset, (int)totalSize).ToArray();
        }
        else
        {
            data = Convert(file, dataOffset, width, height, mipCount, arrayLayers, conversion, sourceBytesPerPixel, totalSize, name);
        }

        return new TextureData
        {
            Format = format,
            Width = width,
            Height = height,
            MipLevels = mipCount,
            ArrayLayers = arrayLayers,
            IsCube = isCube,
            Data = data,
            Regions = regions,
        };
    }

    private static byte[] Convert(byte[] file, int offset, uint width, uint height, uint mips, uint layers,
        Conversion conversion, int sourceBpp, ulong totalSize, string name)
    {
        var data = new byte[totalSize];
        var palette = ReadOnlySpan<byte>.Empty;
        if (conversion == Conversion.Palette8ToRgba)
        {
            if (offset + PaletteSize > file.Length)
                throw new InvalidDataException($"{name}: file is truncated");

            palette = file.AsSpan(offset, PaletteSize);
            offset += PaletteSize;
        }

        var source = offset;
        var target = 0;
        for (uint layer = 0; layer < layers; layer++)
        {
            for (uint mip = 0; mip < mips; mip++)
            {
                var pixels = (int)(Math.Max(1u, width >> (int)mip) * Math.Max(1u, height >> (int)mip));
                var stride = conversion is Conversion.BgrxToBgra or Conversion.RgbxToRgba ? 4 : sourceBpp;
                if (source + pixels * stride > file.Length)
                    throw new InvalidDataException($"{name}: file is truncated");

                for (var i = 0; i < pixels; i++, source += stride, target += 4)
                {
                    switch (conversion)
                    {
                        case Conversion.Bgr24ToBgra:
                            data[target] = file[source];
                            data[target + 1] = file[source + 1];
                            data[target + 2] = file[source + 2];
                            data[target + 3] = 0xFF;
                            break;
                        case Conversion.BgrxToBgra:
                        case Conversion.RgbxToRgba:
                            data[target] = file[source];
                            data[target + 1] = file[source + 1];
                            data[target + 2] = file[source + 2];
                            data[target + 3] = 0xFF;
                            break;
                        case Conversion.L8ToRgba:
                            data[target] = data[target + 1] = data[target + 2] = file[source];
                            data[target + 3] = 0xFF;
                            break;
                        case Conversion.A8ToRgba:
                            data[target] = data[target + 1] = data[target + 2] = 0;
                            data[target + 3] = file[source];
                            break;
                        case Conversion.Palette8ToRgba:
                            var entry = file[source] * 4;
                            data[target] = palette[entry];
                            data[target + 1] = palette[entry + 1];
                            data[target + 2] = palette[entry + 2];
                            data[target + 3] = 0xFF; // PALETTEENTRY.peFlags is not alpha
                            break;
                        case Conversion.L8A8ToRgba:
                            data[target] = data[target + 1] = data[target + 2] = file[source];
                            data[target + 3] = file[source + 1];
                            break;
                    }
                }
            }
        }

        return data;
    }

    private static (VkFormat, Conversion) FromDxgi(uint dxgi) => dxgi switch
    {
        2 => (VkFormat.R32G32B32A32Sfloat, Conversion.None),
        10 => (VkFormat.R16G16B16A16Sfloat, Conversion.None),
        11 => (VkFormat.R16G16B16A16Unorm, Conversion.None),
        16 => (VkFormat.R32G32Sfloat, Conversion.None),
        24 => (VkFormat.A2B10G10R10UnormPack32, Conversion.None),
        26 => (VkFormat.B10G11R11UfloatPack32, Conversion.None),
        28 => (VkFormat.R8G8B8A8Unorm, Conversion.None),
        29 => (VkFormat.R8G8B8A8Srgb, Conversion.None),
        34 => (VkFormat.R16G16Sfloat, Conversion.None),
        35 => (VkFormat.R16G16Unorm, Conversion.None),
        41 => (VkFormat.R32Sfloat, Conversion.None),
        49 => (VkFormat.R8G8Unorm, Conversion.None),
        54 => (VkFormat.R16Sfloat, Conversion.None),
        56 => (VkFormat.R16Unorm, Conversion.None),
        61 => (VkFormat.R8Unorm, Conversion.None),
        65 => (VkFormat.R8G8B8A8Unorm, Conversion.A8ToRgba),
        71 => (VkFormat.Bc1RgbaUnormBlock, Conversion.None),
        72 => (VkFormat.Bc1RgbaSrgbBlock, Conversion.None),
        74 => (VkFormat.Bc2UnormBlock, Conversion.None),
        75 => (VkFormat.Bc2SrgbBlock, Conversion.None),
        77 => (VkFormat.Bc3UnormBlock, Conversion.None),
        78 => (VkFormat.Bc3SrgbBlock, Conversion.None),
        80 => (VkFormat.Bc4UnormBlock, Conversion.None),
        81 => (VkFormat.Bc4SnormBlock, Conversion.None),
        83 => (VkFormat.Bc5UnormBlock, Conversion.None),
        84 => (VkFormat.Bc5SnormBlock, Conversion.None),
        85 => (VkFormat.R5G6B5UnormPack16, Conversion.None),
        86 => (VkFormat.A1R5G5B5UnormPack16, Conversion.None),
        87 => (VkFormat.B8G8R8A8Unorm, Conversion.None),
        88 => (VkFormat.B8G8R8A8Unorm, Conversion.BgrxToBgra),
        91 => (VkFormat.B8G8R8A8Srgb, Conversion.None),
        93 => (VkFormat.B8G8R8A8Srgb, Conversion.BgrxToBgra),
        95 => (VkFormat.Bc6hUfloatBlock, Conversion.None),
        96 => (VkFormat.Bc6hSfloatBlock, Conversion.None),
        98 => (VkFormat.Bc7UnormBlock, Conversion.None),
        99 => (VkFormat.Bc7SrgbBlock, Conversion.None),
        115 => (VkFormat.A4R4G4B4UnormPack16, Conversion.None),
        _ => (VkFormat.Undefined, Conversion.None),
    };

    private static VkFormat FromFourCC(uint fourCC)
    {
        if (fourCC == FourCC("DXT1")) return VkFormat.Bc1RgbaUnormBlock;
        if (fourCC == FourCC("DXT2") || fourCC == FourCC("DXT3")) return VkFormat.Bc2UnormBlock;
        if (fourCC == FourCC("DXT4") || fourCC == FourCC("DXT5")) return VkFormat.Bc3UnormBlock;
        if (fourCC == FourCC("ATI1") || fourCC == FourCC("BC4U")) return VkFormat.Bc4UnormBlock;
        if (fourCC == FourCC("BC4S")) return VkFormat.Bc4SnormBlock;
        if (fourCC == FourCC("ATI2") || fourCC == FourCC("BC5U")) return VkFormat.Bc5UnormBlock;
        if (fourCC == FourCC("BC5S")) return VkFormat.Bc5SnormBlock;

        // D3DFORMAT values stored in the FourCC field
        return fourCC switch
        {
            36 => VkFormat.R16G16B16A16Unorm,
            111 => VkFormat.R16Sfloat,
            112 => VkFormat.R16G16Sfloat,
            113 => VkFormat.R16G16B16A16Sfloat,
            114 => VkFormat.R32Sfloat,
            115 => VkFormat.R32G32Sfloat,
            116 => VkFormat.R32G32B32A32Sfloat,
            _ => VkFormat.Undefined,
        };
    }

    private static (VkFormat, Conversion) FromMasks(uint flags, uint bits, uint r, uint g, uint b, uint a)
    {
        var hasAlpha = (flags & PixelFormatAlphaPixels) != 0;

        if ((flags & PixelFormatRgb) != 0)
        {
            if (bits == 32 && r == 0x00FF0000 && g == 0x0000FF00 && b == 0x000000FF)
                return hasAlpha && a == 0xFF000000 ? (VkFormat.B8G8R8A8Unorm, Conversion.None) : (VkFormat.B8G8R8A8Unorm, Conversion.BgrxToBgra);
            if (bits == 32 && r == 0x000000FF && g == 0x0000FF00 && b == 0x00FF0000)
                return hasAlpha && a == 0xFF000000 ? (VkFormat.R8G8B8A8Unorm, Conversion.None) : (VkFormat.R8G8B8A8Unorm, Conversion.RgbxToRgba);
            if (bits == 32 && r == 0x3FF00000 && g == 0x000FFC00 && b == 0x000003FF)
                return (VkFormat.A2R10G10B10UnormPack32, Conversion.None);
            if (bits == 32 && r == 0x000003FF && g == 0x000FFC00 && b == 0x3FF00000)
                return (VkFormat.A2B10G10R10UnormPack32, Conversion.None);
            if (bits == 24 && r == 0x00FF0000 && g == 0x0000FF00 && b == 0x000000FF)
                return (VkFormat.B8G8R8A8Unorm, Conversion.Bgr24ToBgra);
            if (bits == 16 && r == 0xF800 && g == 0x07E0 && b == 0x001F)
                return (VkFormat.R5G6B5UnormPack16, Conversion.None);
            if (bits == 16 && r == 0x7C00 && g == 0x03E0 && b == 0x001F)
                return (VkFormat.A1R5G5B5UnormPack16, Conversion.None);
            if (bits == 16 && r == 0x0F00 && g == 0x00F0 && b == 0x000F)
                return (VkFormat.A4R4G4B4UnormPack16, Conversion.None);
        }
        else if ((flags & PixelFormatLuminance) != 0)
        {
            if (bits == 8)
                return (VkFormat.R8G8B8A8Unorm, Conversion.L8ToRgba);
            if (bits == 16 && hasAlpha)
                return (VkFormat.R8G8B8A8Unorm, Conversion.L8A8ToRgba);
        }
        else if ((flags & PixelFormatAlpha) != 0 && bits == 8)
        {
            return (VkFormat.R8G8B8A8Unorm, Conversion.A8ToRgba);
        }

        return (VkFormat.Undefined, Conversion.None);
    }

    // Texel block dimensions and bytes per block (1x1 for uncompressed formats)
    public static (uint Width, uint Height, uint Bytes) BlockInfo(VkFormat format) => format switch
    {
        VkFormat.Bc1RgbaUnormBlock or VkFormat.Bc1RgbaSrgbBlock or VkFormat.Bc4UnormBlock or VkFormat.Bc4SnormBlock => (4, 4, 8),
        VkFormat.Bc2UnormBlock or VkFormat.Bc2SrgbBlock or VkFormat.Bc3UnormBlock or VkFormat.Bc3SrgbBlock
            or VkFormat.Bc5UnormBlock or VkFormat.Bc5SnormBlock or VkFormat.Bc6hUfloatBlock or VkFormat.Bc6hSfloatBlock
            or VkFormat.Bc7UnormBlock or VkFormat.Bc7SrgbBlock => (4, 4, 16),
        VkFormat.R32G32B32A32Sfloat => (1, 1, 16),
        VkFormat.R16G16B16A16Sfloat or VkFormat.R16G16B16A16Unorm or VkFormat.R32G32Sfloat => (1, 1, 8),
        VkFormat.R8G8B8A8Unorm or VkFormat.R8G8B8A8Srgb or VkFormat.B8G8R8A8Unorm or VkFormat.B8G8R8A8Srgb
            or VkFormat.A2B10G10R10UnormPack32 or VkFormat.A2R10G10B10UnormPack32 or VkFormat.B10G11R11UfloatPack32
            or VkFormat.R16G16Sfloat or VkFormat.R16G16Unorm or VkFormat.R32Sfloat => (1, 1, 4),
        VkFormat.R8G8Unorm or VkFormat.R16Sfloat or VkFormat.R16Unorm
            or VkFormat.R5G6B5UnormPack16 or VkFormat.A1R5G5B5UnormPack16 or VkFormat.A4R4G4B4UnormPack16 => (1, 1, 2),
        VkFormat.R8Unorm => (1, 1, 1),
        _ => throw new NotSupportedException($"Unknown block size for {format}"),
    };

    // The stock renderer samples every texture except normal maps as sRGB (MyFileTexture: MakeSrgb)
    public static VkFormat MakeSrgb(VkFormat format) => format switch
    {
        VkFormat.R8G8B8A8Unorm => VkFormat.R8G8B8A8Srgb,
        VkFormat.B8G8R8A8Unorm => VkFormat.B8G8R8A8Srgb,
        VkFormat.Bc1RgbaUnormBlock => VkFormat.Bc1RgbaSrgbBlock,
        VkFormat.Bc2UnormBlock => VkFormat.Bc2SrgbBlock,
        VkFormat.Bc3UnormBlock => VkFormat.Bc3SrgbBlock,
        VkFormat.Bc7UnormBlock => VkFormat.Bc7SrgbBlock,
        _ => format,
    };

    private static uint Read(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset));

    private static uint FourCC(string code) => (uint)(code[0] | (code[1] << 8) | (code[2] << 16) | (code[3] << 24));
}
