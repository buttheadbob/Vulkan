// FXAA 3.11 (quality preset 29), ported from the stock Postprocess/Fxaa3_11.hlsli.
// Runs on the tonemapped scene before the GUI, like the stock renderer's FXAA pass.

import Common.Bindless;

struct PushConstants
{
    uint source;       // bindless slot of the tonemapped scene
    float rcpFrameX;   // 1 / backbuffer width
    float rcpFrameY;   // 1 / backbuffer height
    uint linearOutput; // 1: the swapchain is sRGB and the shader output is encoded by hardware
    uint enabled;      // 0: copy only
    uint quality;      // 0: low, 1: medium, 2: high, 3: very high (stock preset 29)
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

float3 linearToSrgb(float3 c)
{
    float3 low = c * 12.92;
    float3 high = 1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055;
    return lerp(low, high, step(float3(0.0031308, 0.0031308, 0.0031308), c));
}

float3 srgbToLinear(float3 c)
{
    float3 low = c / 12.92;
    float3 high = pow((max(c, 0.0) + 0.055) / 1.055, 2.4);
    return lerp(low, high, step(float3(0.04045, 0.04045, 0.04045), c));
}

float3 fetchEncoded(float2 uv)
{
    return linearToSrgb(sampleTextureLevel(push.source, SamplerSlot.LinearClamp, uv, 0.0).rgb);
}

// The stock FXAA pass fills the alpha channel with BT.709 luminance of the encoded color.
float luma(float3 encoded)
{
    return dot(encoded, float3(0.2126, 0.7152, 0.0722));
}

float3 fxaa(float2 uv, float3 centerEncoded)
{
    const float2 rcp = float2(push.rcpFrameX, push.rcpFrameY);
    // Medium is the stock preset 29. Very high lowers both thresholds and searches further.
    const float edgeThreshold = push.quality >= 3 ? 0.063 : 0.125;
    const float edgeThresholdMin = push.quality >= 3 ? 0.0312 : 0.0625;

    float lumaM = luma(centerEncoded);
    float lumaS = luma(fetchEncoded(uv + float2(0.0, rcp.y)));
    float lumaE = luma(fetchEncoded(uv + float2(rcp.x, 0.0)));
    float lumaN = luma(fetchEncoded(uv + float2(0.0, -rcp.y)));
    float lumaW = luma(fetchEncoded(uv + float2(-rcp.x, 0.0)));

    float rangeMax = max(max(lumaN, lumaW), max(lumaE, max(lumaS, lumaM)));
    float rangeMin = min(min(lumaN, lumaW), min(lumaE, min(lumaS, lumaM)));
    float range = rangeMax - rangeMin;
    if (range < max(edgeThresholdMin, rangeMax * edgeThreshold))
        return centerEncoded;

    float lumaNW = luma(fetchEncoded(uv + float2(-rcp.x, -rcp.y)));
    float lumaSE = luma(fetchEncoded(uv + float2(rcp.x, rcp.y)));
    float lumaNE = luma(fetchEncoded(uv + float2(rcp.x, -rcp.y)));
    float lumaSW = luma(fetchEncoded(uv + float2(-rcp.x, rcp.y)));

    float edgeHorz1 = (-2.0 * lumaM) + lumaN + lumaS;
    float edgeVert1 = (-2.0 * lumaM) + lumaW + lumaE;
    float edgeHorz2 = (-2.0 * lumaE) + lumaNE + lumaSE;
    float edgeVert2 = (-2.0 * lumaN) + lumaNW + lumaNE;
    float edgeHorz3 = (-2.0 * lumaW) + lumaNW + lumaSW;
    float edgeVert3 = (-2.0 * lumaS) + lumaSW + lumaSE;

    float edgeHorz = abs(edgeHorz3) + (abs(edgeHorz1) * 2.0) + abs(edgeHorz2);
    float edgeVert = abs(edgeVert3) + (abs(edgeVert1) * 2.0) + abs(edgeVert2);
    bool horzSpan = edgeHorz >= edgeVert;

    float subpixA = ((lumaN + lumaS + lumaW + lumaE) * 2.0) + lumaNW + lumaNE + lumaSW + lumaSE;
    float subpixB = (subpixA * (1.0 / 12.0)) - lumaM;
    float subpixC = saturate(abs(subpixB) / max(range, 1.0e-5));

    if (!horzSpan)
    {
        lumaN = lumaW;
        lumaS = lumaE;
    }

    float gradientN = lumaN - lumaM;
    float gradientS = lumaS - lumaM;
    float lumaNN = lumaN + lumaM;
    float lumaSS = lumaS + lumaM;
    bool pairN = abs(gradientN) >= abs(gradientS);
    float gradient = max(abs(gradientN), abs(gradientS));
    if (!pairN)
        lumaNN = lumaSS;

    float lengthSign = horzSpan ? rcp.y : rcp.x;
    if (pairN)
        lengthSign = -lengthSign;

    float2 offNP = horzSpan ? float2(rcp.x, 0.0) : float2(0.0, rcp.y);
    float2 posB = uv;
    if (horzSpan)
        posB.y += lengthSign * 0.5;
    else
        posB.x += lengthSign * 0.5;

    // Low = preset 10, Medium = stock preset 29, High = preset 39, Very high = extended 39.
    float steps[20];
    int stepCount;
    if (push.quality == 0)
    {
        stepCount = 3;
        steps[0] = 1.5; steps[1] = 3.0; steps[2] = 12.0;
    }
    else if (push.quality == 1)
    {
        stepCount = 12;
        steps[0] = 1.0; steps[1] = 1.5; steps[2] = 2.0; steps[3] = 2.0; steps[4] = 2.0; steps[5] = 2.0;
        steps[6] = 2.0; steps[7] = 2.0; steps[8] = 2.0; steps[9] = 2.0; steps[10] = 4.0; steps[11] = 8.0;
    }
    else if (push.quality == 2)
    {
        stepCount = 12;
        steps[0] = 1.0; steps[1] = 1.0; steps[2] = 1.0; steps[3] = 1.0; steps[4] = 1.0; steps[5] = 1.5;
        steps[6] = 2.0; steps[7] = 2.0; steps[8] = 2.0; steps[9] = 2.0; steps[10] = 4.0; steps[11] = 8.0;
    }
    else
    {
        stepCount = 20;
        steps[0] = 1.0; steps[1] = 1.0; steps[2] = 1.0; steps[3] = 1.0; steps[4] = 1.0; steps[5] = 1.0;
        steps[6] = 1.5; steps[7] = 1.5; steps[8] = 2.0; steps[9] = 2.0; steps[10] = 2.0; steps[11] = 2.0;
        steps[12] = 2.0; steps[13] = 2.0; steps[14] = 2.0; steps[15] = 2.0; steps[16] = 4.0; steps[17] = 4.0;
        steps[18] = 8.0; steps[19] = 8.0;
    }

    float gradientScaled = gradient * 0.25;
    float lumaMM = lumaM - lumaNN * 0.5;
    bool lumaMLTZero = lumaMM < 0.0;

    float2 posN = posB - offNP * steps[0];
    float2 posP = posB + offNP * steps[0];
    float lumaEndN = luma(fetchEncoded(posN)) - lumaNN * 0.5;
    float lumaEndP = luma(fetchEncoded(posP)) - lumaNN * 0.5;
    bool doneN = abs(lumaEndN) >= gradientScaled;
    bool doneP = abs(lumaEndP) >= gradientScaled;

    for (int i = 1; i < stepCount; i++)
    {
        if (doneN && doneP)
            break;

        if (!doneN)
        {
            posN -= offNP * steps[i];
            lumaEndN = luma(fetchEncoded(posN)) - lumaNN * 0.5;
            doneN = abs(lumaEndN) >= gradientScaled;
        }

        if (!doneP)
        {
            posP += offNP * steps[i];
            lumaEndP = luma(fetchEncoded(posP)) - lumaNN * 0.5;
            doneP = abs(lumaEndP) >= gradientScaled;
        }
    }

    float dstN = horzSpan ? uv.x - posN.x : uv.y - posN.y;
    float dstP = horzSpan ? posP.x - uv.x : posP.y - uv.y;
    float spanLength = dstP + dstN;
    bool goodSpanN = (lumaEndN < 0.0) != lumaMLTZero;
    bool goodSpanP = (lumaEndP < 0.0) != lumaMLTZero;
    bool directionN = dstN < dstP;
    bool goodSpan = directionN ? goodSpanN : goodSpanP;

    float pixelOffset = (min(dstN, dstP) * (-1.0 / max(spanLength, 1.0e-5))) + 0.5;
    float subpixD = (-2.0 * subpixC) + 3.0;
    float subpixE = subpixC * subpixC;
    float subpixF = subpixD * subpixE;
    float subpixG = subpixF * subpixF;
    float subpixH = subpixG; // fxaaQualitySubpix = 1.0
    float pixelOffsetGood = goodSpan ? pixelOffset : 0.0;
    float pixelOffsetSubpix = max(pixelOffsetGood, subpixH);

    float2 finalUv = uv;
    if (horzSpan)
        finalUv.y += pixelOffsetSubpix * lengthSign;
    else
        finalUv.x += pixelOffsetSubpix * lengthSign;

    return fetchEncoded(finalUv);
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    float3 encoded = fetchEncoded(input.uv);
    if (push.enabled != 0)
        encoded = fxaa(input.uv, encoded);

    float3 output = push.linearOutput != 0 ? srgbToLinear(encoded) : encoded;
    return float4(output, 1.0);
}
