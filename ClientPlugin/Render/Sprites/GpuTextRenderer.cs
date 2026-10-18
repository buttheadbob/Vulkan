using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender;
using VRageRender.Messages;
using static Vortice.Vulkan.Vulkan;

namespace ClientPlugin.Render.Sprites;

// GPU text: the CPU uploads only a run description (string + style), and a compute shader
// shapes/lays out the glyphs into SpriteInstance records. The existing sprite pipeline then
// draws those instances from the GPU buffer. Font metrics are uploaded once per font.
internal sealed unsafe class GpuTextRenderer : IDisposable
{
    private const uint GlyphCount = 65536;
    private const uint GlyphStride = 48;
    private const uint HeaderSize = 64;
    private const uint GlyphTableOffset = HeaderSize;
    private const uint KernFirst = 32;
    private const uint KernCount = 96;
    private const uint KernOffset = GlyphTableOffset + GlyphCount * GlyphStride;

    // Must match ClientPlugin/Render/Sprites/GpuTextRenderer.cs and Shaders/Sprites/TextLayout.slang
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuFontHeader
    {
        public uint GlyphCount;
        public uint KernEnabled;
        public uint Pad0;
        public uint Pad1;
        public float LineHeight;
        public float AverageLineHeight;
        public float Spacing;
        public float Pad2;
        public ulong Pad3;
        public ulong Pad4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuGlyph
    {
        public uint PageSlot;
        public uint PageW;
        public uint PageH;
        public uint Flags;
        public float Advance;
        public float LeftBearing;
        public float HeightOffset;
        public float LocX;
        public float LocY;
        public float Width;
        public float Height;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextRun
    {
        public Vector2 Position;
        public Vector2 Pad0;
        public float Scale;
        public float MaxWidth;
        public float TextureWidth;
        public uint Alignment;
        public uint FirstChar;
        public uint CharCount;
        public uint Color;
        public uint GlyphBase;
        public uint MaxGlyphs;
        public uint IgnoreBounds;
        public ulong Font;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextPushConstants
    {
        public ulong Runs;
        public ulong Chars;
        public ulong Glyphs;
        public uint RunCount;
        public uint Pad;
    }

    private sealed class GpuFont : IDisposable
    {
        public GpuBuffer Buffer;
        public ulong Address;

        public void Dispose()
        {
            Buffer?.Dispose();
            Buffer = null;
        }
    }

    private sealed class Batch : IDisposable
    {
        public GpuBuffer Runs;
        public GpuBuffer Chars;
        public GpuBuffer Glyphs;
        public readonly List<TextRun> RunList = new();
        public readonly List<uint> CharList = new();
        public int GlyphCount;

        public void Dispose()
        {
            Runs?.Dispose();
            Chars?.Dispose();
            Glyphs?.Dispose();
        }
    }

    private readonly GpuContext context;
    private readonly SpriteRenderer spriteRenderer;
    private readonly Dictionary<int, GpuFont> fonts = new();
    private readonly Batch[] batches = new Batch[FrameLoop.FramesInFlight];
    private readonly VkPipeline pipeline;
    private int slot;
    private int lastRunCount;
    private int lastGlyphCount;

    public int LastRunCount => lastRunCount;
    public int LastGlyphCount => lastGlyphCount;

    public GpuTextRenderer(GpuContext context, SpriteRenderer spriteRenderer)
    {
        this.context = context;
        this.spriteRenderer = spriteRenderer;
        for (var i = 0; i < batches.Length; i++)
            batches[i] = new Batch();
        pipeline = BuildPipeline(context);
    }

    private static VkPipeline BuildPipeline(GpuContext context)
    {
        var api = context.Device.Api;
        var module = ShaderModule.Create(context.Device, ShaderBytecode.TextLayout.Code);
        try
        {
            using var names = new VkStringArray([ShaderBytecode.TextLayout.Main]);
            var stage = new VkPipelineShaderStageCreateInfo
            {
                stage = VkShaderStageFlags.Compute,
                module = module,
                pName = ((byte**)names)[0],
            };
            var createInfo = new VkComputePipelineCreateInfo
            {
                stage = stage,
                layout = context.Bindless.PipelineLayout,
            };

            VkPipeline created;
            api.vkCreateComputePipelines(VkPipelineCache.Null, 1, &createInfo, null, &created)
                .CheckResult("vkCreateComputePipelines (text layout)");
            return created;
        }
        finally
        {
            api.vkDestroyShaderModule(module, null);
        }
    }

    public void BeginFrame(int frameSlot)
    {
        slot = frameSlot;
        var batch = batches[slot];
        batch.RunList.Clear();
        batch.CharList.Clear();
        batch.GlyphCount = 0;
    }

    // Called once per RenderFont. Uploads the glyph metrics, page slots and kerning table.
    public void CreateFont(int id, RenderFont font)
    {
        var buffer = GpuBuffer.Create(context, HeaderSize + GlyphCount * GlyphStride + KernCount * KernCount * sizeof(float),
            VkBufferUsageFlags.StorageBuffer, hostVisible: true);
        var header = (GpuFontHeader*)buffer.Mapped;
        *header = new GpuFontHeader
        {
            GlyphCount = GlyphCount,
            KernEnabled = font.GameFont.KernEnabled ? 1u : 0u,
            LineHeight = font.GameFont.LineHeight,
            AverageLineHeight = font.GameFont.AverageLineHeight,
            Spacing = font.GameFont.Spacing,
        };

        var glyphs = (GpuGlyph*)(buffer.Mapped + GlyphTableOffset);
        for (var i = 0; i < GlyphCount; i++)
        {
            glyphs[i] = default;
            if (!font.GameFont.TryGetGlyphInfo((char)i, out var glyph))
                continue;

            if (!font.PageEntries.TryGetValue(glyph.nBitmapID, out var page) || page == null)
                continue;

            glyphs[i] = new GpuGlyph
            {
                PageSlot = TextureCache.SlotOf(page),
                PageW = (uint)page.Size.X,
                PageH = (uint)page.Size.Y,
                Flags = font.IsPagePremultiplied(glyph.nBitmapID) ? 0u : SpriteFlags.PremultiplyTexture,
                Advance = glyph.pxAdvanceWidth,
                LeftBearing = glyph.pxLeftSideBearing,
                HeightOffset = glyph.pxHeightOffset,
                LocX = glyph.pxLocX,
                LocY = glyph.pxLocY,
                Width = glyph.pxWidth,
                Height = glyph.pxHeight,
            };
        }

        var kern = (float*)(buffer.Mapped + KernOffset);
        for (var y = 0; y < KernCount; y++)
        {
            for (var x = 0; x < KernCount; x++)
                kern[y * KernCount + x] = font.GameFont.CalcKern((char)(KernFirst + x), (char)(KernFirst + y));
        }

        if (fonts.TryGetValue(id, out var previous))
            previous.Dispose();
        fonts[id] = new GpuFont { Buffer = buffer, Address = buffer.DeviceAddress };
    }

    // Returns false when the font is not ready; the caller can fall back to the CPU path.
    public bool AddString(int fontId, RenderFont font, string text, Vector2D position, double scale, Color color,
        bool ignoreBounds, double maxTextWidth, MyRenderTextAlignmentEnum alignment, int textureWidthInPx)
    {
        if (font == null || string.IsNullOrEmpty(text) || !fonts.TryGetValue(fontId, out var gpuFont))
            return false;

        var batch = batches[slot];
        var run = new TextRun
        {
            Position = new Vector2((float)position.X, (float)position.Y),
            Scale = (float)scale,
            MaxWidth = maxTextWidth > 0 ? (float)maxTextWidth : float.MaxValue,
            TextureWidth = textureWidthInPx,
            Alignment = alignment switch
            {
                MyRenderTextAlignmentEnum.Align_Center => 1u,
                MyRenderTextAlignmentEnum.Align_Right => 2u,
                _ => 0u,
            },
            FirstChar = (uint)batch.CharList.Count,
            CharCount = (uint)text.Length,
            Color = (color * font.FontColorMask).PackedValue,
            GlyphBase = (uint)batch.GlyphCount,
            MaxGlyphs = (uint)text.Length + 1,
            IgnoreBounds = ignoreBounds ? 1u : 0u,
            Font = gpuFont.Address,
        };

        foreach (var c in text)
            batch.CharList.Add(c);
        batch.RunList.Add(run);
        batch.GlyphCount += (int)run.MaxGlyphs;
        return true;
    }

    // Records the layout compute. Must run outside a dynamic rendering pass, before the GUI draw.
    public void Prepare(VkCommandBuffer commands)
    {
        var batch = batches[slot];
        lastRunCount = batch.RunList.Count;
        lastGlyphCount = batch.GlyphCount;
        if (batch.RunList.Count == 0)
            return;

        EnsureBuffer(ref batch.Runs, (ulong)(batch.RunList.Count * sizeof(TextRun)));
        EnsureBuffer(ref batch.Chars, (ulong)(batch.CharList.Count * sizeof(uint)));
        EnsureBuffer(ref batch.Glyphs, (ulong)(batch.GlyphCount * SpriteInstance.Size));

        var runs = new Span<TextRun>(batch.Runs.Mapped, batch.RunList.Count);
        for (var i = 0; i < runs.Length; i++)
            runs[i] = batch.RunList[i];
        var chars = new Span<uint>(batch.Chars.Mapped, batch.CharList.Count);
        for (var i = 0; i < chars.Length; i++)
            chars[i] = batch.CharList[i];
        new Span<byte>(batch.Glyphs.Mapped, batch.GlyphCount * SpriteInstance.Size).Clear();

        var api = context.Device.Api;
        var hostBarrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.Host,
            srcAccessMask = VkAccessFlags2.HostWrite,
            dstStageMask = VkPipelineStageFlags2.ComputeShader,
            dstAccessMask = VkAccessFlags2.ShaderRead | VkAccessFlags2.ShaderWrite,
        };
        var hostDependency = new VkDependencyInfo
        {
            memoryBarrierCount = 1,
            pMemoryBarriers = &hostBarrier,
        };
        api.vkCmdPipelineBarrier2(commands, &hostDependency);

        var push = new TextPushConstants
        {
            Runs = batch.Runs.DeviceAddress,
            Chars = batch.Chars.DeviceAddress,
            Glyphs = batch.Glyphs.DeviceAddress,
            RunCount = (uint)batch.RunList.Count,
        };
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Compute, pipeline);
        api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout, VkShaderStageFlags.Compute,
            0, (uint)sizeof(TextPushConstants), &push);
        api.vkCmdDispatch(commands, ((uint)batch.RunList.Count + 63) / 64, 1, 1);

        var drawBarrier = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.ComputeShader,
            srcAccessMask = VkAccessFlags2.ShaderWrite,
            dstStageMask = VkPipelineStageFlags2.VertexShader,
            dstAccessMask = VkAccessFlags2.ShaderRead,
        };
        var drawDependency = new VkDependencyInfo
        {
            memoryBarrierCount = 1,
            pMemoryBarriers = &drawBarrier,
        };
        api.vkCmdPipelineBarrier2(commands, &drawDependency);
    }

    // Draws the glyph instances through the sprite pipeline, after the CPU sprite batch.
    public void RecordDraw(VkCommandBuffer commands, VkFormat targetFormat, SpriteBlend blend,
        Vector2 coordinateSize, VkExtent2D framebuffer, MyViewport boundViewport, MyViewport fullViewport)
    {
        var batch = batches[slot];
        if (batch.RunList.Count == 0 || batch.Glyphs == null)
            return;

        for (var i = 0; i < batch.RunList.Count; i++)
        {
            var run = batch.RunList[i];
            if (run.MaxGlyphs == 0)
                continue;
            spriteRenderer.RecordGpuInstances(commands, targetFormat, blend, coordinateSize, framebuffer,
                boundViewport, fullViewport,
                batch.Glyphs.DeviceAddress + (ulong)run.GlyphBase * SpriteInstance.Size,
                run.MaxGlyphs, run.IgnoreBounds != 0);
        }
    }

    private void EnsureBuffer(ref GpuBuffer buffer, ulong bytes)
    {
        if (buffer != null && buffer.Size >= bytes)
            return;
        buffer?.Dispose();
        buffer = GpuBuffer.Create(context, Math.Max(bytes, 4096), VkBufferUsageFlags.StorageBuffer, hostVisible: true);
    }

    public void Dispose()
    {
        var handle = pipeline;
        context.Release(() => context.Device.Api.vkDestroyPipeline(handle, null));
        foreach (var font in fonts.Values)
            font.Dispose();
        foreach (var batch in batches)
            batch.Dispose();
    }
}
