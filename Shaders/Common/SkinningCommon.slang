// Shared layouts for GPU skinning. Must match ClientPlugin/Render/Models/ModelCache.cs.

module SkinningCommon;

// Input format of the skinned MWM vertex buffer (64 bytes)
public struct SkinnedVertex
{
    public float4 position;
    public float4 boneWeights;
    public float2 texcoord;
    public uint normal;
    public uint tangent;
    public uint boneIndices;    // four bytes
    public uint pad0;
    public uint pad1;
    public uint pad2;
};

// Output format, identical to the normal ModelVertex (32 bytes)
public struct StaticVertex
{
    public float4 position;
    public float2 texcoord;
    public uint normal;
    public uint tangent;
};

// Four rows of a row-vector XNA matrix (64 bytes)
public struct BoneMatrix
{
    public float4 row0;
    public float4 row1;
    public float4 row2;
    public float4 row3;
};
