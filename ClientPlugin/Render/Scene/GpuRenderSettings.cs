using System.Runtime.InteropServices;
using VRageMath;

namespace ClientPlugin.Render.Scene;

// Persistent GPU-resident user settings. Uploaded only when Config changes, never per frame.
// Must match RenderSettings in Shaders/Common/Bindless.slang (std430, 48 bytes).
[StructLayout(LayoutKind.Sequential)]
internal struct GpuRenderSettings
{
    public Vector4 Tuning0; // x: sun, y: ambient, z: direct specular, w: metalness
    public Vector4 Tuning1; // x: emissive, y: dynamic lights, z: IBL reflection, w: screen
    public Vector4 Post;    // x: exposure multiplier, y: bloom strength

    public const int Size = 48;
}
