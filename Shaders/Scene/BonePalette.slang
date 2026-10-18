// GPU bone palette assembly: palette[modelBone] = skinTransform[skeletonBone] * absolute[skeletonBone].
// Runs as a small compute pass before the skinning pass; the palette is read through a buffer address.

import Common.SkinningCommon;

// Must match ClientPlugin/Render/Scene/ModelPass.cs
struct PushConstants
{
    BoneMatrix* skinTransforms;   // per skeleton bone (bind-inverse)
    BoneMatrix* boneTransforms;   // per skeleton bone (absolute, per frame)
    uint* indices;                // model bone -> skeleton bone
    BoneMatrix* output;           // per model bone
    uint indexCount;
    uint skeletonCount;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> push;

BoneMatrix multiplyBoneMatrices(BoneMatrix a, BoneMatrix b)
{
    BoneMatrix result;
    result.row0 = a.row0.x * b.row0 + a.row0.y * b.row1 + a.row0.z * b.row2 + a.row0.w * b.row3;
    result.row1 = a.row1.x * b.row0 + a.row1.y * b.row1 + a.row1.z * b.row2 + a.row1.w * b.row3;
    result.row2 = a.row2.x * b.row0 + a.row2.y * b.row1 + a.row2.z * b.row2 + a.row2.w * b.row3;
    result.row3 = a.row3.x * b.row0 + a.row3.y * b.row1 + a.row3.z * b.row2 + a.row3.w * b.row3;
    return result;
}

[numthreads(64, 1, 1)]
[shader("compute")]
void main(uint3 threadId : SV_DispatchThreadID)
{
    uint modelBone = threadId.x;
    if (modelBone >= push.indexCount)
        return;

    uint skeletonBone = push.indices[modelBone];
    if (skeletonBone >= push.skeletonCount)
    {
        push.output[modelBone] = (BoneMatrix)0;
        return;
    }

    push.output[modelBone] = multiplyBoneMatrices(
        push.skinTransforms[skeletonBone],
        push.boneTransforms[skeletonBone]);
}
