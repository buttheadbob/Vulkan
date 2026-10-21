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
    public uint iblCube;           // bindless cube slot of BackgroundCubeIndirect (0 = black)
    public uint brdfLut;           // bindless 2D slot of the ambient BRDF LUT
    public uint pad;

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

    // Cascades 1 and 2; cascade 0 uses shadowMatrix0..3 and shadowMap above.
    public float4 shadowMatrix1_0;
    public float4 shadowMatrix1_1;
    public float4 shadowMatrix1_2;
    public float4 shadowMatrix1_3;
    public float4 shadowMatrix2_0;
    public float4 shadowMatrix2_1;
    public float4 shadowMatrix2_2;
    public float4 shadowMatrix2_3;
    public float4 shadowSplits;         // x,y,z: far view distance; w: count
    public float4 shadowCascadeParams;  // x,y,z: light-space extent
    public uint shadowMap1;
    public uint shadowMap2;
    public uint shadowPad1;
    public uint shadowPad2;

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

// Stock samples the indirect cubemap directly (no skybox orientation), with x flipped.
public float3 iblColorRaw(FrameConstants frame, float3 direction, float level)
{
    return sampleCube(frame.iblCube, SamplerSlot.LinearClamp, direction, level).rgb;
}

// IBL lookup: the indirect cubemap shares the skybox orientation
public float3 iblColor(FrameConstants frame, float3 direction, float level)
{
    return sampleCube(frame.iblCube, SamplerSlot.LinearClamp,
                      applySkyboxOrientation(frame, direction), level).rgb;
}
