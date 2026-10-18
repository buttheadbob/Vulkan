using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin.Render;
using VRage.FileSystem;
using VRage.Import;
using VRageMath;
using VRageMath.PackedVector;
using VRageRender.Import;

namespace ClientPlugin.Render.Models;

// Reads the game's MWM format through the renderer-agnostic MyModelImporter and decodes it into
// tightly packed streams. The formulas mirror MyMwmData/MyLodUtils and VF_Packer (see Docs/Roadmap.md).
internal static class MwmLoader
{
    private static readonly string[] Tags =
    [
        "Vertices", "BlendIndices", "BlendWeights", "Normals", "TexCoords0", "Tangents", "Binormals",
        "Bones", "MeshParts", "Sections", "BoundingBox", "BoundingSphere", "LODs", "PatternScale",
        "GeometryDataAsset",
    ];

    public static MwmMeshData Load(string model)
    {
        var path = ResolveModelFile(model);
        return LoadFile(path, 0);
    }

    // Loads one file without following its LOD descriptors (ModelCache loads those explicitly)
    public static MwmMeshData LoadPath(string absolutePath) => LoadFile(absolutePath, 0);

    private static MwmMeshData LoadFile(string path, int depth)
    {
        if (depth > 4)
            throw new InvalidDataException($"GeometryDataAsset chain too deep at {path}");

        var importer = new MyModelImporter();
        importer.ImportData(path, Tags);
        var tags = importer.GetTagData();
        try
        {
            // The LOD descriptors belong to this file, even when it is a stub whose geometry lives elsewhere
            var lodInfo = ReadLods(tags, path);

            // Stub models reference the file that holds the actual geometry
            if (tags.TryGetValue("GeometryDataAsset", out var geometry))
            {
                var stub = LoadFile(ResolveModelFile((string)geometry), depth + 1);
                if (lodInfo.HasValue)
                {
                    stub.LodModels = lodInfo.Value.Models;
                    stub.LodDistances = lodInfo.Value.Distances;
                }

                return stub;
            }

            if (!tags.TryGetValue("Vertices", out var verticesTag))
                throw new InvalidDataException("No Vertices tag");

            var positions = (HalfVector4[])verticesTag;
            var normals = tags.TryGetValue("Normals", out var n) ? (Byte4[])n : [];
            var tangents = tags.TryGetValue("Tangents", out var t) ? (Byte4[])t : [];
            var binormals = tags.TryGetValue("Binormals", out var b) ? (Byte4[])b : [];
            var texcoords = tags.TryGetValue("TexCoords0", out var uv) ? (HalfVector2[])uv : [];
            var boneIndices = tags.TryGetValue("BlendIndices", out var bi) ? (Vector4I[])bi : [];
            var boneWeights = tags.TryGetValue("BlendWeights", out var bw) ? (Vector4[])bw : [];

            var count = positions.Length;
            var data = new MwmMeshData
            {
                Positions = new Vector4[count],
                Texcoords = new Vector2[count],
                Normals = new uint[count],
                Tangents = new uint[count],
                // MyMwmData.IsSkinnedProperly; note that MESH parts of such a model still render statically
                Skinned = count != 0 && boneIndices.Length == count && boneWeights.Length == count,
                Bounds = tags.TryGetValue("BoundingBox", out var bounds) ? (BoundingBox)bounds : default,
                BoneIndices = boneIndices,
                BoneWeights = boneWeights,
            };

            var patternScale = tags.TryGetValue("PatternScale", out var ps) ? (float)ps : 1f;
            var hasTangents = tangents.Length == count && binormals.Length == count && count != 0;
            data.HasTangents = hasTangents;

            var up = new Vector3(0, 0, 1);
            var defaultNormal = VF_Packer.PackNormalB4(ref up);
            for (var i = 0; i < count; i++)
            {
                data.Positions[i] = new Vector4(VF_Packer.UnpackPosition(positions[i]), 1f);
                if (i < texcoords.Length)
                {
                    var uvValue = texcoords[i].ToVector2();
                    data.Texcoords[i] = patternScale != 1f ? uvValue / patternScale : uvValue;
                }

                data.Normals[i] = i < normals.Length ? normals[i].PackedValue : defaultNormal.PackedValue;
                if (hasTangents)
                {
                    // MyMwmData.CreateAlteredTangents: the packed tangent stores the sign of cross(t, n) . b
                    var normal = VF_Packer.UnpackNormal(normals[i].PackedValue);
                    var tangent = VF_Packer.UnpackNormal(tangents[i].PackedValue);
                    var bitangent = VF_Packer.UnpackNormal(binormals[i].PackedValue);
                    var tangentW = new Vector4(tangent, Vector3.Cross(tangent, normal).Dot(bitangent) < 0f ? -1f : 1f);
                    data.Tangents[i] = VF_Packer.PackTangentSignB4(ref tangentW).PackedValue;
                }
            }

            data.Parts = ReadParts(tags, data, path);
            if (count == 0)
                data.Bounds = default;
            else if (data.Bounds.Min == data.Bounds.Max)
                data.Bounds = ComputeBounds(data.Positions);

            if (lodInfo.HasValue)
            {
                data.LodModels = lodInfo.Value.Models;
                data.LodDistances = lodInfo.Value.Distances;
            }

            return data;
        }
        finally
        {
            importer.Clear();
        }
    }

    private static List<MwmPartData> ReadParts(Dictionary<string, object> tags, MwmMeshData data, string modelPath)
    {
        var contentPath = ContentPathOf(modelPath);
        var parts = new List<MwmPartData>();
        var indices = new List<uint>();

        if (tags.TryGetValue("MeshParts", out var meshPartsTag))
        {
            foreach (var part in (List<MyMeshPartInfo>)meshPartsTag)
            {
                var descriptor = part.m_MaterialDesc;
                var partData = new MwmPartData
                {
                    Technique = part.Technique,
                    MaterialName = part.GetMaterialName(),
                    IndexStart = indices.Count,
                    IndexCount = part.m_indices.Count,
                    ColorMetal = ResolveTexture(Texture(descriptor, "ColorMetalTexture"), contentPath),
                    NormalGloss = ResolveTexture(Texture(descriptor, "NormalGlossTexture"), contentPath),
                    Extensions = ResolveTexture(Texture(descriptor, "AddMapsTexture"), contentPath),
                    Alphamask = ResolveTexture(Texture(descriptor, "AlphamaskTexture"), contentPath),
                    GlassCCW = descriptor?.GlassCCW ?? "",
                };

                foreach (var index in part.m_indices)
                    indices.Add((uint)index);

                parts.Add(partData);
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new MwmPartData
            {
                Technique = MyMeshDrawTechnique.MESH,
                MaterialName = "",
                IndexStart = 0,
                IndexCount = 0,
            });
        }

        data.Indices = indices.ToArray();
        return parts;
    }

    // MyModel.LoadFromFile: LOD0 is the file itself, LOD1..N come from the LODs descriptors
    private static (string[] Models, float[] Distances)? ReadLods(Dictionary<string, object> tags, string modelPath)
    {
        if (!tags.TryGetValue("LODs", out var tag) || tag is not MyLODDescriptor[] descriptors || descriptors.Length == 0)
            return null;

        var models = new List<string>();
        var distances = new List<float>();
        foreach (var descriptor in descriptors)
        {
            if (string.IsNullOrEmpty(descriptor.Model))
                continue;

            // MyLODDescriptor.GetModelAbsoluteFilePath, case-safe for Linux
            var model = NormalizeSeparators(descriptor.Model);
            if (!model.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase))
                model += ".mwm";

            var lowerParent = modelPath.ToLowerInvariant();
            var modelsIndex = lowerParent.IndexOf("models", StringComparison.Ordinal);
            var path = modelsIndex >= 0 && Path.IsPathRooted(modelPath)
                ? ExistingFile(Path.Combine(modelPath[..modelsIndex], model))
                : null;
            path ??= ExistingFile(Path.Combine(MyFileSystem.ContentPath, model));

            if (path == null)
            {
                RenderLog.Warning($"LOD model '{descriptor.Model}' of {modelPath} not found");
                continue;
            }

            models.Add(path);
            distances.Add(descriptor.Distance);
        }

        return models.Count == 0 ? null : (models.ToArray(), distances.ToArray());
    }

    // Game assets use Windows separators; use the running platform's separator on both pipelines
    private static string NormalizeSeparators(string path) =>
        path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string ExistingFile(string path) =>
        MyFileSystem.FileExists(path) ? path : null;

    private static string Texture(MyMaterialDescriptor descriptor, string key) =>
        descriptor != null && descriptor.Textures.TryGetValue(key, out var value) ? value : "";

    internal static BoundingBox ComputeBounds(Vector4[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in positions)
        {
            var p = new Vector3(position);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new BoundingBox(min, max);
    }

    // MyMwmUtils.GetFullMwmFilepath, made case-safe for Linux: exact case first, lower case as fallback
    public static string ResolveModelFile(string model)
    {
        if (string.IsNullOrEmpty(model))
            return model;

        var path = NormalizeSeparators(model);
        if (!path.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase))
            path += ".mwm";
        if (!Path.IsPathRooted(path))
            path = Path.Combine(MyFileSystem.ContentPath, path);

        // MyFileSystem resolves paths case-insensitively like the game (raw File.Exists does not on Linux)
        if (MyFileSystem.FileExists(path))
            return path;

        var lower = path.ToLowerInvariant();
        return MyFileSystem.FileExists(lower) ? lower : path;
    }

    // MyMwmUtils.GetFullMwmContentPath: everything before the "models" folder, so textures next to the model win
    private static string ContentPathOf(string modelPath)
    {
        var lower = modelPath.ToLowerInvariant();
        var index = lower.LastIndexOf("models", StringComparison.Ordinal);
        return index >= 0 ? modelPath[..index] : null;
    }

    // MyResourceUtils.GetTextureFullPath, made case-safe for Linux
    private static string ResolveTexture(string texture, string contentPath)
    {
        if (string.IsNullOrEmpty(texture))
            return "";

        var path = NormalizeSeparators(texture);
        if (Path.IsPathRooted(path))
            return ExistingOrLower(path);

        if (!string.IsNullOrEmpty(contentPath))
        {
            var candidate = Path.Combine(contentPath, path);
            if (MyFileSystem.FileExists(candidate))
                return candidate;
        }

        return ExistingOrLower(Path.Combine(MyFileSystem.ContentPath, path));
    }

    private static string ExistingOrLower(string path) =>
        MyFileSystem.FileExists(path) ? path : MyFileSystem.FileExists(path.ToLowerInvariant()) ? path.ToLowerInvariant() : path;
}
