// Brings the HDR scene to the swapchain: one fullscreen triangle sampling the scene color.
// Placeholder transfer (clamp); tonemapping, exposure, bloom and upscaling replace it in Phase 5.

import Common.Bindless;

struct PushConstants
{
    uint sceneTexture;      // Bindless slot of the HDR scene color
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

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float3 color = sampleTexture(push.sceneTexture, SamplerSlot.PointClamp, input.uv).rgb;
    return float4(saturate(color), 1);
}
