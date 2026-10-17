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

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    DrawTransparentMaterial material = *push.material;
    FrameConstants frame = *push.frame;

    float4 texel = sampleTexture(material.colorTexture, SamplerSlot.LinearWrap, input.uv);
    float4 glossTexel = sampleTexture(material.glossTexture, SamplerSlot.LinearWrap, input.uv);

    if ((material.flags & 1u) != 0) // alpha cutout
        clip(texel.a - 0.5);

    float3 n = normalize(input.normal);
    float3 toCamera = normalize(-input.worldPosition);
    float nv = saturate(dot(n, toCamera));

    float gloss = saturate(glossTexel.w + material.params.z);
    float maxIblMip = max(frame.skyboxParams.y - 1.0, 0.0);

    // Stock glass: reflection = AmbientSpecular * (fresnel + reflectivity) * 0.5
    float3 reflection = iblColor(frame, reflect(-toCamera, n), (1.0 - gloss) * maxIblMip);
    float f = 1.0 - nv;
    float f2 = f * f;
    f *= f2 * f2;
    float fresnel = lerp(0.0, 1.0, f * material.params.y);
    reflection *= fresnel + material.params.x;
    reflection *= 0.5;

    float3 baseColor = material.color.rgb * texel.rgb;
    float3 toSun = -normalize(frame.sunDirection.xyz);
    float3 sunColor = frame.sunColor.rgb * frame.sunDirection.w;
    float nol = saturate(dot(n, toSun));

    // Stock glass uses ambientMultiplier = 4
    float3 irradiance = iblColor(frame, n, maxIblMip);
    float3 ambient = baseColor * irradiance * frame.skyboxParams.z * 4.0;
    float3 direct = baseColor * sunColor * nol * material.lightMultiplier.rgb;

    float3 color = ambient + direct + material.colorAdd.rgb + reflection;

    // Stock adds ColorAdd (including its alpha) and the reflection alpha to transparency.
    float reflectionAlpha = 0.1 * (fresnel + material.params.x) * 0.5;
    float alpha = saturate(material.color.a * texel.a + material.colorAdd.a + reflectionAlpha);
    return float4(color, alpha);
}
