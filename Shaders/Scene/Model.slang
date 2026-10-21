// Opaque world geometry: MWM meshes with the stock material channels.
// Camera-relative: world matrices already have the camera position subtracted and the view matrix is
// rotation only. Depth is reverse-Z (0 = far); the projection is Y-flipped for Vulkan in C#.

import Common.Bindless;
import Common.Color;
import Common.Environment;

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
    MetalnessColorable = 128,
    Decal = 256, // static decal cutout: nudge the clip-space depth like stock VertexStage
    Screen = 512, // generated LCD target: never take metalness from its alpha channel
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

// Per-draw data written by Scene/Culling.slang (184 bytes, instancing included). The indirect path reads everything from
// here, so the CPU submits one draw per bucket instead of one per part.
struct DrawData
{
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 colorMaskHsv;
    DrawMaterial material;
    ModelVertex* vertices;
    uint* indices;
    uint indexStart;
    uint indexCount;
    uint pad0;
    uint pad1;
    CubeInstance* instances;
    uint instanceStart;
    uint instanceCount;
    uint pad2;
    uint pad3;
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
    DrawData* drawData; // 0 for direct draws, bucket draw data for vkCmdDrawIndirectCount
};

// Base6Directions.GetVector order (VRage.Math), see VertexTransformations.hlsli
static const float3 PACKED_DIR[6] =
{
    float3(0, 0, -1), float3(0, 0, 1), float3(-1, 0, 0),
    float3(1, 0, 0), float3(0, 1, 0), float3(0, -1, 0)
};

// Unit-disk sample set for the soft PCF penumbra. The disk is rotated per cascade so the fixed
// pattern does not line up across the three maps.
static const float2 SHADOW_DISK[16] =
{
    float2(-0.326, -0.406), float2(-0.840, -0.074), float2(-0.696, 0.457),
    float2(-0.203, 0.621), float2(0.962, -0.195), float2(0.473, -0.480),
    float2(0.519, 0.767), float2(0.185, -0.893), float2(0.507, 0.064),
    float2(0.896, 0.412), float2(-0.322, -0.933), float2(-0.792, -0.598),
    float2(-0.050, 0.930), float2(0.760, 0.700), float2(-0.950, 0.150),
    float2(0.120, -0.220)
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

// Geometry/Materials/PixelUtilsMaterials.hlsli ColorizeGray. Stock operates on the linear
// texel and adds the paint-mask HSV directly; converting to sRGB first shifts both hue and value.
float3 colorizeGray(float3 texel, float3 hsvmask, float coloring)
{
    if (coloring <= 0.0)
        return texel;

    float3 coloringc = hsvToRgb(float3(hsvmask.x, 1.0, 1.0));
    float3 hsv = rgbToHsv(lerp(texel, texel * coloringc, coloring));
    hsv.x = 0.0;

    float3 fhsv = hsv + hsvmask * float3(1.0, 1.0, 0.5);
    fhsv.x = frac(fhsv.x);

    float gray2 = 1.0 - saturate((hsvmask.y + 1.0) / 0.1);
    fhsv.yz = lerp(saturate(fhsv.yz), saturate(hsv.yz + hsvmask.yz), gray2);

    float gray3 = 1.0 - saturate((hsvmask.y + 0.9) / 0.1);
    fhsv.y = lerp(saturate(fhsv.y), saturate(hsv.y + hsvmask.y), gray3);

    return lerp(texel, hsvToRgb(fhsv), coloring);
}

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 tangent : TEXCOORD2;
    float3 worldPosition : TEXCOORD3;
    nointerpolation float4 colorMaskHsv : TEXCOORD4;
    nointerpolation uint drawIndex : TEXCOORD5;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID,
    uint drawIndex : SV_DrawIndex)
{
    DrawMaterial material;
    ModelVertex vertex;
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 colorMaskHsv;
    CubeInstance* cubeInstances = push.instances;
    uint cubeInstanceStart = push.instanceStart;

    if (push.drawData != nullptr)
    {
        // GPU-driven path: the draw command is non-indexed; the culling compute supplied the model's
        // index buffer, so fetch the index from device memory and then the vertex.
        DrawData draw = push.drawData[drawIndex];
        uint index = draw.indices[draw.indexStart + vertexId];
        material = draw.material;
        vertex = draw.vertices[index];
        world0 = draw.world0;
        world1 = draw.world1;
        world2 = draw.world2;
        world3 = draw.world3;
        colorMaskHsv = draw.colorMaskHsv;
        cubeInstances = draw.instances;
        cubeInstanceStart = draw.instanceStart;
    }
    else
    {
        material = *push.material;
        vertex = push.vertices[vertexId];
        world0 = push.world0;
        world1 = push.world1;
        world2 = push.world2;
        world3 = push.world3;
        colorMaskHsv = push.colorMaskHsv;
    }

    float4 position = vertex.position;
    float3 normal = unpackNormal(vertex.normal);
    float4 tangent = unpackTangentSign(vertex.tangent);
    float3x3 instanceRotation = float3x3(float3(1, 0, 0), float3(0, 1, 0), float3(0, 0, 1));

    if ((material.flags & uint(MaterialFlags.Instanced)) != 0)
    {
        // MyCubeInstanceData packed ortho matrix (Vector4.PackOrthoMatrix, VRage.Math)
        CubeInstance instance = cubeInstances[cubeInstanceStart + instanceId];
        float4 packed = instance.translationAndRot;
        int dir = (int)packed.w;
        float3 forward = PACKED_DIR[dir / 6];
        float3 up = PACKED_DIR[dir - (dir / 6) * 6];
        float3 right = cross(up, -forward);

        // Row-vector cube matrix, as VertexTransformations.hlsli translation_rotation_matrix.
        // HLSL/Slang mul(rowVector, matrix) uses the matrix columns; using dot(position, row)
        // here would apply the transpose. The packed matrix has a translation row, added after
        // the rotation.
        instanceRotation = float3x3(right, up, -forward);
        float3 translation = packed.xyz;
        position = float4(mul(position.xyz, instanceRotation) + translation, 1.0);

        normal = mul(normal, instanceRotation);
        tangent.xyz = mul(tangent.xyz, instanceRotation);
        colorMaskHsv = float4(instance.colorMaskHsv.xyz, 0.0);
    }

    // Row-vector convention: clip = mul(mul(position, world), viewProjection)
    float4 worldPosition4 = float4(dot(position, world0),
                                   dot(position, world1),
                                   dot(position, world2),
                                   1.0);
    float4 clip = float4(dot(worldPosition4, push.frame.viewProj0),
                         dot(worldPosition4, push.frame.viewProj1),
                         dot(worldPosition4, push.frame.viewProj2),
                         dot(worldPosition4, push.frame.viewProj3));

    // MyRenderableComponent.ApplyDepthBias: added to clip z, before the perspective divide
    clip.z += material.depthBias * 1e-6;

    // Geometry/Passes/GBuffer/VertexStage.hlsli: static decals are pushed slightly towards the
    // viewer in clip space so they win the depth test against the surface they are applied to.
    if ((material.flags & uint(MaterialFlags.Decal)) != 0)
        clip.z += 0.0001;

    float3x3 worldRotation = float3x3(world0.xyz, world1.xyz, world2.xyz);

    VertexOutput output;
    output.position = clip;
    output.uv = vertex.texcoord;
    output.normal = normalize(mul(normal, worldRotation));
    output.tangent = float4(normalize(mul(tangent.xyz, worldRotation)), tangent.w);
    output.worldPosition = worldPosition4.xyz;
    output.colorMaskHsv = colorMaskHsv;
    output.drawIndex = drawIndex;
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

// Geometry/PixelUtils.hlsli PixelTangentSpace (derivative tangent basis)
float3x3 pixelTangentSpace(float3 normal, float3 position, float2 uv)
{
    float3 dpx = ddx(position);
    float3 dpy = ddy(position);
    float2 duvx = ddx(uv);
    float2 duvy = ddy(uv);

    float3 dpxPerp = cross(normal, dpx);
    float3 dpyPerp = cross(dpy, normal);
    float3 t = dpyPerp * duvx.x + dpxPerp * duvy.x;
    float3 b = dpyPerp * duvx.y + dpxPerp * duvy.y;
    float invMax = rsqrt(max(dot(t, t), dot(b, b)));
    return float3x3(t * invMax, b * invMax, normal);
}

// Lighting/Brdf.hlsli ToksvigGloss: narrow normal-map detail also narrows the highlight.
float roughnessToSpecular(float roughness)
{
    return max(2.0 / max(roughness * roughness, 1e-6) - 2.0, 0.0);
}

float specularToGloss(float specular)
{
    return 1.0 - sqrt(2.0 / (specular + 2.0));
}

float toksvigGloss(float gloss, float normalLength)
{
    float specular = roughnessToSpecular(max(1.0 - gloss, 0.001));
    float ft = normalLength / lerp(specular, 1.0, normalLength);
    ft = max(ft, 0.001);
    return specularToGloss(ft * specular);
}

float3 addDirectionalLight(float3 n, float3 albedo, float3 f0, float gloss, float nov,
    float3 toCamera, float3 toLight, float3 color, float glossFactor, float diffuseFactor)
{
    if (glossFactor <= 0.0 && diffuseFactor <= 0.0)
        return 0.0;

    float nol = saturate(dot(n, toLight));
    float3 halfVector = normalize(toLight + toCamera);
    float noh = saturate(dot(n, halfVector));
    float voh = saturate(dot(toCamera, halfVector));
    float roughness = max(1.0 - gloss, 0.04);
    float alpha = roughness * roughness;
    float3 fresnel = fresnelSchlick(f0, voh);
    float specular = distributionGGX(noh, alpha) * visibilitySmithGGX(nov, nol, alpha);
    return (albedo * (nol / PI) * diffuseFactor + fresnel * specular * nol * glossFactor) * color;
}


// Sun shadow depth pass. Reuses the opaque ShaderBytecode.Model module (a standalone vertex-only
// module crashes RADV's pipeline compiler). The four "world" push constants carry the light
// view-projection rows instead of an object matrix.
struct ShadowVertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    nointerpolation uint alphamask : TEXCOORD1;
    nointerpolation uint flags : TEXCOORD2;
};

[shader("vertex")]
ShadowVertexOutput shadowMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID,
    uint drawIndex : SV_DrawIndex)
{
    DrawData draw = push.drawData[drawIndex];
    uint index = draw.indices[draw.indexStart + vertexId];
    ModelVertex vertex = draw.vertices[index];
    float4 position = vertex.position;

    if (draw.instanceCount > 0 && draw.instances != nullptr)
    {
        CubeInstance instance = draw.instances[draw.instanceStart + instanceId];
        float4 packed = instance.translationAndRot;
        int dir = (int)packed.w;
        float3 forward = PACKED_DIR[dir / 6];
        float3 up = PACKED_DIR[dir - (dir / 6) * 6];
        float3 right = cross(up, -forward);
        float3x3 rotation = float3x3(right, up, -forward);
        position = float4(mul(position.xyz, rotation) + packed.xyz, 1.0);
    }

    float4 world = float4(dot(position, draw.world0), dot(position, draw.world1),
                         dot(position, draw.world2), 1.0);

    ShadowVertexOutput output;
    output.position = float4(dot(world, push.world0), dot(world, push.world1),
                             dot(world, push.world2), dot(world, push.world3));
    output.uv = vertex.texcoord;
    output.alphamask = draw.material.alphamask;
    output.flags = draw.material.flags;
    return output;
}

// Alpha-masked casters (DECAL_CUTOUT, fences, grilles) keep their cutout silhouette in the
// shadow map instead of casting a full quad. Opaque casters still pay one fragment stage;
// the branch only samples the alphamask for alpha-masked materials.
[shader("fragment")]
void shadowFragment(ShadowVertexOutput input)
{
    if ((input.flags & uint(MaterialFlags.AlphaMasked)) != 0 &&
        (input.flags & uint(MaterialFlags.HasAlphamask)) != 0)
    {
        float alpha = sampleTexture(input.alphamask, SamplerSlot.LinearClamp, input.uv).r;
        if (alpha < 0.5)
            discard;
    }
}

struct ShadowCascadeSample
{
    float4x4 matrix;
    uint slot;
    float extent;
};

ShadowCascadeSample getShadowCascade(int cascade)
{
    ShadowCascadeSample result;
    if (cascade == 0)
    {
        result.matrix = float4x4(push.frame.shadowMatrix0, push.frame.shadowMatrix1,
                                 push.frame.shadowMatrix2, push.frame.shadowMatrix3);
        result.slot = push.frame.shadowMap;
        result.extent = push.frame.shadowCascadeParams.x;
    }
    else if (cascade == 1)
    {
        result.matrix = float4x4(push.frame.shadowMatrix1_0, push.frame.shadowMatrix1_1,
                                 push.frame.shadowMatrix1_2, push.frame.shadowMatrix1_3);
        result.slot = push.frame.shadowMap1;
        result.extent = push.frame.shadowCascadeParams.y;
    }
    else
    {
        result.matrix = float4x4(push.frame.shadowMatrix2_0, push.frame.shadowMatrix2_1,
                                 push.frame.shadowMatrix2_2, push.frame.shadowMatrix2_3);
        result.slot = push.frame.shadowMap2;
        result.extent = push.frame.shadowCascadeParams.z;
    }
    return result;
}

// Soft shadow lookup for one cascade. A fixed wide Poisson kernel is used instead of a stochastic
// blocker search: without temporal accumulation, per-pixel blocker variance reads as dither/static.
float sampleShadowCascade(float4x4 matrix, uint slot, float extent, float3 worldPosition, float3 n,
    float nol, float softness, float rotation)
{
    float texelWorld = 2.0 * extent / max((float)push.frame.shadowSize, 1.0);
    // One-texel normal offset suppresses self-shadowing on thin/edge geometry.
    float4 light = float4(worldPosition + n * texelWorld, 1.0);
    float4 shadowPos = float4(dot(light, matrix[0]), dot(light, matrix[1]),
                              dot(light, matrix[2]), dot(light, matrix[3]));
    float3 ndc = shadowPos.xyz / max(shadowPos.w, 1e-6);
    float2 uv = ndc.xy * 0.5 + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0) || ndc.z < 0.0 || ndc.z > 1.0)
        return 1.0;

    float texel = push.frame.shadowParams.z / max((float)push.frame.shadowSize, 1.0);
    float bias = push.frame.shadowParams.y / max(nol, 0.25);
    float cs = cos(rotation);
    float sn = sin(rotation);
    float2x2 rot = float2x2(cs, -sn, sn, cs);
    float radius = texel * max(softness, 1.0);

    float lit = 0.0;
    [unroll]
    for (int j = 0; j < 16; j++)
    {
        float2 offset = mul(SHADOW_DISK[j], rot) * radius;
        // Linear depth filtering turns the 16 taps into a smoother penumbra than point sampling.
        float stored = sampleTextureLevel(slot, SamplerSlot.LinearClamp, uv + offset, 0.0).r;
        lit += ndc.z >= stored - bias ? 1.0 : 0.0; // reverse-Z: nearer fragment has larger depth
    }
    return lit / 16.0;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target0
{
    DrawMaterial material = push.drawData != nullptr
        ? push.drawData[input.drawIndex].material
        : *push.material;
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

    float metalness = (flags & uint(MaterialFlags.Screen)) != 0
        ? 0.0
        : saturate(cm.w * renderSettings[0].tuning0.w);
    float coloring = extras.w;
    if ((flags & uint(MaterialFlags.MetalnessColorable)) == 0)
    {
        // Geometry/Materials/PixelUtilsMaterials.hlsli RemoveMetalnessFromColoring
        coloring *= saturate(1.0 - saturate(metalness - 0.4) / 0.3);
    }

    float3 paintMask = input.colorMaskHsv.xyz + push.frame.colorizeHsv.xyz;
    float3 baseColor = colorizeGray(cm.rgb, paintMask, coloring);
    baseColor *= material.colorMulEmissive.rgb;
    // Stock SurfaceAlbedo: metals do not contribute diffuse, only their F0 does.
    float3 albedo = baseColor * (1.0 - metalness);

    float3 n = normalize(input.normal);
    float gloss = 0.5;
    if ((flags & uint(MaterialFlags.HasNormalGloss)) != 0)
    {
        float3 normalMap = ng.xyz * 2.0 - 1.0;
        float normalLength = length(normalMap);
        if (dot(input.tangent.xyz, input.tangent.xyz) > 1e-8)
        {
            normalMap.y *= -1.0;
            float3 tangent = normalize(input.tangent.xyz - n * dot(n, input.tangent.xyz));
            float3 bitangent = cross(n, tangent) * input.tangent.w;
            n = normalize(mul(normalMap, float3x3(tangent, bitangent, n)));
        }
        else
        {
            normalMap.x *= -1.0;
            n = normalize(mul(normalMap, pixelTangentSpace(n, input.worldPosition, input.uv)));
        }

        gloss = toksvigGloss(ng.w, min(normalLength, 1.0));
    }

    float ao = extras.x;
    float emissive = saturate(extras.y - 1.0 / 255.0) * material.colorMulEmissive.w;

    float3 toCamera = normalize(-input.worldPosition);
    float3 toSun = -normalize(push.frame.sunDirection.xyz);
    float3 sunColor = push.frame.sunColor.rgb * push.frame.sunDirection.w * renderSettings[0].tuning0.x;

    float3 faceNormal = normalize(cross(ddx(input.worldPosition), ddy(input.worldPosition)));
    if (dot(faceNormal, toCamera) < 0.0)
        faceNormal = -faceNormal;
    float nol = saturate(dot(faceNormal, toSun));
    n = faceNormal;
    float3 halfVector = normalize(toSun + toCamera);
    float noh = saturate(dot(n, halfVector));
    float voh = saturate(dot(toCamera, halfVector));
    float nov = saturate(dot(n, toCamera));

    float roughness = max(1.0 - gloss, 0.04);
    float alpha = roughness * roughness;
    float3 f0 = lerp(0.04, baseColor, metalness);
    float3 fresnel = fresnelSchlick(f0, voh);
    float specular = distributionGGX(noh, alpha) * visibilitySmithGGX(nov, nol, alpha);

    float3 diffuse = albedo * (nol / PI);
    float3 direct = (diffuse + fresnel * specular * nol * renderSettings[0].tuning0.z) * sunColor;

    // Sun shadow cascades (reverse-Z). Three maps: a tight near cascade for close-up texel density,
    // a mid cascade and a far cascade. The filter narrows with distance so the far map does not turn
    // into visible dither, and neighbouring cascades overlap across a split to avoid a pop.
    float shadowFactor = 1.0;
    if (push.frame.shadowEnabled != 0)
    {
        float viewDistance = length(input.worldPosition);
        float split0 = push.frame.shadowSplits.x;
        float split1 = push.frame.shadowSplits.y;
        float split2 = push.frame.shadowSplits.z;

        int cascade = viewDistance < split0 ? 0 : (viewDistance < split1 ? 1 : 2);
        if (viewDistance < split2)
        {
            float nolGeometric = saturate(dot(faceNormal, toSun));

            // Stable per-cascade rotation and filter width. The near cascade gets the widest
            // filter (in its smaller texels); distant cascades need less screen-space dither.
            ShadowCascadeSample current = getShadowCascade(cascade);
            float softness = push.frame.shadowParams.w *
                (cascade == 0 ? 1.0 : (cascade == 1 ? 0.8 : 0.6));
            shadowFactor = sampleShadowCascade(current.matrix, current.slot, current.extent,
                input.worldPosition, faceNormal, nolGeometric, softness, (float)cascade * 1.1);

            // Blend into the next cascade near each split so the wider filter/texel grid does not
            // pop as the camera crosses a cascade boundary.
            float blend = 0.0;
            if (cascade == 0 && viewDistance > split0 * 0.88)
                blend = smoothstep(split0 * 0.88, split0, viewDistance);
            else if (cascade == 1 && viewDistance > split1 * 0.90)
                blend = smoothstep(split1 * 0.90, split1, viewDistance);

            if (blend > 0.0)
            {
                ShadowCascadeSample next = getShadowCascade(cascade + 1);
                float nextSoftness = push.frame.shadowParams.w * (cascade == 0 ? 0.8 : 0.6);
                float nextShadow = sampleShadowCascade(next.matrix, next.slot, next.extent,
                    input.worldPosition, faceNormal, nolGeometric, nextSoftness,
                    (float)(cascade + 1) * 1.1);
                shadowFactor = lerp(shadowFactor, nextShadow, blend);
            }

            direct *= lerp(1.0, shadowFactor, saturate(push.frame.shadowParams.x));
        }
    }

    // Dynamic point/spot lights. The game's light component already multiplies intensity into the
    // linear color; falloff is the converted polynomial from MyLightComponent.
    if (push.frame.lightCount != 0)
    {
        GpuLight* lights = push.frame.lights;
        uint lightCount = min(push.frame.lightCount, 64u);
        for (uint i = 0; i < lightCount; i++)
        {
            GpuLight light = lights[i];
            float3 toLight = light.positionRange.xyz - input.worldPosition;
            float distanceToLight = length(toLight);
            float range = light.positionRange.w;
            if (distanceToLight >= range || range <= 0.0)
                continue;

            float3 L = toLight / max(distanceToLight, 1e-4);
            float d = distanceToLight / range;
            float dq = d * d;
            float attenuationHard = pow(saturate(1.0 - dq * dq), 3.0);
            float attenuationDiffuse = 1.0 / (1.0 + light.colorFalloff.w * dq);

            if (light.params.w > 0.5)
            {
                // Stock LightSpot.hlsl: clip when -dot(L, direction) < apertureCos
                float cosAngle = -dot(L, normalize(light.direction.xyz));
                float cone = saturate((cosAngle - light.params.z) / max(1.0 - light.params.z, 1e-3));
                cone = cone * cone * (3.0 - 2.0 * cone);
                attenuationHard *= cone;
            }

            float nolLight = saturate(dot(faceNormal, L));
            if (attenuationHard <= 0.0 || nolLight <= 0.0)
                continue;

            float3 halfLight = normalize(L + toCamera);
            float nohLight = saturate(dot(n, halfLight));
            float vohLight = saturate(dot(toCamera, halfLight));
            float specularLight = distributionGGX(nohLight, alpha) *
                                  visibilitySmithGGX(nov, nolLight, alpha);
            float3 fresnelLight = fresnelSchlick(f0, vohLight);

            float3 lightColor = light.colorFalloff.rgb * attenuationHard * attenuationDiffuse * renderSettings[0].tuning1.y;
            float3 lightDiffuse = albedo * (nolLight / PI) * light.params.y * lightColor;
            float3 lightSpecular = fresnelLight * specularLight * nolLight *
                                   light.params.x * lightColor;
            direct += lightDiffuse + lightSpecular;
        }
    }

    // Stock: shaded += albedo * emissive * BloomEmissiveness. LCD screen materials use the
    // ScreenArea_add/CockpitScreen_add emissive channel, so the display does not depend on scene
    // lighting. The Screen flag also forces metalness 0 (generated targets have opaque alpha).
    float screenScale = (flags & uint(MaterialFlags.Screen)) != 0 ? renderSettings[0].tuning1.w : 1.0;
    float3 emissiveColor = albedo * emissive * push.frame.emissiveScale *
                           renderSettings[0].tuning1.x * screenScale;

    // Forward ambient floor. Full skybox irradiance is intentionally not used here: the game's
    // AmbientForwardPass is the stock forward ambient and avoids washing albedo out to white.
    float3 ambient = albedo * push.frame.ambientColor.rgb * push.frame.ambientColor.w *
                     renderSettings[0].tuning0.y * ao;
    ambient += albedo * renderSettings[0].tuning0.y * 0.25;
    float3 ambientSpecular = 0;
    if (push.frame.iblCube != 0 && renderSettings[0].tuning1.z > 0.0)
    {
        float maxIblMip = max(push.frame.skyboxParams.y - 1.0, 0.0);
        float3 reflection = reflect(-toCamera, n);
        float3 prefiltered = iblColor(*push.frame, reflection, (1.0 - gloss) * maxIblMip);
        float2 brdf = sampleTexture(push.frame.brdfLut, SamplerSlot.LinearClamp,
                                    float2(gloss, nov)).rg;
        ambientSpecular = prefiltered * (f0 * brdf.x + brdf.y) * push.frame.skyboxParams.w * ao *
                          renderSettings[0].tuning1.z;
    }
    int debugView = (int)push.frame.colorizeHsv.w;
    if (debugView == 1)
        return float4(n * 0.5 + 0.5, 1.0);
    if (debugView == 2)
        return float4(direct, 1.0);
    if (debugView == 3)
        return float4(ambient, 1.0);
    if (debugView == 4)
        return float4(emissiveColor, 1.0);
    if (debugView == 5)
        return float4(albedo, 1.0);
    if (debugView == 6)
        return float4(shadowFactor.xxx, 1.0);
    return float4(direct + ambient + ambientSpecular + emissiveColor, 1.0);
}