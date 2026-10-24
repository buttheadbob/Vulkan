// Transparent model parts (glass, holo, shield) using the game's MyTransparentMaterial definitions.
// Drawn after opaque geometry with depth test read-only and premultiplied-alpha blending.

import Common.Bindless;
import Common.Environment;

// Must match ClientPlugin/Render/Scene/ModelPass.cs (std430, 96 bytes)
struct DrawTransparentMaterial
{
    float4 color;           // premultiplied linear
    float4 colorAdd;        // linear
    float4 lightMultiplier;
    float4 params;          // x reflectivity, y fresnel, z glossTextureAdd, w specularColorFactor
    float4 params2;         // x gloss, y reflectionShadow, z alphaSaturation, w flags
    uint colorTexture;
    uint glossTexture;
    uint flags;             // 1: alpha cutout, 2: instanced
    uint pad0;
};

// Must match ClientPlugin/Render/Models/ModelCache.cs (32 bytes)
struct ModelVertex
{
    float4 position;
    float2 texcoord;
    uint normal;
    uint tangent;
};

// MyCubeInstanceData (64 bytes), see Model.slang
struct CubeInstance
{
    uint4 bones0;
    uint4 bones1;
    float4 translationAndRot;
    float4 colorMaskHsv;
};

// Must match ClientPlugin/Render/Scene/ModelPass.cs
struct PushConstants
{
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    FrameConstants* frame;
    DrawTransparentMaterial* material;
    ModelVertex* vertices;
    CubeInstance* instances;
    uint instanceStart;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

static const float3 PACKED_DIR[6] =
{
    float3(0, 0, -1), float3(0, 0, 1), float3(-1, 0, 0),
    float3(1, 0, 0), float3(0, 1, 0), float3(0, -1, 0)
};

float3 unpackNormal(uint packed)
{
    uint2 p = uint2(packed & 0xFFFF, packed >> 16);
    float zsign = -1.0;
    if (p.x > 32767)
    {
        zsign = 1.0;
        p.x &= 0x7FFF;
    }

    float2 xy = float2(p) / 32767.0;
    xy = 2.0 * xy - 1.0;
    return float3(xy, zsign * sqrt(saturate(1.0 - dot(xy, xy))));
}

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float3 worldPosition : TEXCOORD2;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    ModelVertex vertex = push.vertices[vertexId];
    float4 position = vertex.position;
    float3 normal = unpackNormal(vertex.normal);

    if ((push.material.flags & 2u) != 0)
    {
        CubeInstance instance = push.instances[push.instanceStart + instanceId];
        float4 packed = instance.translationAndRot;
        int dir = (int)packed.w;
        float3 forward = PACKED_DIR[dir / 6];
        float3 up = PACKED_DIR[dir - (dir / 6) * 6];
        float3 right = cross(up, -forward);

        position = float4(dot(position, float4(right, 0.0)),
                          dot(position, float4(up, 0.0)),
                          dot(position, float4(-forward, 0.0)),
                          1.0);
        normal = mul(normal, float3x3(right, up, -forward));
    }

    float4 worldPosition = float4(dot(position, push.world0),
                                  dot(position, push.world1),
                                  dot(position, push.world2),
                                  1.0);
    float4 clip = float4(dot(worldPosition, push.frame.viewProj0),
                         dot(worldPosition, push.frame.viewProj1),
                         dot(worldPosition, push.frame.viewProj2),
                         dot(worldPosition, push.frame.viewProj3));

    float3x3 worldRotation = float3x3(push.world0.xyz, push.world1.xyz, push.world2.xyz);

    VertexOutput output;
    output.position = clip;
    output.uv = vertex.texcoord;
    output.normal = normalize(mul(normal, worldRotation));
    output.worldPosition = worldPosition.xyz;
    return output;
}

struct FragmentOutput
{
    float4 accum : SV_Target0;  // premultiplied by alpha * weight, additive
    float reveal : SV_Target1;  // alpha; the reveal target blends dst *= (1 - src)
};

float3 reconstructScenePosition(float2 uv, float depth, FrameConstants frame)
{
    float4 ndc = float4(uv * 2.0 - 1.0, depth, 1.0);
    float4 world = float4(dot(ndc, frame.invViewProj0),
                          dot(ndc, frame.invViewProj1),
                          dot(ndc, frame.invViewProj2),
                          dot(ndc, frame.invViewProj3));
    return world.xyz / max(world.w, 1e-6);
}

float3 glassScreenSpaceReflection(float2 uv, float3 position, float3 normal, float gloss, FrameConstants frame, out float hit)
{
    hit = 0.0;
    if (frame.sceneColor == 0 || frame.sceneDepth == 0 || gloss < 0.1)
        return 0.0;

    float depth = sampleTexture(frame.sceneDepth, SamplerSlot.PointClamp, uv).r;
    if (depth <= 0.00001)
        return 0.0;

    float3 start = position + normal * 0.06;
    float3 ray = normalize(reflect(normalize(position), normal));
    float previousError = 1e9;

    int steps = 20 + int(saturate(renderSettings[0].post.w)) * 10;
    [unroll]
    for (int i = 1; i <= 40; i++)
    {
        if (i > steps)
            break;
        float t = float(i) * 0.25;
        float3 p = start + ray * t;
        float4 clip = float4(dot(float4(p, 1.0), frame.viewProj0),
                             dot(float4(p, 1.0), frame.viewProj1),
                             dot(float4(p, 1.0), frame.viewProj2),
                             dot(float4(p, 1.0), frame.viewProj3));
        if (clip.w <= 1e-5)
            break;

        float2 hitUv = clip.xy / clip.w * 0.5 + 0.5;
        if (any(hitUv < 0.0) || any(hitUv > 1.0))
            break;

        float storedDepth = sampleTexture(frame.sceneDepth, SamplerSlot.PointClamp, hitUv).r;
        if (storedDepth <= 0.00001)
            break;

        float3 surface = reconstructScenePosition(hitUv, storedDepth, frame);
        float error = length(surface - p);
        if (error < 0.12 + t * 0.012)
        {
            float2 edge = smoothstep(0.0, 0.08, uv) * smoothstep(0.0, 0.08, 1.0 - uv);
            hit = edge.x * edge.y;
            return sampleTexture(frame.sceneColor, SamplerSlot.LinearClamp, hitUv).rgb;
        }

        if (error > previousError * 2.5 && i > 2)
            break;
        previousError = error;
    }

    return 0.0;
}

[shader("fragment")]
FragmentOutput fragmentMain(VertexOutput input)
{
    DrawTransparentMaterial material = *push.material;
    FrameConstants frame = *push.frame;

    float4 texel = sampleTexture(material.colorTexture, SamplerSlot.LinearWrap, input.uv);
    float4 glossTexel = sampleTexture(material.glossTexture, SamplerSlot.LinearWrap, input.uv);

    if ((material.flags & 1u) != 0) // alpha cutout
        clip(texel.a - 0.5);

    float3 n = normalize(input.normal);
    float3 toCamera = normalize(-input.worldPosition);
    // Glass is often two-sided. Force the shading/reflection normal to face the camera; otherwise
    // back-facing panels reflect the hemisphere behind them (the ship interior instead of the scene).
    if (dot(n, toCamera) < 0.0)
        n = -n;
    float nv = saturate(dot(n, toCamera));

    float gloss = saturate(glossTexel.w + material.params.z);

    // Stock glass: reflection = AmbientSpecular * (fresnel + reflectivity) * 0.5
    float3 reflectionDirection = reflect(-toCamera, n);
    float maxMip = max(frame.skyboxParams.y - 1.0, 0.0);
    float normalVariance = length(ddx(n)) + length(ddy(n));
    float mip = min(reflectionMipForDistance(frame, gloss, length(input.worldPosition)) +
                    saturate(normalVariance * 6.0) * maxMip * 0.5, maxMip);
    float3 reflection = iblColor(frame, reflectionDirection, mip);
    float f = 1.0 - nv;
    float f2 = f * f;
    f *= f2 * f2;
    float fresnel = lerp(0.0, 1.0, f * material.params.y);
    // Prefer a screen-space hit (ship parts, blocks) over the skybox IBL when one exists.
    float hit;
    float3 ssr = glassScreenSpaceReflection(input.uv, input.worldPosition, n, gloss, frame, hit);
    reflection = lerp(reflection, ssr, hit);
    reflection *= fresnel + material.params.x;
    reflection *= 0.5;

    float3 baseColor = material.color.rgb * texel.rgb;
    float3 toSun = -normalize(frame.sunDirection.xyz);
    float3 sunColor = frame.sunColor.rgb * frame.sunDirection.w;
    float nol = saturate(dot(n, toSun));

    // Stock glass uses ambientMultiplier = 4
    float3 irradiance = iblColor(frame, n, max(frame.skyboxParams.y - 1.0, 0.0));
    float3 ambient = baseColor * irradiance * frame.skyboxParams.z * 4.0;
    float3 direct = baseColor * sunColor * nol * material.lightMultiplier.rgb;

    float3 color = ambient + direct + material.colorAdd.rgb + reflection;

    // Stock adds ColorAdd (including its alpha) and the reflection alpha to transparency.
    float reflectionAlpha = 0.1 * (fresnel + material.params.x) * 0.5;
    float alpha = saturate(material.color.a * texel.a + material.colorAdd.a + reflectionAlpha);

    // Weighted-blended OIT (McGuire): close/far layers get a bounded weight; the resolve pass
    // reconstructs the average color and coverage. No per-frame sort.
    float distanceToCamera = length(input.worldPosition);
    float weight = alpha * clamp(0.03 / (1e-5 + pow(distanceToCamera / 200.0, 4.0)), 0.01, 3000.0);

    FragmentOutput output;
    output.accum = float4(color * weight, alpha * weight);
    output.reveal = alpha;
    return output;
}
