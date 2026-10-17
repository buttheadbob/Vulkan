// Opaque world geometry: MWM meshes with the stock material channels.
// Camera-relative: world matrices already have the camera position subtracted and the view matrix is
// rotation only. Depth is reverse-Z (0 = far); the projection is Y-flipped for Vulkan in C#.

import Common.Bindless;
import Common.Color;

struct FrameConstants
{
    float4 viewProj0;
    float4 viewProj1;
    float4 viewProj2;
    float4 viewProj3;
    float4 sunDirection;    // xyz: direction from the sun, w: diffuse factor
    float4 sunColor;        // rgb: linear, w: specular factor
    float4 ambientColor;    // rgb: linear, w: ambient factor
};

// Must match ClientPlugin/Render/Scene/ModelPass.cs (std430, 48 bytes)
struct DrawMaterial
{
    float4 colorMulEmissive; // rgb: multiplier, w: emissivity
    uint colorMetal;
    uint normalGloss;
    uint extensions;
    uint alphamask;
    uint flags;
    float depthBias;
    uint pad0;
    uint pad1;
};

enum MaterialFlags : uint
{
    HasColorMetal = 1,
    HasNormalGloss = 2,
    HasExtensions = 4,
    HasAlphamask = 8,
    HasTangent = 16,
    AlphaMasked = 32,
    Instanced = 64,
};

// Must match ClientPlugin/Render/Models/ModelCache.cs (32 bytes)
struct ModelVertex
{
    float4 position;
    float2 texcoord;
    uint normal;
    uint tangent;
};

// MyCubeInstanceData: 32 bytes of bone bytes the static cube path ignores, then the packed
// ortho matrix (Vector4.PackOrthoMatrix) and the instance key color. Must match InstanceBuffers.cs.
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
    float4 colorMaskHsv;
    FrameConstants* frame;
    DrawMaterial* material;
    ModelVertex* vertices;
    CubeInstance* instances;
    uint instanceStart;
};

// Base6Directions.GetVector order (VRage.Math), see VertexTransformations.hlsli
static const float3 PACKED_DIR[6] =
{
    float3(0, 0, -1), float3(0, 0, 1), float3(-1, 0, 0),
    float3(1, 0, 0), float3(0, 1, 0), float3(0, -1, 0)
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

// VF_Packer.UnpackNormal / VertexTransformations.hlsli unpack_normal
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

// VF_Packer.UnpackTangentSign / VertexTransformations.hlsli unpack_tangent_sign
float4 unpackTangentSign(uint packed)
{
    float4 p = float4(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, packed >> 24) / 255.0;
    float sign = p.w > 0.5 ? 1.0 : -1.0;
    float zsign = p.y > 0.5 ? 1.0 : -1.0;
    if (zsign > 0.0)
        p.y -= 0.5;
    if (sign > 0.0)
        p.w -= 0.5;

    float2 xy = 256.0 * (p.xz + 256.0 * p.yw);
    xy /= 32767.0;
    xy = 2.0 * xy - 1.0;
    return float4(xy, zsign * sqrt(saturate(1.0 - dot(xy, xy))), sign);
}

// Math/Color.hlsli
float3 hsvToRgb(float3 hsv)
{
    float4 k = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(hsv.xxx + k.xyz) * 6.0 - k.www);
    return hsv.z * lerp(k.xxx, saturate(p - k.xxx), hsv.y);
}

float3 rgbToHsv(float3 rgb)
{
    float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(rgb.bg, k.wz), float4(rgb.gb, k.xy), step(rgb.b, rgb.g));
    float4 q = lerp(float4(p.xyw, rgb.r), float4(rgb.r, p.yzx), step(p.x, rgb.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

// Geometry/Materials/PixelUtilsMaterials.hlsli ColorizeGray
float3 colorizeGray(float3 texel, float3 colorMaskHsv, float coloring)
{
    if (coloring <= 0.0)
        return texel;

    float3 hsv = rgbToHsv(linearToSrgb(texel));
    hsv.xy = 0;
    float3 finalHsv = saturate(hsv + colorMaskHsv);
    return lerp(texel, srgbToLinear(hsvToRgb(finalHsv)), coloring);
}

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float3 worldPosition : TEXCOORD3;
    nointerpolation float4 colorMaskHsv : TEXCOORD4;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    ModelVertex vertex = push.vertices[vertexId];
    float4 position = vertex.position;
    float3 normal = unpackNormal(vertex.normal);
    float4 tangent = unpackTangentSign(vertex.tangent);
    float4 colorMaskHsv = push.colorMaskHsv;
    float3x3 instanceRotation = float3x3(float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1));

    if ((push.material.flags & uint(MaterialFlags.Instanced)) != 0)
    {
        // MyCubeInstanceData packed ortho matrix (Vector4.PackOrthoMatrix, VRage.Math)
        CubeInstance instance = push.instances[push.instanceStart + instanceId];
        float4 packed = instance.translationAndRot;
        int dir = (int)packed.w;
        float3 forward = PACKED_DIR[dir / 6];
        float3 up = PACKED_DIR[dir - (dir / 6) * 6];
        float3 right = cross(up, -forward);

        // Row-vector cube matrix, as VertexTransformations.hlsli translation_rotation_matrix
        float4 row0 = float4(right, 0.0);
        float4 row1 = float4(up, 0.0);
        float4 row2 = float4(-forward, 0.0);
        position = float4(dot(position, row0), dot(position, row1), dot(position, row2), 1.0);

        instanceRotation = float3x3(right, up, -forward);
        normal = mul(normal, instanceRotation);
        tangent.xyz = mul(tangent.xyz, instanceRotation);
        colorMaskHsv = float4(instance.colorMaskHsv.xyz, 0.0);
    }

    // Row-vector convention: clip = mul(mul(position, world), viewProjection)
    float4 worldPosition4 = float4(dot(position, push.world0),
                                   dot(position, push.world1),
                                   dot(position, push.world2),
                                   1.0);
    float4 clip = float4(dot(worldPosition4, push.frame.viewProj0),
                         dot(worldPosition4, push.frame.viewProj1),
                         dot(worldPosition4, push.frame.viewProj2),
                         dot(worldPosition4, push.frame.viewProj3));

    // MyRenderableComponent.ApplyDepthBias: added to clip z, before the perspective divide
    clip.z += push.material.depthBias * 1e-6;

    float3x3 worldRotation = float3x3(push.world0.xyz, push.world1.xyz, push.world2.xyz);

    VertexOutput output;
    output.position = clip;
    output.uv = vertex.texcoord;
    output.normal = normalize(mul(normal, worldRotation));
    output.tangent = float4(normalize(mul(tangent.xyz, worldRotation)), tangent.w);
    output.worldPosition = worldPosition4.xyz;
    output.colorMaskHsv = colorMaskHsv;
    return output;
}

static const float PI = 3.14159265359;

float3 fresnelSchlick(float3 f0, float voh)
{
    return f0 + (1.0 - f0) * pow(1.0 - voh, 5.0);
}

float distributionGGX(float noh, float alpha)
{
    float a2 = alpha * alpha;
    float d = noh * noh * (a2 - 1.0) + 1.0;
    return a2 / max(PI * d * d, 1e-6);
}

float visibilitySmithGGX(float nov, float nol, float alpha)
{
    float a2 = alpha * alpha;
    float gv = nol * sqrt(nov * nov * (1.0 - a2) + a2);
    float gl = nov * sqrt(nol * nol * (1.0 - a2) + a2);
    return 0.5 / max(gv + gl, 1e-4);
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    DrawMaterial material = *push.material;
    uint flags = material.flags;

    float4 cm = float4(1.0, 1.0, 1.0, 0.0);
    if ((flags & uint(MaterialFlags.HasColorMetal)) != 0)
        cm = sampleTexture(material.colorMetal, SamplerSlot.AnisotropicWrap, input.uv);

    float4 ng = float4(0.5, 0.5, 0.5, 0.0);
    if ((flags & uint(MaterialFlags.HasNormalGloss)) != 0)
        ng = sampleTexture(material.normalGloss, SamplerSlot.AnisotropicWrap, input.uv);

    float4 extras = float4(1.0, 0.0, 0.0, 0.0);
    if ((flags & uint(MaterialFlags.HasExtensions)) != 0)
        extras = sampleTexture(material.extensions, SamplerSlot.AnisotropicWrap, input.uv);

    if ((flags & uint(MaterialFlags.AlphaMasked)) != 0 && (flags & uint(MaterialFlags.HasAlphamask)) != 0)
    {
        float alpha = sampleTexture(material.alphamask, SamplerSlot.LinearClamp, input.uv).r;
        clip(alpha - 0.5);
    }

    float3 baseColor = colorizeGray(cm.rgb, input.colorMaskHsv.xyz, extras.w);
    baseColor *= material.colorMulEmissive.rgb;

    float3 n = normalize(input.normal);
    float gloss = 0.5;
    if ((flags & uint(MaterialFlags.HasNormalGloss)) != 0)
    {
        float3 normalMap = ng.xyz * 2.0 - 1.0;
        normalMap.y *= -1.0; // Geometry/PixelUtils.hlsli adjust_normalmap_precomputed_tangents
        if ((flags & uint(MaterialFlags.HasTangent)) != 0)
        {
            float3 t = input.tangent.xyz;
            float3 b = cross(t, n) * input.tangent.w;
            n = normalize(float3(dot(normalMap, t), dot(normalMap, b), dot(normalMap, n)));
        }

        gloss = ng.w;
    }

    float metalness = cm.w;
    float ao = extras.x;
    float emissive = saturate(extras.y - 1.0 / 255.0) * material.colorMulEmissive.w;

    float3 toCamera = normalize(-input.worldPosition);
    float3 toSun = -normalize(push.frame.sunDirection.xyz);
    float3 sunColor = push.frame.sunColor.rgb * push.frame.sunDirection.w;

    float nol = saturate(dot(n, toSun));
    float3 halfVector = normalize(toSun + toCamera);
    float noh = saturate(dot(n, halfVector));
    float voh = saturate(dot(toCamera, halfVector));
    float nov = saturate(dot(n, toCamera));

    float roughness = max(1.0 - gloss, 0.04);
    float alpha = roughness * roughness;
    float3 f0 = lerp(0.04, baseColor, metalness);
    float3 fresnel = fresnelSchlick(f0, voh);
    float specular = distributionGGX(noh, alpha) * visibilitySmithGGX(nov, nol, alpha);

    float3 diffuse = baseColor * (1.0 - metalness) * (nol / PI);
    float3 direct = (diffuse + fresnel * specular * nol) * sunColor;

    float3 ambient = baseColor * push.frame.ambientColor.rgb * push.frame.ambientColor.w * ao;
    return float4(direct + ambient + baseColor * emissive, 1.0);
}
