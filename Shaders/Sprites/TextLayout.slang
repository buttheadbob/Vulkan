// GPU text layout: one thread per text run, ports the stock RenderFont/MyRenderFont positioning
// into glyph SpriteInstance records. The CPU only uploads the run description and string.

// Must match ClientPlugin/Render/Sprites/SpriteInstance.cs
struct SpriteInstance
{
    float4 scaleShift;
    float4 uvOffsetScale;
    float4 originRotation;
    uint color;
    uint texture;
    uint mask;
    uint flags;
};

// Must match GpuTextRenderer.GpuFontHeader
struct FontHeader
{
    uint glyphCount;
    uint kernEnabled;
    uint pad0;
    uint pad1;
    float lineHeight;
    float averageLineHeight;
    float spacing;
    float pad2;
    uint64_t pad3;
    uint64_t pad4;
};

// Must match GpuTextRenderer.GpuGlyph
struct Glyph
{
    uint pageSlot;
    uint pageW;
    uint pageH;
    uint flags;
    float advance;
    float leftBearing;
    float heightOffset;
    float locX;
    float locY;
    float width;
    float height;
    float pad;
};

// Must match GpuTextRenderer.TextRun
struct TextRun
{
    float2 position;
    float2 pad0;
    float scale;
    float maxWidth;
    float textureWidth;
    uint alignment;
    uint firstChar;
    uint charCount;
    uint color;
    uint glyphBase;
    uint maxGlyphs;
    uint ignoreBounds;
    uint64_t font;
};

struct PushConstants
{
    TextRun* runs;
    uint* chars;
    SpriteInstance* glyphs;
    uint runCount;
    uint pad;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

static const uint GlyphTableOffset = 64;
static const uint GlyphStride = 48;
static const uint KernFirst = 32;
static const uint KernCount = 96;
static const uint KernOffset = GlyphTableOffset + 65536 * GlyphStride;
static const float ScaleFactor = 0.7783783674240112;
static const float BaselineOffset = 3.8333333;

float kernValue(FontHeader* font, uint prev, uint c)
{
    if (font->kernEnabled == 0 || prev < KernFirst || prev >= KernFirst + KernCount
        || c < KernFirst || c >= KernFirst + KernCount)
        return 0.0;

    float* table = (float*)((uint64_t)font + KernOffset);
    return table[(prev - KernFirst) * KernCount + (c - KernFirst)];
}

float lineLength(FontHeader* font, Glyph* glyphs, uint* chars, TextRun run, float scale, float spacing)
{
    float length = 0;
    uint prev = 0;
    for (uint i = 0; i < run.charCount; i++)
    {
        uint c = chars[run.firstChar + i];
        if (c == '\n')
            break;
        if (c >= 65536)
            continue;
        Glyph g = glyphs[c];
        if (g.advance == 0 && g.width == 0 && g.height == 0)
            continue;
        length += kernValue(font, prev, c) * scale;
        length += g.advance * scale;
        if (i + 1 < run.charCount)
            length += spacing;
        prev = c;
    }
    return length;
}

[numthreads(64, 1, 1)]
[shader("compute")]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= push.runCount)
        return;

    TextRun run = push.runs[index];
    FontHeader* font = (FontHeader*)run.font;
    Glyph* glyphs = (Glyph*)(run.font + GlyphTableOffset);
    float scale = run.scale * ScaleFactor;
    float spacing = font->spacing * scale;
    float startX = run.position.x;

    if (run.alignment != 0 && run.textureWidth > 0)
    {
        float free = run.textureWidth - lineLength(font, glyphs, push.chars, run, scale, spacing);
        startX += run.alignment == 1 ? free * 0.5 : free;
    }

    float x = startX;
    float y = run.position.y;
    float line = 0;
    uint prev = 0;
    uint outIndex = run.glyphBase;

    for (uint i = 0; i < run.charCount; i++)
    {
        uint c = push.chars[run.firstChar + i];
        if (c == '\n')
        {
            line += 1;
            x = startX;
            prev = 0;
            continue;
        }

        if (c >= 65536)
            continue;
        Glyph g = glyphs[c];
        if (g.advance == 0 && g.width == 0 && g.height == 0)
            continue;

        if (font->kernEnabled != 0 && prev != 0)
            x += kernValue(font, prev, c) * scale;

        if (g.width > 0 && g.height > 0 && g.pageW > 0 && g.pageH > 0 && outIndex < run.glyphBase + run.maxGlyphs)
        {
            float gx = x + g.leftBearing * scale;
            float gy = y + (g.leftBearing + g.heightOffset + BaselineOffset + line * font->lineHeight) * scale;

            SpriteInstance instance;
            instance.scaleShift = float4(g.width * scale, g.height * scale, gx, gy);
            instance.uvOffsetScale = float4(g.locX / g.pageW, g.locY / g.pageH,
                                            g.width / g.pageW, g.height / g.pageH);
            instance.originRotation = float4(0, 0, 1, 0);
            instance.color = run.color;
            instance.texture = g.pageSlot;
            instance.mask = 0;
            instance.flags = g.flags;
            push.glyphs[outIndex++] = instance;
        }

        x += g.advance * scale;
        if (i + 1 < run.charCount)
            x += spacing;
        prev = c;
    }
}
