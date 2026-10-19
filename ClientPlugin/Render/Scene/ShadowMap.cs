using System;
using ClientPlugin.Render.Vulkan;
using VRageMath;
using Vortice.Vulkan;

namespace ClientPlugin.Render.Scene;

// One reverse-Z directional shadow map fitted around the camera-relative scene. A single map keeps
// the depth pass cheap; cascades can be added later without touching the sampling contract.
internal sealed class ShadowMap : IDisposable
{
    public const uint Size = 2048;
    public const float Extent = 250f;
    private const float LightDistance = 600f;

    private readonly GpuContext context;
    private GpuImage image;

    public GpuImage Image => image;
    public uint Slot { get; private set; }
    public VkImageLayout Layout = VkImageLayout.Undefined;
    public bool Ready => image != null;

    public ShadowMap(GpuContext context)
    {
        this.context = context;
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

    // Camera-relative light matrix. The camera sits at the origin, the light looks at the nearby
    // scene from the sun, and the projection is reverse-Z so the scene pass can compare directly.
    public static Matrix BuildViewProjection(Vector3D sunDirection)
    {
        var toSun = (Vector3)(-sunDirection);
        if (toSun.LengthSquared() < 1e-8f)
            toSun = new Vector3(0.3f, 0.8f, -0.2f);
        toSun.Normalize();

        var lightPosition = toSun * LightDistance;
        var up = Math.Abs(toSun.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        var view = Matrix.CreateLookAt(lightPosition, Vector3.Zero, up);
        var projection = Matrix.CreateOrthographicOffCenter(-Extent, Extent, -Extent, Extent, 0.1f, 1200f);

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
