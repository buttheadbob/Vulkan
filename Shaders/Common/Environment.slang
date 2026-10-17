// Per-frame environment constants shared by the scene shaders.
// Must match ClientPlugin/Render/Scene/CameraState.cs (FrameConstants).

module Environment;

import Common.Bindless;

public struct FrameConstants
{
    public float4 viewProj0;
    public float4 viewProj1;
    public float4 viewProj2;
    public float4 viewProj3;

    public float4 sunDirection;    // xyz: direction from the sun, w: diffuse factor
    public float4 sunColor;        // rgb: linear, w: specular factor
    public float4 ambientColor;    // fallback floor used until the IBL cubemap is ready

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

// IBL lookup: the indirect cubemap shares the skybox orientation
public float3 iblColor(FrameConstants frame, float3 direction, float level)
{
    return sampleCube(frame.iblCube, SamplerSlot.LinearClamp,
                      applySkyboxOrientation(frame, direction), level).rgb;
}
