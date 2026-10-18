using System.Collections.Generic;
using VRageMath;
using VRageRender.Import;

namespace ClientPlugin.Render.Models;

// CPU side of a loaded MWM: decoded vertex streams, combined index list and material parts.
// Produced on a worker thread by MwmLoader, consumed on the render thread by ModelCache.
internal sealed class MwmMeshData
{
    public Vector4[] Positions;   // xyz, w = 1
    public Vector2[] Texcoords;
    public uint[] Normals;        // VF_Packer.PackNormalB4
    public uint[] Tangents;       // VF_Packer.PackTangentSignB4
    public uint[] Indices;
    public List<MwmPartData> Parts;
    public BoundingBox Bounds;
    public bool HasTangents;
    public bool Skinned;

    // Skinning streams (model bone space, stock layout)
    public Vector4I[] BoneIndices;
    public Vector4[] BoneWeights;

    // LOD1..N model files and the switching distances from the parent file's LODs tag
    public string[] LodModels;
    public float[] LodDistances;
}

internal sealed class MwmPartData
{
    public MyMeshDrawTechnique Technique;
    public string MaterialName;
    public int IndexStart;
    public int IndexCount;
    public string ColorMetal;
    public string NormalGloss;
    public string Extensions;
    public string Alphamask;

    // Glass materials can carry a separate CCW material name (MyMaterialDescriptor.GlassCCW)
    public string GlassCCW;
}

internal static class MeshTechniques
{
    // Techniques that consume the skinned vertex stream and need a bone palette.
    public static bool IsSkinned(MyMeshDrawTechnique technique) =>
        technique is MyMeshDrawTechnique.SKINNED or MyMeshDrawTechnique.MESH_INSTANCED_SKINNED;
}
