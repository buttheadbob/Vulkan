using System;
using ClientPlugin.Render.Vulkan;
using VRageMath;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// One reverse-Z directional shadow map. Four maps are fitted around the camera: a tight near
// cascade for close-up texel density and three progressively wider cascades out to the configured
// shadow distance. Each map has its own bindless slot; the shader picks by view distance and blends
// across the split.
internal sealed class ShadowMap : IDisposable
{
    public const uint Size = 4096;
    public const int Count = 4;
    private const float LightDistance = 600f;

    // Far distance of each cascade (metres). The near cascade stays tight for close-up quality; the
    // remaining cascades grow out to the configured shadow distance so quality steps are spread over
    // a much wider range instead of dropping sharply just past 40 m.
    public static readonly float[] SplitEnds = { 15f, 55f, 125f, 250f };

    // Light-space ortho half-extent per cascade, fitted to the camera frustum slice every frame.
    // Texel sizes at the default 250 m are ~0.6 cm / 2.5 cm / 5.5 cm / 9 cm.
    public static readonly float[] Extents = { 23f, 75f, 170f, 340f };

    // Fits four cascades inside the quality distance selected by the user. The near cascade never
    // grows past ~15 m, so close-up shadows keep their sub-centimetre texels regardless of the far
    // distance.
    public static void Configure(float qualityDistance)
    {
        var far = Math.Clamp(qualityDistance, 50f, 1000f);
        var near = Math.Min(15f, far * 0.3f);
        // Cap the two middle cascades so raising the slider towards 1 km does not degrade the
        // close/mid texels the player actually looks at; only the final cascade grows very wide.
        var mid1 = Math.Clamp(far * 0.22f, near * 1.5f, 70f);
        var mid2 = Math.Clamp(far * 0.5f, mid1 * 1.8f, 300f);

        SplitEnds[0] = near;
        SplitEnds[1] = Math.Min(mid1, far);
        SplitEnds[2] = Math.Min(mid2, far);
        SplitEnds[3] = far;
    }

    // Fits the light-space footprint of one camera frustum slice. The previous fixed 1.35x split
    // estimates wasted more than half the shadow map on a typical FOV, which is what made nearby
    // shadow edges read as large blocks. A small margin keeps off-screen casters from popping.
    public static float FitExtent(CameraState camera, float near, float far, Vector3D sunDirection)
    {
        var forward = -new Vector3(camera.ViewAt0.M31, camera.ViewAt0.M32, camera.ViewAt0.M33);
        if (forward.LengthSquared() < 1e-8f)
            forward = -Vector3.Forward;
        forward.Normalize();
        var right = new Vector3(camera.ViewAt0.M11, camera.ViewAt0.M12, camera.ViewAt0.M13);
        var up = new Vector3(camera.ViewAt0.M21, camera.ViewAt0.M22, camera.ViewAt0.M23);
        right.Normalize();
        up.Normalize();

        var tanH = MathF.Tan(MathF.Max(camera.FovH, 0.1f) * 0.5f);
        var tanV = MathF.Tan(MathF.Max(camera.FovV, 0.1f) * 0.5f);

        var toSun = (Vector3)(-sunDirection);
        if (toSun.LengthSquared() < 1e-8f)
            toSun = new Vector3(0.3f, 0.8f, -0.2f);
        toSun.Normalize();
        var lightUp = Math.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        var rotation = Matrix.CreateLookAt(toSun * LightDistance, Vector3.Zero, lightUp);
        var lightRight = new Vector3(rotation.M11, rotation.M12, rotation.M13);
        var lightUpAxis = new Vector3(rotation.M21, rotation.M22, rotation.M23);

        var sliceCenter = forward * ((near + far) * 0.5f);
        var halfH = right * (far * tanH);
        var halfV = up * (far * tanV);

        var extent = 0f;
        for (var sx = -1; sx <= 1; sx += 2)
        {
            for (var sy = -1; sy <= 1; sy += 2)
            {
                var cornerFromCamera = forward * far + halfH * sx + halfV * sy;
                var cornerFromCenter = cornerFromCamera - sliceCenter;
                extent = MathF.Max(extent, MathF.Abs(Vector3.Dot(cornerFromCenter, lightRight)));
                extent = MathF.Max(extent, MathF.Abs(Vector3.Dot(cornerFromCenter, lightUpAxis)));
            }
        }

        return MathF.Max(extent * 1.12f, 1f);
    }

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
