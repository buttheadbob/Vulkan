using System;
using System.Collections.Generic;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Render.Sprites;

// Collects the sprites of one target (the screen or an offscreen texture) for a frame.
// Geometry follows the stock MySpritesRenderer. Unlike the stock renderer, texture changes do not break
// batches (textures are bindless): a new draw starts only when the scissor rectangle or viewport changes.
internal sealed class SpriteBatcher
{
    public struct Draw
    {
        public int First;
        public int Count;
        public RectangleF? Scissor;     // In target pixels, before the viewport mapping
        public bool IgnoreBounds;       // Full viewport instead of the scaled main viewport
    }

    private readonly SpriteScissorStack scissors = new();
    private SpriteInstance[] instances = new SpriteInstance[1024];
    private readonly List<Draw> draws = new();

    public int InstanceCount { get; private set; }
    public ReadOnlySpan<SpriteInstance> Instances => instances.AsSpan(0, InstanceCount);
    public IReadOnlyList<Draw> Draws => draws;

    public void Clear()
    {
        InstanceCount = 0;
        draws.Clear();
        while (!scissors.Empty)
            scissors.Pop();
    }

    public void PushScissor(Rectangle rectangle) => scissors.Push(rectangle);

    public void PopScissor() => scissors.Pop();

    // textureSize is needed to turn a source rectangle in pixels into texture coordinates
    public void Add(uint texture, Vector2 textureSize, Color color, Vector2 origin, Vector2 tangent,
        Rectangle? sourceRectangle, RectangleF destination, bool ignoreBounds, uint flags, uint mask)
    {
        var scissor = scissors.Peek();
        if (scissor.HasValue)
        {
            var bounds = tangent == Vector2.UnitX ? destination : RotatedBounds(destination, tangent);
            var scissorValue = scissor.Value;
            if (!RectangleF.Intersect(ref scissorValue, ref bounds, out _))
                return;
        }

        var uvOffset = Vector2.Zero;
        var uvScale = Vector2.One;
        if (sourceRectangle.HasValue && textureSize.X > 0 && textureSize.Y > 0)
        {
            var source = sourceRectangle.Value;
            uvOffset = new Vector2(source.Left, source.Top) / textureSize;
            uvScale = new Vector2(source.Width, source.Height) / textureSize;
        }

        StartDrawIfNeeded(scissor, ignoreBounds);

        if (InstanceCount == instances.Length)
            Array.Resize(ref instances, instances.Length * 2);

        instances[InstanceCount++] = new SpriteInstance
        {
            ScaleShift = new Vector4(destination.Size.X, destination.Size.Y, destination.Position.X, destination.Position.Y),
            UvOffsetScale = new Vector4(uvOffset.X, uvOffset.Y, uvScale.X, uvScale.Y),
            OriginRotation = new Vector4(origin.X, origin.Y, tangent.X, tangent.Y),
            Color = color.PackedValue,
            Texture = texture,
            Mask = mask,
            Flags = flags,
        };

        var last = draws[^1];
        last.Count++;
        draws[^1] = last;
    }

    private void StartDrawIfNeeded(RectangleF? scissor, bool ignoreBounds)
    {
        if (draws.Count != 0)
        {
            var current = draws[^1];
            if (current.IgnoreBounds == ignoreBounds && Nullable.Equals(current.Scissor, scissor))
                return;
        }

        draws.Add(new Draw { First = InstanceCount, Count = 0, Scissor = scissor, IgnoreBounds = ignoreBounds });
    }

    private static RectangleF RotatedBounds(RectangleF rect, Vector2 tangent)
    {
        var normal = new Vector2(-tangent.Y, tangent.X);
        var center = rect.Center;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        void Include(Vector2 corner)
        {
            var d = corner - center;
            var p = center + d.X * tangent + d.Y * normal;
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        Include(new Vector2(rect.X, rect.Y));
        Include(new Vector2(rect.Right, rect.Y));
        Include(new Vector2(rect.Right, rect.Bottom));
        Include(new Vector2(rect.X, rect.Bottom));
        return new RectangleF(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }
}
