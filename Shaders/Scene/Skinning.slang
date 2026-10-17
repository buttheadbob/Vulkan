// GPU skinning: deforms the skinned vertex buffer into the normal static vertex layout using the
// per-actor bone palette. Runs as a compute pass before the scene rendering pass; the forward shaders
// then read the output exactly like any other ModelVertex buffer.

// Must match ClientPlugin/Render/Models/ModelCache.cs (64 bytes)
struct SkinnedVertex
{
    float4 position;
    float4 boneWeights;
    float2 texcoord;
    uint normal;
    uint tangent;
    uint boneIndices;    // four bytes
    uint pad0;
    uint pad1;
    uint pad2;
};

// Must match ClientPlugin/Render/Models/ModelCache.cs (32 bytes)
struct StaticVertex
{
    float4 position;
    float2 texcoord;
    uint normal;
    uint tangent;
};

// Four rows of a row-vector XNA matrix
struct BoneMatrix
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 row3;
};

// Must match ClientPlugin/Render/Scene/ModelPass.cs
struct PushConstants
{
    float4 world0;   // transposed world rows (camera-relative)
    float4 world1;
    float4 world2;
    float4 world3;
    SkinnedVertex* input;
    StaticVertex* output;
    BoneMatrix* bones;
    uint vertexCount;
    uint boneCount;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

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

uint packNormal(float3 n)
{
    float2 xy = 0.5 * (n.xy + 1.0);
    uint x = (uint)(xy.x * 32767.0);
    uint y = (uint)(xy.y * 32767.0);
    uint zsign = n.z > 0.0 ? 1u : 0u;
    return x | (zsign << 15) | (y << 16);
}

uint packTangentSign(float4 tangent)
{
    float2 xy = 0.5 * (tangent.xy + 1.0);
    uint x = (uint)(xy.x * 32767.0);
    uint y = (uint)(xy.y * 32767.0);
    uint zsign = tangent.z > 0.0 ? 1u : 0u;
    y |= (tangent.w > 0.0 ? 1u : 0u) << 15;
    return x | (zsign << 15) | (y << 16);
}

[numthreads(64, 1, 1)]
[shader("compute")]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint index = threadId.x;
    if (index >= push.vertexCount || push.boneCount == 0)
        return;

    SkinnedVertex vertex = push.input[index];
    float4 weights = vertex.boneWeights;
    uint4 indices = uint4(vertex.boneIndices & 0xFF,
                          (vertex.boneIndices >> 8) & 0xFF,
                          (vertex.boneIndices >> 16) & 0xFF,
                          vertex.boneIndices >> 24);

    float3 objectNormal = unpackNormal(vertex.normal);
    float4 objectTangent = unpackTangentSign(vertex.tangent);

    float3 skinnedPosition = 0;
    float3 skinnedNormal = 0;
    float3 skinnedTangent = 0;
    float tangentSign = 0;
    float3 objectPosition = vertex.position.xyz;

    for (int i = 0; i < 4; i++)
    {
        float weight = weights[i];
        if (weight == 0.0)
            continue;

        uint boneIndex = min(indices[i], push.boneCount - 1);
        BoneMatrix bone = push.bones[boneIndex];
        float3x3 boneRotation = float3x3(bone.row0.xyz, bone.row1.xyz, bone.row2.xyz);

        // XNA row-vector transform: x = dot(p, column0) + M41, etc. The rows are untransposed here,
        // so the columns are assembled explicitly and the translation comes from row3.
        skinnedPosition += weight * float3(
            dot(objectPosition, float3(bone.row0.x, bone.row1.x, bone.row2.x)) + bone.row3.x,
            dot(objectPosition, float3(bone.row0.y, bone.row1.y, bone.row2.y)) + bone.row3.y,
            dot(objectPosition, float3(bone.row0.z, bone.row1.z, bone.row2.z)) + bone.row3.z);
        skinnedNormal += weight * mul(objectNormal, boneRotation);
        skinnedTangent += weight * mul(objectTangent.xyz, boneRotation);
        tangentSign += weight * objectTangent.w;
    }

    // The compute pass applies the camera-relative entity world matrix; the graphics pass uses identity.
    float4 skinnedPosition4 = float4(skinnedPosition, 1.0);
    float3 worldPosition = float3(dot(skinnedPosition4, push.world0),
                                  dot(skinnedPosition4, push.world1),
                                  dot(skinnedPosition4, push.world2));
    float3x3 worldRotation = float3x3(push.world0.xyz, push.world1.xyz, push.world2.xyz);
    float3 worldNormal = normalize(mul(skinnedNormal, worldRotation));
    float3 worldTangent = normalize(mul(skinnedTangent, worldRotation));

    StaticVertex output;
    output.position = float4(worldPosition, 1.0);
    output.texcoord = vertex.texcoord;
    output.normal = packNormal(worldNormal);
    output.tangent = packTangentSign(float4(worldTangent, tangentSign >= 0.0 ? 1.0 : -1.0));
    push.output[index] = output;
}
