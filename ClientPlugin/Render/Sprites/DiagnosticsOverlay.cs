using System;
using System.Diagnostics;
using ClientPlugin.Render.Vulkan;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Render.Sprites;

internal struct DiagnosticsStats
{
    public bool Visible;
    public double FrameMilliseconds;
    public double RawFrameMilliseconds;
    public double Fps;
    public uint Triangles;
    public uint Parts;
    public uint Instances;
    public uint Culled;
    public int DrawnActors;
    public int CulledActors;
    public int GpuParts;
    public uint DirectTriangles;
    public int TextRuns;
    public int TextGlyphs;
    public ulong GpuMemoryBytes;
    public ulong GpuMemoryTotalBytes;
}

internal sealed class DiagnosticsOverlay
{
    private const int HistoryLength = 120;
    private const double SampleSeconds = 0.5;
    private const float X = 10f;
    private const float Y = 10f;
    private const float Width = 400f;
    private const float Height = 248f;
    private const float GraphHeight = 42f;
    private const float TextScale = 0.62f;

    private readonly double[] historyAverage = new double[HistoryLength];
    private readonly double[] historyMax = new double[HistoryLength];
    private readonly double[] historyTime = new double[HistoryLength];
    private int historyIndex;
    private int historyCount;
    private bool sampling;
    private double sampleStart;
    private double sampleSum;
    private double sampleMax;
    private int sampleFrames;
    private double timeNow;

    public void Draw(SpriteBatcher batcher, RenderFont font, DiagnosticsStats stats,
        Action<string, Vector2D, float> addText)
    {
        if (!stats.Visible)
        {
            Reset();
            return;
        }

        Sample(stats);

        AddSolid(batcher, X, Y, Width, Height, new Color(0f, 0f, 0f, 0.62f));
        AddSolid(batcher, X, Y, Width, 2f, new Color(0.25f, 0.8f, 1f, 1f));

        if (font == null)
            return;

        var line = 0;
        DrawLine(font, addText, ref line,
            $"Vulkan  {stats.Fps:0.0} fps   frame {stats.FrameMilliseconds:0.00} ms");
        DrawLine(font, addText, ref line, $"GPU tris {stats.Triangles:N0}  parts {stats.Parts:N0}  inst {stats.Instances:N0}");
        DrawLine(font, addText, ref line, $"Drawn {stats.DrawnActors}  culled {stats.CulledActors}  GPU parts {stats.GpuParts:N0}");
        DrawLine(font, addText, ref line, $"GPU culled {stats.Culled:N0}  direct tris {stats.DirectTriangles:N0}");
        DrawLine(font, addText, ref line, $"GPU text {stats.TextRuns} runs  {stats.TextGlyphs} glyphs");
        var memoryPercent = stats.GpuMemoryTotalBytes > 0
            ? stats.GpuMemoryBytes * 100.0 / stats.GpuMemoryTotalBytes
            : 0;
        DrawLine(font, addText, ref line,
            $"GPU mem {FormatBytes(stats.GpuMemoryBytes)} / {FormatBytes(stats.GpuMemoryTotalBytes)}  {memoryPercent:0}%");
        DrawLine(font, addText, ref line,
            $"60s avg {AverageWindow():0.00} ms  60s max {MaxWindow():0.00} ms");

        var graphX = X + 8f;
        var graphY = Y + Height - GraphHeight - 8f;
        var graphWidth = Width - 16f;
        var step = graphWidth / HistoryLength;
        for (var i = 0; i < historyCount; i++)
        {
            var index = (historyIndex - historyCount + i + HistoryLength * 2) % HistoryLength;
            var ms = historyMax[index];
            var normalized = Math.Clamp(ms / 33.0, 0.0, 1.0);
            var h = (float)(normalized * GraphHeight);
            var color = ms < 16.7 ? new Color(0.3f, 0.9f, 0.4f, 1f)
                : ms < 33.0 ? new Color(1f, 0.8f, 0.2f, 1f)
                : new Color(1f, 0.3f, 0.3f, 1f);
            AddSolid(batcher, graphX + i * step, graphY + GraphHeight - h, Math.Max(step - 0.5f, 0.5f), h, color);
        }

        AddSolid(batcher, graphX, graphY + GraphHeight, graphWidth, 1f, new Color(1f, 1f, 1f, 0.35f));
    }

    private void Sample(DiagnosticsStats stats)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        timeNow = now;
        if (!sampling)
        {
            sampling = true;
            sampleStart = now;
        }

        var ms = stats.RawFrameMilliseconds > 0 ? stats.RawFrameMilliseconds : stats.FrameMilliseconds;
        sampleSum += ms;
        sampleMax = Math.Max(sampleMax, ms);
        sampleFrames++;

        if (now - sampleStart < SampleSeconds)
            return;

        historyAverage[historyIndex] = sampleSum / Math.Max(sampleFrames, 1);
        historyMax[historyIndex] = sampleMax;
        historyTime[historyIndex] = now;
        historyIndex = (historyIndex + 1) % HistoryLength;
        if (historyCount < HistoryLength)
            historyCount++;
        sampleStart = now;
        sampleSum = 0;
        sampleMax = 0;
        sampleFrames = 0;
    }

    private double AverageWindow()
    {
        var cutoff = timeNow - HistoryLength * SampleSeconds;
        var sum = 0.0;
        var count = 0;
        for (var i = 0; i < historyCount; i++)
        {
            if (historyTime[i] < cutoff)
                continue;
            sum += historyAverage[i];
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    private double MaxWindow()
    {
        var cutoff = timeNow - HistoryLength * SampleSeconds;
        var max = 0.0;
        for (var i = 0; i < historyCount; i++)
        {
            if (historyTime[i] < cutoff)
                continue;
            max = Math.Max(max, historyMax[i]);
        }

        return max;
    }

    private void Reset()
    {
        historyIndex = 0;
        historyCount = 0;
        sampling = false;
        sampleStart = 0;
        sampleSum = 0;
        sampleMax = 0;
        sampleFrames = 0;
        Array.Clear(historyAverage);
        Array.Clear(historyMax);
        Array.Clear(historyTime);
    }

    private static string FormatBytes(ulong bytes) => bytes >= 1ul << 30
        ? $"{bytes / (double)(1ul << 30):0.0} GB"
        : $"{bytes / (double)(1ul << 20):0} MB";

    private static void DrawLine(RenderFont font, Action<string, Vector2D, float> addText, ref int line, string text)
    {
        var y = Y + 10f + line * (font.GameFont.LineHeight * TextScale * 0.7783783674240112f + 2f);
        addText(text, new Vector2D(X + 8f, y), TextScale);
        line++;
    }

    private static void AddSolid(SpriteBatcher batcher, float x, float y, float width, float height, Color color)
    {
        batcher.Add((uint)BindlessDescriptors.TextureSlot.White, Vector2.One, color, Vector2.Zero, Vector2.UnitX,
            null, new RectangleF(x, y, width, height), ignoreBounds: true, 0,
            (uint)BindlessDescriptors.TextureSlot.White);
    }
}
