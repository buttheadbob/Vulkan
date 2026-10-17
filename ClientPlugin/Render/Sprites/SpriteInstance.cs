using System.Runtime.InteropServices;
using VRageMath;

namespace ClientPlugin.Render.Sprites;

// Must match SpriteInstance in Shaders/Sprites/Sprite.slang (scalar layout)
[StructLayout(LayoutKind.Sequential)]
internal struct SpriteInstance
{
    public const int Size = 64;

    public Vector4 ScaleShift;      // xy: size, zw: top-left position, in target pixels
    public Vector4 UvOffsetScale;   // xy: uv offset, zw: uv scale
    public Vector4 OriginRotation;  // xy: rotation origin in target pixels, zw: (cos, sin) of the rotation
    public uint Color;              // RGBA8, sRGB encoded, not premultiplied
    public uint Texture;            // Bindless texture slot
    public uint Mask;               // Bindless texture slot of the alpha mask
    public uint Flags;              // SpriteFlags
}

// Must match SpriteFlags in Shaders/Sprites/Sprite.slang
internal static class SpriteFlags
{
    public const uint PremultiplyTexture = 1;
}

// Must match PushConstants in Shaders/Sprites/Sprite.slang
[StructLayout(LayoutKind.Sequential)]
internal struct SpritePushConstants
{
    public ulong Instances;
    public Vector2 TargetOffset;
    public Vector2 TargetSize;
    public Vector2 InvResolution;
}
