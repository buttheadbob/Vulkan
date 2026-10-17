// GUI sprites and text glyphs. One instance per sprite, drawn as a 4 vertex triangle strip.
// Instances are read through a buffer device address and select their textures bindlessly,
// so sprites with different textures share a single draw.

import Common.Bindless;
import Common.Color;

// Must match ClientPlugin/Render/Sprites/SpriteInstance.cs (scalar layout, 64 bytes)
struct SpriteInstance
{
    float4 scaleShift;      // xy: size, zw: top-left position, in target pixels
    float4 uvOffsetScale;   // xy: uv offset, zw: uv scale
    float4 originRotation;  // xy: rotation origin in target pixels, zw: (cos, sin) of the rotation
    uint color;             // RGBA8, sRGB encoded, not premultiplied
    uint texture;           // Bindless texture slot
    uint mask;              // Bindless texture slot of the alpha mask (TextureSlot.White if none)
    uint flags;             // SpriteFlags
};

enum SpriteFlags : uint
{
    PremultiplyTexture = 1,     // Texture has straight alpha, premultiply after sampling
};

// Must match ClientPlugin/Render/Sprites/SpritePushConstants.cs
struct PushConstants
{
    SpriteInstance* instances;
    float2 targetOffset;        // Clip space offset of the target region
    float2 targetSize;          // Clip space size of the target region
    float2 invResolution;       // 1 / size of the target region in pixels
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
    float2 uv : TEXCOORD0;
    nointerpolation uint texture : TEXCOORD1;
    nointerpolation uint mask : TEXCOORD2;
    nointerpolation uint flags : TEXCOORD3;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    SpriteInstance sprite = push.instances[instanceId];

    // Strip order: (0,1) (0,0) (1,1) (1,0)
    float2 corner = float2(vertexId > 1 ? 1 : 0, (~vertexId) & 1);

    float2 position = corner * sprite.scaleShift.xy + sprite.scaleShift.zw;
    float2 origin = sprite.originRotation.xy;
    float c = sprite.originRotation.z;
    float s = sprite.originRotation.w;
    float2 offset = position - origin;
    position = origin + float2(offset.x * c - offset.y * s, offset.x * s + offset.y * c);

    VertexOutput output;
    // Vulkan clip space has +Y down, matching the pixel coordinates of the GUI
    output.position = float4(position * push.invResolution * push.targetSize + push.targetOffset, 0, 1);
    output.uv = corner * sprite.uvOffsetScale.zw + sprite.uvOffsetScale.xy;

    float4 color = unpackRgba8(sprite.color);
    color.rgb = srgbToLinear(color.rgb) * color.a;  // Premultiplied, linear
    output.color = color;

    output.texture = sprite.texture;
    output.mask = sprite.mask;
    output.flags = sprite.flags;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float4 texel = sampleTexture(input.texture, SamplerSlot.LinearClamp, input.uv);
    if ((input.flags & uint(SpriteFlags.PremultiplyTexture)) != 0)
        texel.rgb *= texel.a;

    float mask = sampleTexture(input.mask, SamplerSlot.LinearClamp, input.uv).r;
    return texel * input.color * mask;
}
