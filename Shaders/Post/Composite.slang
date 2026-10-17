// Brings the HDR scene to the swapchain: fullscreen triangle, exposure and tonemapping.
// Fixed exposure is a placeholder until eye adaptation arrives with the rest of post-processing (Phase 5).

import Common.Bindless;

struct PushConstants
{
    uint sceneTexture;      // Bindless slot of the HDR scene color
    float exposure;         // Linear multiplier applied before tonemapping
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

// Narkowicz ACES filmic curve, good enough as the placeholder tonemapper
float3 aces(float3 x)
{
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float3 color = sampleTexture(push.sceneTexture, SamplerSlot.PointClamp, input.uv).rgb;
    return float4(aces(color * push.exposure), 1);
}
