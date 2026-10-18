// HDR bloom prefilter + separable blur. The composite adds the blurred bright pass over the
// scene. Bright-pass threshold is in linear HDR units, so emissive indicators and stars bloom.

import Common.Bindless;

struct PushConstants
{
    uint source;       // bindless slot of the source color target
    float threshold;   // linear HDR bright-pass threshold
    float2 direction;  // texel-space blur direction
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

float3 brightPass(float3 color)
{
    float brightness = max(color.r, max(color.g, color.b));
    float knee = max(push.threshold * 0.5, 1.0e-4);
    float soft = clamp(brightness - push.threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1.0e-4);
    float contribution = max(soft, brightness - push.threshold) / max(brightness, 1.0e-4);
    return color * contribution;
}

float3 blur(float2 uv, float2 direction)
{
    // 9-tap Gaussian, sampled at half resolution by the target size.
    float3 result = sampleTexture(push.source, SamplerSlot.LinearClamp, uv).rgb * 0.227027;
    result += sampleTexture(push.source, SamplerSlot.LinearClamp, uv + direction * 1.384615).rgb * 0.316216;
    result += sampleTexture(push.source, SamplerSlot.LinearClamp, uv - direction * 1.384615).rgb * 0.316216;
    result += sampleTexture(push.source, SamplerSlot.LinearClamp, uv + direction * 3.230769).rgb * 0.070270;
    result += sampleTexture(push.source, SamplerSlot.LinearClamp, uv - direction * 3.230769).rgb * 0.070270;
    return result;
}

[shader("fragment")]
float4 prefilterMain(VertexOutput input) : SV_Target0
{
    float3 color = sampleTexture(push.source, SamplerSlot.LinearClamp, input.uv).rgb;
    return float4(brightPass(color), 1.0);
}

[shader("fragment")]
float4 blurHorizontalMain(VertexOutput input) : SV_Target0
{
    return float4(blur(input.uv, push.direction), 1.0);
}

[shader("fragment")]
float4 blurVerticalMain(VertexOutput input) : SV_Target0
{
    return float4(blur(input.uv, push.direction), 1.0);
}
