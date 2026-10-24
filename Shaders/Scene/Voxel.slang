// Basic voxel/planet rendering. The game's clipmap builds the mesh; this shader draws it with a
// cheap material palette instead of the full stock triplanar texture stack. This is intentionally
// low-detail: it gives planets, asteroids and snow/grass ground a recognizable colour so the
// environment probe can reflect them into ships.

import Common.Bindless;
import Common.Environment;

// 24 bytes, matching VRage.MyVertexFormatVoxelSingleData split into position+material and
// normal+colorShift streams. Standard vertex input: no physical-storage-buffer pointer is used for
// per-cell geometry, which avoids invalid-address faults on drivers.
struct VoxelVertex
{
    [[vk::location(0)]] float3 position;
    [[vk::location(1)]] uint material;
    [[vk::location(2)]] uint packedNormal;
    [[vk::location(3)]] uint colorShift;
};

struct VoxelMaterial
{
    uint colorMetalXZnY;
    uint colorMetalY;
    uint normalGlossXZnY;
    uint normalGlossY;
    uint extensionsXZnY;
    uint extensionsY;
    float tiling;
    uint flags;
    float4 color;
};

struct PushConstants
{
    FrameConstants* frame;
    VoxelMaterial* materials;
    float4 world0;
    float4 world1;
    float4 world2;
    float4 world3;
    float4 color;
    uint materialIndex;
    float3 voxelOffset;
    float voxelScale;
    uint pad0;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

static const float PI = 3.14159265359;

struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float3 worldPosition : TEXCOORD1;
    nointerpolation uint material : TEXCOORD2;
    float3 texCoord : TEXCOORD3;
    float3 localNormal : TEXCOORD4;
};

// VF_Packer.PackNormal / stock unpack_normal (same packing as the model normal stream).
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

[shader("vertex")]
VertexOutput vertexMain(VoxelVertex vertex)
{
    float4 position = float4(vertex.position, 1.0);
    float3 normal = unpackNormal(vertex.packedNormal);

    // Camera-relative row-vector convention, matching Model.slang.
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
    output.normal = normalize(mul(normal, worldRotation));
    output.worldPosition = worldPosition.xyz;
    output.material = vertex.material;
    output.texCoord = vertex.position * push.voxelScale + push.voxelOffset;
    output.localNormal = normal;
    return output;
}

// The main scene uses three HDR attachments; voxels write the same normal/gloss and F0/AO
// channels as opaque models so screen-space reflections and the composite keep working.
struct FragmentOutput
{
    float4 color : SV_Target0;
    float4 gbuffer : SV_Target1;
    float4 gbuffer2 : SV_Target2;
};

// Stock GetTriplanarWeights
float3 triplanarWeights(float3 n)
{
    float3 w = saturate(abs(n) - 0.55);
    w *= w;
    w *= w;
    return w / max(dot(w, 1.0), 1e-4);
}

[shader("fragment")]
FragmentOutput fragmentScene(VertexOutput input)
{
    FrameConstants frame = *push.frame;
    VoxelMaterial material = push.materials[min(push.materialIndex, 255u)];
    float3 n = normalize(input.normal);
    float3 local = normalize(input.localNormal);
    float3 weights = triplanarWeights(local);
    float3 tc = input.texCoord * max(material.tiling, 0.001);
    float2 uvX = tc.zy;
    float2 uvY = tc.xz;
    float2 uvZ = tc.xy;

    float4 cmX = sampleTexture(material.colorMetalXZnY, SamplerSlot.AnisotropicWrap, uvX);
    float4 cmY = sampleTexture(material.colorMetalY, SamplerSlot.AnisotropicWrap, uvY);
    float4 cmZ = sampleTexture(material.colorMetalXZnY, SamplerSlot.AnisotropicWrap, uvZ);
    float4 cm = cmX * weights.x + cmY * weights.y + cmZ * weights.z;

    float4 ngX = sampleTexture(material.normalGlossXZnY, SamplerSlot.AnisotropicWrap, uvX);
    float4 ngY = sampleTexture(material.normalGlossY, SamplerSlot.AnisotropicWrap, uvY);
    float4 ngZ = sampleTexture(material.normalGlossXZnY, SamplerSlot.AnisotropicWrap, uvZ);
    float4 ng = ngX * weights.x + ngY * weights.y + ngZ * weights.z;

    float4 extX = sampleTexture(material.extensionsXZnY, SamplerSlot.AnisotropicWrap, uvX);
    float4 extY = sampleTexture(material.extensionsY, SamplerSlot.AnisotropicWrap, uvY);
    float4 extZ = sampleTexture(material.extensionsXZnY, SamplerSlot.AnisotropicWrap, uvZ);
    float4 ext = extX * weights.x + extY * weights.y + extZ * weights.z;

    // Textures are loaded asynchronously; while a slot is not ready, keep the far-colour palette.
    bool hasColorMetal = (material.flags & 1u) != 0 && dot(cm.rgb, cm.rgb) > 0.0001;
    float3 base = hasColorMetal ? saturate(cm.rgb) : saturate(push.color.rgb);
    if (dot(base, base) < 0.0001)
        base = float3(0.26, 0.25, 0.23);
    base *= lerp(float3(1.0), material.color.rgb, 0.2);
    float gloss = (material.flags & 2u) != 0 ? saturate(ng.a) : 0.15;
    float ao = (material.flags & 4u) != 0 ? saturate(ext.r) : 1.0;
    float metalness = saturate(cm.a);

    float3 toSun = -normalize(frame.sunDirection.xyz);
    float nol = saturate(dot(n, toSun));
    float3 sunColor = frame.sunColor.rgb * frame.sunDirection.w * renderSettings[0].tuning0.x;
    // Stock forward path: Lambert albedo, plus a small emissive extension term.
    float3 direct = base * nol / PI * sunColor * 0.18;
    float3 ambient = base * (frame.ambientColor.rgb * frame.ambientColor.w +
                             renderSettings[0].tuning0.y * 0.25) * ao;
    float3 emissive = base * ext.g * frame.emissiveScale * renderSettings[0].tuning1.x;
    float3 color = direct + ambient + emissive;

    FragmentOutput output;
    output.color = float4(color, 1.0);
    output.gbuffer = float4(n * 0.5 + 0.5, gloss);
    output.gbuffer2 = float4(lerp(float3(0.04), base, metalness), ao);
    return output;
}
