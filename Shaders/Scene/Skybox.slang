// Space background: fullscreen triangle that samples the environment cubemap along the view ray.
// Drawn inside the HDR scene pass before geometry, with depth testing disabled.

import Common.Bindless;
import Common.Environment;

struct PushConstants
{
    FrameConstants* frame;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID)
{
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    VertexOutput output;
    output.position = float4(uv * 2 - 1, 0, 1);
    output.uv = uv;
    return output;
}

struct FragmentOutput
{
    float4 color : SV_Target0;
    float4 gbuffer : SV_Target1;
    float4 gbuffer2 : SV_Target2;
};

FragmentOutput debugOutput(float3 color)
{
    FragmentOutput output;
    output.color = float4(color, 1.0);
    output.gbuffer = float4(0.0, 0.0, 0.0, 0.0);
    output.gbuffer2 = float4(0.0, 0.0, 0.0, 0.0);
    return output;
}

[shader("fragment")]
FragmentOutput fragmentMain(VertexOutput input)
{
    // z = 1 selects the near plane; for a perspective projection that gives the view ray
    float4 ndc = float4(input.uv * 2 - 1, 1, 1);
    float4 world = float4(dot(ndc, push.frame.invViewProj0),
                          dot(ndc, push.frame.invViewProj1),
                          dot(ndc, push.frame.invViewProj2),
                          dot(ndc, push.frame.invViewProj3));

    float3 direction = normalize(world.xyz / world.w);
    int debugView = (int)push.frame.colorizeHsv.w;
    if (debugView == 12)
    {
        // Probe orientation/quality check: show the captured world-space cube as the background.
        return debugOutput(iblColorRaw(*push.frame, direction, 0));
    }
    if (debugView == 13)
    {
        // Same view with the reflection path's probe z-correction, for comparison.
        return debugOutput(iblColorRaw(*push.frame, float3(direction.x, direction.y, -direction.z), 0));
    }
    if (debugView == 14)
    {
        // 2x2 comparison: raw / X-flip / Z-flip / both. The correct variant is the one whose
        // starfield and ship directions match the real background.
        float3 d = direction;
        int variant = (input.uv.x < 0.5 ? 0 : 1) + (input.uv.y < 0.5 ? 0 : 2);
        if ((variant & 1) != 0)
            d.x = -d.x;
        if ((variant & 2) != 0)
            d.z = -d.z;
        return debugOutput(iblColorRaw(*push.frame, d, 0));
    }
    if (debugView == 15)
    {
        // Alignment check: red is the real skybox, green is the raw probe, sampled along the same
        // view ray. Correctly oriented stars overlap and read yellow; a mirrored/swizzled probe
        // leaves separated red/green stars.
        float3 real = skyboxColorReflected(*push.frame, direction) * push.frame.skyboxParams.x;
        float3 probe = iblColorRaw(*push.frame, direction, 0);
        float realLum = dot(real, float3(0.2126, 0.7152, 0.0722));
        float probeLum = dot(probe, float3(0.2126, 0.7152, 0.0722));
        return debugOutput(float3(saturate(realLum * 4.0), saturate(probeLum * 4.0), 0.0));
    }
    if (debugView == 16)
    {
        // Split-screen: left is the real skybox, right is the raw probe. Stars must continue
        // across the vertical seam if the captured cube has the correct orientation.
        float3 color = input.uv.x < 0.5
            ? skyboxColorReflected(*push.frame, direction) * push.frame.skyboxParams.x
            : iblColorRaw(*push.frame, direction, 0);
        return debugOutput(saturate(color * 4.0));
    }
    float3 color = skyboxColorReflected(*push.frame, direction) * push.frame.skyboxParams.x;

    // Stock LightDir.hlsl: the sun is a smoothstep disc around -SunLightDirection.
    float3 toSun = -normalize(push.frame.sunDirection.xyz);
    float cosSun = dot(direction, toSun);
    float disc = smoothstep(push.frame.sunDiscDots.y, push.frame.sunDiscDots.x, cosSun);
    disc *= disc;
    color += push.frame.sunDiscColor.rgb * push.frame.sunDiscColor.w * disc *
             push.frame.sunDiscDots.z;
    FragmentOutput output;
    output.color = float4(color, 1.0);
    output.gbuffer = float4(0.0, 0.0, 0.0, 0.0);
    output.gbuffer2 = float4(0.0, 0.0, 0.0, 0.0);
    return output;
}
