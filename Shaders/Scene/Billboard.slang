// Camera-facing transparent billboards (thruster flames, particles, glares) into the OIT targets.
// Positions are already camera-relative. One quad per instance, six vertices.

import Common.Bindless;
import Common.Environment;

// Must match ClientPlugin/Render/Scene/BillboardPass.cs
struct BillboardInstance
{
    float4 p0;       // top-left
    float4 p1;       // top-right
    float4 p2;       // bottom-right
    float4 p3;       // bottom-left
    float4 color;    // linear RGBA (intensity already applied to RGB)
    float4 uvRect;   // xy: offset, zw: size
    uint texture;
    uint flags;      // 1: alpha cutout, 2: single-channel texture (BC4/R8)
    float alphaCutout;
    float pad;
};

struct PushConstants
{
    BillboardInstance* instances;
    FrameConstants* frame;
    uint count;
    uint pad;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float3 worldPosition : TEXCOORD1;
    float4 color : TEXCOORD2;
    uint texture : TEXCOORD3;
    uint flags : TEXCOORD4;
    float alphaCutout : TEXCOORD5;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    BillboardInstance billboard = push.instances[instanceId];
    float4 position;
    float2 uv;

    if (vertexId == 0)
    {
        position = billboard.p0;
        uv = float2(0.0, 0.0);
    }
    else if (vertexId == 1)
    {
        position = billboard.p1;
        uv = float2(1.0, 0.0);
    }
    else if (vertexId == 2 || vertexId == 4)
    {
        position = billboard.p2;
        uv = float2(1.0, 1.0);
    }
    else if (vertexId == 3)
    {
        position = billboard.p0;
        uv = float2(0.0, 0.0);
    }
    else
    {
        position = billboard.p3;
        uv = float2(0.0, 1.0);
    }

    FrameConstants frame = *push.frame;
    float4 clip = float4(dot(position, frame.viewProj0),
                         dot(position, frame.viewProj1),
                         dot(position, frame.viewProj2),
                         dot(position, frame.viewProj3));

    VertexOutput output;
    output.position = clip;
    output.uv = billboard.uvRect.xy + uv * billboard.uvRect.zw;
    output.worldPosition = position.xyz;
    output.color = billboard.color;
    output.texture = billboard.texture;
    output.flags = billboard.flags;
    output.alphaCutout = billboard.alphaCutout;
    return output;
}

struct FragmentOutput
{
    float4 accum : SV_Target0;  // premultiplied by alpha * weight, additive
    float reveal : SV_Target1;  // alpha; the reveal target blends dst *= (1 - src)
};

[shader("fragment")]
FragmentOutput fragmentMain(VertexOutput input)
{
    float4 texel = sampleTexture(input.texture, SamplerSlot.LinearClamp, input.uv);
    if ((input.flags & 2u) != 0)
        texel = texel.rrrr;

    float4 color = texel * input.color;
    float sourceAlpha = saturate(max(texel.r, max(texel.g, texel.b)));
    if ((input.flags & 1u) != 0)
        clip(color.a - input.alphaCutout);

    float alpha = saturate(color.a);
    if ((input.flags & 12u) != 0)
        alpha = sourceAlpha;
    if ((input.flags & 4u) != 0)
    {
        bool ion = color.b > color.r;
        float core = smoothstep(ion ? 0.72 : 0.86, 1.0, sourceAlpha);
        float luminance = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
        color.rgb = lerp(color.rgb, luminance.xxx, core * (ion ? 0.4 : 0.18));
    }
    float distanceToCamera = length(input.worldPosition);
    float weight = alpha * clamp(0.03 / (1e-5 + pow(distanceToCamera / 200.0, 4.0)), 0.01, 3000.0);

    FragmentOutput output;
    output.accum = float4(color.rgb * weight, alpha * weight);
    output.reveal = alpha;
    return output;
}

// Reflection-probe variant: a single additive HDR color target instead of OIT accumulation and
// revealage. This is how thruster flames, particle glows and light billboards become visible in
// the dynamic cubemap (the opaque probe pass cannot see them).
[shader("fragment")]
float4 fragmentProbe(VertexOutput input) : SV_Target0
{
    float4 texel = sampleTexture(input.texture, SamplerSlot.LinearClamp, input.uv);
    if ((input.flags & 2u) != 0)
        texel = texel.rrrr;

    float4 color = texel * input.color;
    float sourceAlpha = saturate(max(texel.r, max(texel.g, texel.b)));
    if ((input.flags & 1u) != 0)
        clip(color.a - input.alphaCutout);

    float alpha = saturate(color.a);
    if ((input.flags & 12u) != 0)
        alpha = sourceAlpha;
    if ((input.flags & 4u) != 0)
    {
        bool ion = color.b > color.r;
        float core = smoothstep(ion ? 0.72 : 0.86, 1.0, sourceAlpha);
        float luminance = dot(color.rgb, float3(0.2126, 0.7152, 0.0722));
        color.rgb = lerp(color.rgb, luminance.xxx, core * (ion ? 0.4 : 0.18));
    }

    return float4(color.rgb * alpha, 1.0);
}
