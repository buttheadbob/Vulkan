using System;
using ClientPlugin.Render.Vulkan;
using VRageMath;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// One reverse-Z directional shadow map. Three maps are fitted around the camera: a tight near
// cascade for close-up texel density, a mid cascade and a far cascade. Each map has its own
// bindless slot; the shader picks by view distance and blends across the split.
internal sealed class ShadowMap : IDisposable
{
    public const uint Size = 2048;
    public const int Count = 3;
    private const float LightDistance = 600f;

    // Far distance of each cascade (metres). The near cascade is deliberately tight so a ship
    // 2-5 m away does not get 5 cm shadow texels.
    public static readonly float[] SplitEnds = { 15f, 40f, 150f };

    // Light-space ortho half-extent per cascade. Must cover the camera frustum slice at the
    // cascade split; texel sizes are ~2.2 cm / 5.4 cm / 19.5 cm.
    public static readonly float[] Extents = { 23f, 55f, 200f };

    private readonly GpuContext context;
    private GpuImage image;

    public GpuImage Image => image;
    public uint Slot { get; private set; }
    public VkImageLayout Layout = VkImageLayout.Undefined;
    public bool Ready => image != null;
    public float Extent { get; }

    public ShadowMap(GpuContext context, float extent)
    {
        this.context = context;
        Extent = extent;
    }

    public void Ensure()
    {
        if (image != null)
            return;

        image = GpuImage.Create2D(context, Size, Size, 1, 1, VkFormat.D32Sfloat,
            VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled);
        Slot = context.Bindless.AllocateTextureSlot();
        context.Bindless.WriteTexture(Slot, image.View);
    }

    // Camera-relative light matrix. The cascade centre is snapped to whole light-space texels so a
    // moving camera does not make the shadow edges crawl.
    public static Matrix BuildViewProjection(Vector3D sunDirection, Vector3 center, float extent)
    {
        var toSun = (Vector3)(-sunDirection);
        if (toSun.LengthSquared() < 1e-8f)
            toSun = new Vector3(0.3f, 0.8f, -0.2f);
        toSun.Normalize();

        var up = Math.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        var rotation = Matrix.CreateLookAt(toSun * LightDistance, Vector3.Zero, up);
        var right = new Vector3(rotation.M11, rotation.M12, rotation.M13);
        var lightUp = new Vector3(rotation.M21, rotation.M22, rotation.M23);
        var forward = new Vector3(rotation.M31, rotation.M32, rotation.M33);
        var texel = 2f * extent / Size;
        var u = MathF.Round(Vector3.Dot(center, right) / texel) * texel;
        var v = MathF.Round(Vector3.Dot(center, lightUp) / texel) * texel;
        center = right * u + lightUp * v + forward * Vector3.Dot(center, forward);

        var view = Matrix.CreateLookAt(center + toSun * LightDistance, center, up);
        var projection = Matrix.CreateOrthographicOffCenter(-extent, extent, -extent, extent, 0.1f, 1200f);

        // Reverse Z: near -> 1, far -> 0.
        projection.M33 = -projection.M33;
        projection.M43 = 1f - projection.M43;

        return view * projection;
    }

    public void Dispose()
    {
        if (image == null)
            return;

        context.Bindless.FreeTextureSlot(Slot);
        image.Dispose();
        image = null;
    }
}
