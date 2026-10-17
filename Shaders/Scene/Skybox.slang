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

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    // z = 1 selects the near plane; for a perspective projection that gives the view ray
    float4 ndc = float4(input.uv * 2 - 1, 1, 1);
    float4 world = float4(dot(ndc, push.frame.invViewProj0),
                          dot(ndc, push.frame.invViewProj1),
                          dot(ndc, push.frame.invViewProj2),
                          dot(ndc, push.frame.invViewProj3));

    float3 direction = normalize(world.xyz / world.w);
    float3 color = skyboxColorReflected(*push.frame, direction) * push.frame.skyboxParams.x;
    return float4(color, 1.0);
}
