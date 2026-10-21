// Brings the HDR scene to the swapchain: fullscreen triangle, exposure and tonemapping.

import Common.Bindless;

struct PushConstants
{
    uint sceneTexture;      // Bindless slot of the HDR scene color
    uint oitAccum;          // Bindless slot of the OIT accumulation target
    uint oitReveal;         // Bindless slot of the OIT revealage target
    uint exposureTexture;   // Bindless slot of the 1x1 auto-exposure target
    uint bloomTexture;      // Bindless slot of the blurred HDR bloom target
    uint shadowMap;         // Bindless slot of the sun shadow map
    uint shadowDebug;       // 1: show the raw shadow map instead of the scene
    float whitePoint;       // Hable tonemapping white point
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
    // Triangle covering the screen: (0,0) (2,0) (0,2) in uv
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    VertexOutput output;
    output.position = float4(uv * 2 - 1, 0, 1);
    output.uv = uv;
    return output;
}

// Shipped Postprocess/Tonemapping/Filters.hlsli: ToneMapFilmic_Hable
float hableCurve(float x)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 hableCurve3(float3 x)
{
    const float A = 0.15;
    const float B = 0.50;
    const float C = 0.10;
    const float D = 0.20;
    const float E = 0.02;
    const float F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 hable(float3 x, float whitePoint)
{
    float whiteScale = 1.0 / hableCurve(max(whitePoint, 0.01));
    return hableCurve3(x) * whiteScale;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    // Validation view: show the raw sun shadow map (reverse-Z depth) stretched over the screen.
    // Empty texels are 0; casters are brighter towards the near plane.
    if (push.shadowDebug != 0)
    {
        float depth = sampleTexture(push.shadowMap, SamplerSlot.PointClamp, input.uv).r;
        float value = saturate(depth * 4.0);
        return float4(value.xxx, 1.0);
    }

    float3 color = sampleTexture(push.sceneTexture, SamplerSlot.PointClamp, input.uv).rgb;

    // Order-independent transparency: accum holds premultiplied color/alpha sums, reveal is the
    // remaining visibility of the opaque scene.
    float4 accum = sampleTexture(push.oitAccum, SamplerSlot.PointClamp, input.uv);
    float reveal = saturate(sampleTexture(push.oitReveal, SamplerSlot.PointClamp, input.uv).r);
    float3 oitColor = accum.rgb / max(accum.a, 1e-4);
    float oitAlpha = 1.0 - reveal;
    color = color * (1.0 - oitAlpha) + oitColor * oitAlpha;

    float3 bloom = sampleTexture(push.bloomTexture, SamplerSlot.LinearClamp, input.uv).rgb;
    color += bloom * renderSettings[0].post.y;

    float exposure = sampleTexture(push.exposureTexture, SamplerSlot.PointClamp, float2(0.5, 0.5)).r;
    return float4(saturate(hable(color * exposure, push.whitePoint)), 1);
}
