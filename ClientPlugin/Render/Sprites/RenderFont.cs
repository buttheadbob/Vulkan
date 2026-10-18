using System.Collections.Generic;
using System.IO;
using ClientPlugin.Render.Textures;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace ClientPlugin.Render.Sprites;

// A loaded font: the game's MyFont (glyph metrics, kerning) plus its bitmap pages in the texture cache.
// Text layout is a port of the stock MyRenderFont, so text is placed exactly as with the D3D11 renderer.
internal sealed class RenderFont
{
    // MyRenderFont applies this to every scale
    private const double ScaleFactor = 0.7783783674240112;
    private const float BaselineOffset = 3.8333333f;

    private readonly MyFont font;
    private readonly Color colorMask;
    private readonly Dictionary<int, TextureCache.Entry> pages = new();
    private readonly bool premultipliedAlpha;

    private readonly List<MyGlyphInfo> glyphs = new();
    private List<int> lineOffsets = new();

    public string Path { get; }

    internal MyFont GameFont => font;
    internal IReadOnlyDictionary<int, TextureCache.Entry> PageEntries => pages;
    internal Color FontColorMask => colorMask;

    internal bool IsPagePremultiplied(int bitmapId) =>
        pages.TryGetValue(bitmapId, out var page) && IsPremultiplied(page);

    public RenderFont(string path, Color colorMask, MyFont font, TextureCache textures)
    {
        Path = path;
        this.colorMask = colorMask;
        this.font = font;

        foreach (var (id, bitmap) in font.BitmapInfoById)
        {
            var name = System.IO.Path.Combine(font.FontDirectory, bitmap.strFilename);
            pages[id] = textures.Get(name, waitTillLoaded: true);

            // MyRenderFont: raw RGBA pages are premultiplied on load, "FontDataPA" pages are authored premultiplied
            premultipliedAlpha |= bitmap.strFilename.Contains("FontDataPA")
                                  || !bitmap.strFilename.EndsWith(".dds", System.StringComparison.OrdinalIgnoreCase);
        }
    }

    // Raw RGBA pages are only known after decoding, like MyRenderFont.OnFormatChanged
    private bool IsPremultiplied(TextureCache.Entry page) => premultipliedAlpha || page.Pending?.IsRawRgba == true || page.Image?.Format is
        Vortice.Vulkan.VkFormat.R8G8B8A8Srgb or Vortice.Vulkan.VkFormat.B8G8R8A8Srgb;

    public float DrawString(SpriteBatcher batcher, Vector2D position, Color color, string text, double scale, bool ignoreBounds, double maxTextWidth)
    {
        double pxWidth = 0;
        if (string.IsNullOrEmpty(text) || scale == 0 || pages.Count == 0)
            return 0;

        PrepareGlyphs(text);
        lineOffsets.Clear();

        scale *= ScaleFactor;
        var cLast = '\0';
        var vAt = position;
        var spacing = font.Spacing * scale;
        color *= colorMask;
        var line = 0;
        var lineOffset = 0;
        var baseColor = color;

        for (var j = 0; j < text.Length; j++)
        {
            var c = text[j];
            var info = glyphs[j];
            if (c == '\n')
            {
                if (lineOffsets.Count == 0)
                    lineOffsets = GetLineOffsets(text);
                lineOffset = 0;
                vAt.X = position.X;
                line++;
                pxWidth = 0;
                continue;
            }

            if (info == null)
                continue;

            if (line >= 1 && lineOffsets.Count >= line)
                lineOffset = lineOffsets[line - 1];

            color = IsIcon(c) ? new Color(Color.White, baseColor.A / 255f) : baseColor;

            if (j + 1 != text.Length && glyphs[j + 1] != null
                && vAt.X - position.X + font.ComputeScaledAdvanceWithKern(c, info, cLast, scale) + font.ComputeScaledAdvanceWithKern('…', glyphs[j + 1], c, scale) >= maxTextWidth)
            {
                DrawGlyph(batcher, info, ref position, scale, color, ref pxWidth, ref cLast, ref vAt, line, '…', ignoreBounds, lineOffset);
                pxWidth += spacing;
                vAt.X += spacing;

                // MyRenderFont's skip loop compares a stale character and so runs to the end of the text:
                // nothing after the ellipsis is drawn, not even further lines
                break;
            }

            DrawGlyph(batcher, info, ref position, scale, color, ref pxWidth, ref cLast, ref vAt, line, c, ignoreBounds, lineOffset);
            if (j < text.Length - 1)
            {
                pxWidth += spacing;
                vAt.X += spacing;
            }
        }

        return (float)pxWidth;
    }

    public float DrawStringAligned(SpriteBatcher batcher, Vector2D position, Color color, string text, double scale, bool ignoreBounds,
        double maxTextWidth, int textureWidthInPx, MyRenderTextAlignmentEnum alignment)
    {
        double pxWidth = 0;
        if (pages.Count == 0 || string.IsNullOrEmpty(text))
            return 0;

        scale *= ScaleFactor;
        var cLast = '\0';
        var vAt = position;
        var spacing = font.Spacing * scale;
        color *= colorMask;
        var line = 0;
        var lineOffset = 0;

        PrepareGlyphs(text);
        lineOffsets.Clear();

        vAt.X = position.X + AlignmentOffset(text, 0, cLast, scale, spacing, alignment, textureWidthInPx);
        var baseColor = color;

        for (var j = 0; j < text.Length; j++)
        {
            var c = text[j];
            var info = glyphs[j];
            color = IsIcon(c) ? new Color(Color.White, baseColor.A / 255f) : baseColor;

            if (c == '\n')
            {
                // MyRenderFont passes scale and spacing swapped here; kept for identical placement
                vAt.X = position.X + AlignmentOffset(text, j + 1, cLast, spacing, scale, alignment, textureWidthInPx);
                line++;
                if (lineOffsets.Count == 1)
                    lineOffsets = GetLineOffsets(text);
                continue;
            }

            if (info == null)
                continue;

            if (line >= 1 && lineOffsets.Count >= line)
                lineOffset = lineOffsets[line - 1];

            if (maxTextWidth < double.PositiveInfinity)
            {
                var advance = font.ComputeScaledAdvanceWithKern(cLast, c, scale);
                var ellipsis = font.ComputeScaledAdvanceWithKern('…', c, scale);
                if (vAt.X - position.X + advance + ellipsis >= maxTextWidth && j + 1 != text.Length)
                {
                    DrawGlyph(batcher, info, ref position, scale, color, ref pxWidth, ref cLast, ref vAt, line, '…', ignoreBounds, lineOffset);
                    pxWidth += spacing;
                    vAt.X += spacing;
                    while (j < text.Length && text[j] != '\n')
                        j++;
                    if (j < text.Length && text[j] == '\n')
                        j--;
                    continue;
                }
            }

            DrawGlyph(batcher, info, ref position, scale, color, ref pxWidth, ref cLast, ref vAt, line, c, ignoreBounds, lineOffset);
            if (j < text.Length - 1)
            {
                pxWidth += spacing;
                vAt.X += spacing;
            }
        }

        return (float)pxWidth;
    }

    private void DrawGlyph(SpriteBatcher batcher, MyGlyphInfo glyph, ref Vector2D origin, double scale, Color color,
        ref double pxWidth, ref char cLast, ref Vector2D vAt, int line, char c, bool ignoreBounds, double lineOffset)
    {
        if (font.KernEnabled)
        {
            var kern = font.CalcKern(cLast, c);
            vAt.X += kern * scale;
            pxWidth += kern * scale;
            cLast = c;
        }

        vAt.Y = origin.Y + (glyph.pxLeftSideBearing + glyph.pxHeightOffset + BaselineOffset + (float)(line * font.LineHeight)) * scale;
        vAt.Y += lineOffset * scale;
        vAt.X += glyph.pxLeftSideBearing * scale;

        if (glyph.pxWidth != 0 && glyph.pxHeight != 0 && pages.TryGetValue(glyph.nBitmapID, out var page))
        {
            var source = new Rectangle(glyph.pxLocX, glyph.pxLocY, glyph.pxWidth, glyph.pxHeight);
            var destination = new RectangleF((float)vAt.X, (float)vAt.Y, (float)(scale * source.Width), (float)(scale * source.Height));
            var flags = IsPremultiplied(page) ? 0 : SpriteFlags.PremultiplyTexture;
            batcher.Add(TextureCache.SlotOf(page), page.Size, color, Vector2.Zero, Vector2.UnitX, source, destination,
                ignoreBounds, flags, (uint)Vulkan.BindlessDescriptors.TextureSlot.White);
        }

        pxWidth += (int)glyph.pxAdvanceWidth * scale;
        vAt.X += (glyph.pxAdvanceWidth - glyph.pxLeftSideBearing) * scale;
    }

    private void PrepareGlyphs(string text)
    {
        glyphs.Clear();
        foreach (var c in text)
            glyphs.Add(font.TryGetGlyphInfo(c, out var info) ? info : null);
    }

    // Controller button icons keep their own colors
    private static bool IsIcon(char c) => c is '' or '' or '';

    private double LineLength(string text, int start, char cLast, double scale, double spacing)
    {
        double length = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                length += (i - start) * spacing;
                break;
            }

            length += font.ComputeScaledAdvanceWithKern(text[i], cLast, scale);
            if (i == text.Length - 1)
                length += (i - start) * spacing;
        }

        return length;
    }

    private double AlignmentOffset(string text, int start, char cLast, double scale, double spacing, MyRenderTextAlignmentEnum alignment, int textureWidthInPx)
    {
        if (alignment is not (MyRenderTextAlignmentEnum.Align_Center or MyRenderTextAlignmentEnum.Align_Right))
            return 0;

        var free = textureWidthInPx - LineLength(text, start, cLast, scale, spacing);
        return alignment == MyRenderTextAlignmentEnum.Align_Center ? free / 2 : free;
    }

    // Extra vertical offset per line when a line has glyphs taller than the font's average line height
    private List<int> GetLineOffsets(string text)
    {
        var list = new List<int> { 0 };
        if (string.IsNullOrEmpty(text))
            return list;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            var lineHeight = font.AverageLineHeight;
            var taller = false;
            for (var j = i + 1; j < text.Length && text[j] != '\n'; j++)
            {
                var glyph = glyphs[j];
                if (glyph == null)
                    continue;

                var height = glyph.pxHeight + glyph.pxHeightOffset;
                if (height > lineHeight)
                {
                    lineHeight = height;
                    if (lineHeight > font.AverageLineHeight)
                        taller = true;
                }
            }

            list.Add(list[^1]);
            if (taller)
                list[^1] += lineHeight - font.AverageLineHeight;
        }

        return list;
    }
}
