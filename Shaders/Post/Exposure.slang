// 1x1 auto-exposure target. Reads the HDR scene at a sparse grid, computes the average
// log-luminance and converts it with the shipped CalculateExposure formula. The composite
// samples this target as a linear exposure multiplier.
//
// Shipped references: Postprocess/Defines.hlsli (CalculateExposure),
// Postprocess/EyeAdaptation/*.hlsl (histogram average), MyPostprocessSettings defaults.

import Common.Bindless;

struct PushConstants
{
    uint sceneTexture;
    uint eyeAdaptation;
    float constantLuminance;
    float luminanceExposure;
    float minLogBrightness;
    float maxLogBrightness;
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

float luminanceKeyValueCendos(float avgLuminance)
{
    float krawczyk = 1.03f - 2.0f / (2.0f + log10(avgLuminance + 1.0f));
    float simple = 1.0f;
    return lerp(krawczyk, simple, saturate(avgLuminance * 2.0f));
}

float calculateExposure(float avgLuminance, float exposure)
{
    float avg = max(avgLuminance, 0.0001f);
    float linearExposure = luminanceKeyValueCendos(avg) / avg;
    linearExposure = log2(max(linearExposure, 0.0001f));
    return linearExposure + exposure;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    const uint GridSize = 16;
    float logSum = 0.0f;
    for (uint y = 0; y < GridSize; y++)
    {
        for (uint x = 0; x < GridSize; x++)
        {
            float2 uv = (float2(x, y) + 0.5f) / float(GridSize);
            float3 color = sampleTexture(push.sceneTexture, SamplerSlot.LinearClamp, uv).rgb;
            float luminance = dot(color, float3(0.2126f, 0.7152f, 0.0722f));
            logSum += log2(max(luminance, 1e-6f));
        }
    }

    float averageLuminance = exp2(logSum / float(GridSize * GridSize));
    averageLuminance = clamp(averageLuminance,
        exp2(push.minLogBrightness), exp2(push.maxLogBrightness));

    // Stock's GetProcessedData disables the constant path when eye adaptation is enabled.
    float luminance = push.eyeAdaptation != 0 ? averageLuminance : push.constantLuminance;
    float logExposure = calculateExposure(luminance, push.luminanceExposure);
    // Stock exposure: CalculateExposure already normalizes the scene's log-average luminance to the
    // key value. Do not scale it down: the LDR scene is calibrated in the same linear units as the
    // shipped shaders (BloomEmissiveness 120, WhitePoint 11.2), so a second calibration darkens the
    // whole world and makes emissive LCD/indicator content look like unlit paint.
    float exposure = clamp(exp2(logExposure) * renderSettings[0].post.x, 0.02f, 4.0f);
    return float4(exposure, 0.0f, 0.0f, 1.0f);
}
