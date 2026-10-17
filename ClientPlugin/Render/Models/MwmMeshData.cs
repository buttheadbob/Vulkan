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
}
