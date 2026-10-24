// Per-frame environment constants shared by the scene shaders.
// Must match ClientPlugin/Render/Scene/CameraState.cs (FrameConstants).

module Environment;

import Common.Bindless;

// Must match ClientPlugin/Render/Scene/SceneLights.cs (64 bytes)
public struct GpuLight
{
    public float4 positionRange; // xyz: camera-relative position, w: range
    public float4 colorFalloff;  // rgb: linear color, w: falloff
    public float4 params;        // x: gloss factor, y: diffuse factor, z: aperture cos, w: 1 = spot
    public float4 direction;     // xyz: spot direction
};

public struct FrameConstants
{
    public float4 viewProj0;
    public float4 viewProj1;
    public float4 viewProj2;
    public float4 viewProj3;

    public float4 sunDirection;    // xyz: direction from the sun, w: diffuse factor
    public float4 sunColor;        // rgb: linear, w: specular factor
    public float4 ambientColor;    // fallback floor used until the IBL cubemap is ready
    public float4 colorizeHsv;     // TextureDebugMultipliers.ColorizeHSV (default 0, 0.8, -0.1)

    public float4 invViewProj0;    // inverse of the Y-flipped view-projection, transposed rows
    public float4 invViewProj1;
    public float4 invViewProj2;
    public float4 invViewProj3;

    public float4 backgroundOrientation0; // transposed SkyboxOrientation rows
    public float4 backgroundOrientation1;
    public float4 backgroundOrientation2;
    public float4 backgroundOrientation3;

    // x: EnvSkyboxBrightness, y: IBL mip count, z: AmbientDiffuseFactor, w: AmbientSpecularFactor
    public float4 skyboxParams;

    public uint skyboxCube;        // bindless cube slot of BackgroundCube
    public uint iblCube;           // bindless cube slot of the prefiltered environment/probe
    public uint brdfLut;           // bindless 2D slot of the ambient BRDF LUT
    public uint iblProbe;          // 1: iblCube is a world-space scene probe, 0: oriented skybox IBL
    public uint iblCubePrev;       // previous probe cube while a completed capture is cross-fading
    public float iblBlend;         // 0: previous cube, 1: current cube
    public uint iblPad;

    public GpuLight* lights;       // dynamic point/spot lights, 0 when lightCount == 0
    public uint lightCount;
    public uint pad2;
    public float emissiveScale;    // MyPostprocessSettings.Data.BloomEmissiveness
    public uint pad3;

    // Stock back directional lights (fill from the environment/planet).
    public float4 backLight1Direction; // xyz: direction FROM light, w: gloss factor
    public float4 backLight1Color;     // rgb: linear, w: diffuse factor
    public float4 backLight2Direction;
    public float4 backLight2Color;

    // x: AmbientGlobalMinimum, y: AmbientGlobalDensity, z: AmbientGlobalMultiplier
    public float4 ambientGlobal;

    // MyEnvironmentLightData.SunDiscColor (rgb), w: stock multiplier
    public float4 sunDiscColor;
    // x: SunDiscInnerDot, y: SunDiscOuterDot, z: SunBillboardEnabled
    public float4 sunDiscDots;

    public float4 shadowMatrix0;
    public float4 shadowMatrix1;
    public float4 shadowMatrix2;
    public float4 shadowMatrix3;
    public uint shadowMap;
    public uint shadowSize;
    public uint shadowEnabled;
    public uint shadowPad;
    public float4 shadowParams; // x: strength, y: depth bias, z: PCF texel scale, w: softness

    // Cascades 1-3; cascade 0 uses shadowMatrix0..3 and shadowMap above.
    public float4 shadowMatrix1_0;
    public float4 shadowMatrix1_1;
    public float4 shadowMatrix1_2;
    public float4 shadowMatrix1_3;
    public float4 shadowMatrix2_0;
    public float4 shadowMatrix2_1;
    public float4 shadowMatrix2_2;
    public float4 shadowMatrix2_3;
    public float4 shadowMatrix3_0;
    public float4 shadowMatrix3_1;
    public float4 shadowMatrix3_2;
    public float4 shadowMatrix3_3;
    public float4 shadowSplits;         // x,y,z,w: far view distance of each cascade
    public float4 shadowCascadeParams;  // x,y,z,w: light-space extent of each cascade
    public uint shadowMap1;
    public uint shadowMap2;
    public uint shadowMap3;
    public uint shadowPad1;

    // Bindless slots used by screen-space reflections.
    public uint sceneColor;
    public uint sceneDepth;
    public uint sceneGBuffer;
    public uint scenePad;

    // x: close/far quality blend start (m), y: blend end (m), z: far mip bias,
    // w: minimum probe mip (0 = sharpest, 2+ = soft blobs)
    public float4 reflectionParams;

    // User tuning lives in the persistent GPU settings buffer (Bindless.renderSettings).
};

// Lighting/EnvAmbient.hlsli ApplySkyboxOrientation (row-vector, transposed rows here)
public float3 applySkyboxOrientation(FrameConstants frame, float3 v)
{
    float4 p = float4(-v, 0.0);
    float3 oriented = float3(dot(p, frame.backgroundOrientation0),
                             dot(p, frame.backgroundOrientation1),
                             dot(p, frame.backgroundOrientation2));
    oriented.z *= -1.0;
    return oriented;
}

// Background: stock SkyboxColorReflected uses the negated oriented view direction
public float3 skyboxColorReflected(FrameConstants frame, float3 viewDirection)
{
    return sampleCube(frame.skyboxCube, SamplerSlot.LinearClamp,
                      -applySkyboxOrientation(frame, viewDirection), 0).rgb;
}

// Samples the raw environment/probe cube without the static skybox orientation. Probe faces are
// rendered with Vulkan's native cube orientation, so no axis swizzle is applied.
public float3 iblColorRaw(FrameConstants frame, float3 direction, float level)
{
    float3 color = sampleCube(frame.iblCube, SamplerSlot.LinearClamp, direction, level).rgb;
    if (frame.iblProbe != 0 && frame.iblCubePrev != 0 && frame.iblBlend < 1.0)
    {
        float3 previous = sampleCube(frame.iblCubePrev, SamplerSlot.LinearClamp, direction, level).rgb;
        color = lerp(previous, color, frame.iblBlend);
    }
    return color;
}

// IBL lookup: scene probes are world-space, the skybox indirect cube shares the skybox orientation
public float3 iblColor(FrameConstants frame, float3 direction, float level)
{
    if (frame.iblProbe != 0)
        return iblColorRaw(frame, direction, level);
    return sampleCube(frame.iblCube, SamplerSlot.LinearClamp,
                      applySkyboxOrientation(frame, direction), level).rgb;
}

// Roughness-to-mip mapping for both the static skybox and the dynamic probe. The probe used a
// sharper exponent, but its box-filtered mips made the starfield sparkle on block edges; matching
// the static curve and adding specular AA at normal discontinuities is more stable.
public float reflectionMipLevel(FrameConstants frame, float gloss)
{
    float maxMip = max(frame.skyboxParams.y - 1.0, 0.0);
    float mip = pow(saturate(1.0 - gloss), 2.0) * maxMip;
    // The probe's mip 0 contains an unfiltered starfield/geometry edge; sampling it on a glossy
    // panel reads as glitter. The minimum is user-tunable and evaluated every frame.
    if (frame.iblProbe != 0)
        mip = max(mip, min(frame.reflectionParams.w, maxMip));
    return mip;
}

// Quality blend by receiver distance. Near surfaces keep the close detail; far surfaces use
// progressively higher mips of the same live scene cube, so distant ships/planets contribute
// colour and broad shapes without sharp detail. There is no hard close/far switch.
public float reflectionMipForDistance(FrameConstants frame, float gloss, float distance)
{
    float maxMip = max(frame.skyboxParams.y - 1.0, 0.0);
    float mip = reflectionMipLevel(frame, gloss);
    if (frame.iblProbe != 0)
    {
        float start = max(frame.reflectionParams.x, 0.0);
        float end = max(frame.reflectionParams.y, start + 1.0);
        float farFactor = smoothstep(start, end, max(distance, 0.0));
        mip += farFactor * max(frame.reflectionParams.z, 0.0);
    }
    return min(mip, maxMip);
}
