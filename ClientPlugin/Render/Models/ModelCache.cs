using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRage.Import;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Import;
using VRageRender.Messages;

namespace ClientPlugin.Render.Models;

// One vertex stream read by Shaders/Scene/Model.slang. 32 bytes, std430 safe (float4 first).
[StructLayout(LayoutKind.Sequential)]
internal struct ModelVertex
{
    public Vector4 Position;
    public Vector2 Texcoord;
    public uint Normal;
    public uint Tangent;

    public const uint Size = 32;
}

// Input format for the GPU skinning compute pass. 64 bytes, std430 safe. The compute pass writes
// the normal 32-byte ModelVertex layout, so the forward shaders stay unchanged.
[StructLayout(LayoutKind.Sequential)]
internal struct SkinnedVertex
{
    public Vector4 Position;      // 16
    public Vector4 BoneWeights;   // 16
    public Vector2 Texcoord;      // 8
    public uint Normal;           // 4
    public uint Tangent;          // 4
    public uint BoneIndices;      // 4 (four bytes)
    public uint Pad0;             // 4
    public uint Pad1;             // 4
    public uint Pad2;             // 4
}

internal sealed class GpuLod : IDisposable
{
    public GpuBuffer Vertices;
    public GpuBuffer Indices;
    public ModelPart[] Parts;
    public BoundingBox Bounds;
    public bool HasTangents;
    public bool Skinned;
    public uint VertexCount;
    public int VertexStride;

    public void Dispose()
    {
        Vertices?.Dispose();
        Indices?.Dispose();
    }
}

internal sealed class GpuModel : IDisposable
{
    public string Name;
    public GpuLod[] Lods;
    public float[] LodDistances; // LOD1..N thresholds, same order as Lods[1..]
    public BoundingBox Bounds;   // union of all LOD bounds

    public int LodCount => Lods.Length;

    // MyLodStrategyInfo.GetTheBestLodWithHisteresis (10% dead zone by default)
    public GpuLod SelectLod(float distance, ref int currentLod, float histeresisRatio)
    {
        var count = Lods.Length;
        if (count == 1)
        {
            currentLod = 0;
            return Lods[0];
        }

        var lod = Math.Clamp(currentLod, 0, count - 1);

        // Move towards near LODs while below their low threshold
        while (lod > 0)
        {
            var threshold = LodDistances[lod - 1];
            if (distance >= threshold - threshold * histeresisRatio)
                break;
            lod--;
        }

        // Move towards far LODs while above the high threshold of the current one
        while (lod < count - 1)
        {
            var threshold = LodDistances[lod];
            if (distance <= threshold + threshold * histeresisRatio)
                break;
            lod++;
        }

        currentLod = lod;
        return Lods[lod];
    }

    public void Dispose()
    {
        foreach (var lod in Lods)
            lod?.Dispose();
    }
}

internal sealed class ModelPart
{
    public MyMeshDrawTechnique Technique;
    public string MaterialName;
    public int IndexStart;
    public int IndexCount;
    public TextureCache.Entry ColorMetal;
    public TextureCache.Entry NormalGloss;
    public TextureCache.Entry Extensions;
    public TextureCache.Entry Alphamask;

    // Transparent techniques (glass/holo/shield): stock transparent material plus its textures
    public MyTransparentMaterial Transparent;
    public TextureCache.Entry TransparentColorTexture;
    public TextureCache.Entry TransparentGlossTexture;
}

// Model path -> parsed MWM with all its LODs -> GPU vertex/index buffers. Parsing runs on worker
// threads; GPU buffers are created on the render thread in ProcessReady.
internal sealed class ModelCache : IDisposable
{
    private enum LoadState
    {
        Loading,
        DataReady,
        Ready,
        Failed,
    }

    private sealed class ModelData
    {
        public List<MwmMeshData> Lods;
        public List<float> Distances; // LOD1..N thresholds
    }

    private sealed class Entry
    {
        public string Model;
        public volatile LoadState State;
        public ModelData Data;
        public GpuModel Gpu;
    }

    private readonly GpuContext context;
    private readonly TextureCache textures;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GpuModel> aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MwmPartData> materialParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Entry> decoded = new();
    private int version;

    // Incremented whenever a model becomes GPU-ready; ModelPass rebuilds its persistent parts then.
    public int Version => version;

    public ModelCache(GpuContext context, TextureCache textures)
    {
        this.context = context;
        this.textures = textures;
    }

    // Render thread. Starts the background load on first use; returns null until the model is on the GPU.
    public GpuModel Get(string model)
    {
        if (string.IsNullOrEmpty(model))
            return null;

        Entry entry;
        lock (entries)
        {
            // AddRuntimeModel replacement: the actor references the replaced model name.
            if (aliases.TryGetValue(model, out var runtime))
                return runtime;

            if (!entries.TryGetValue(model, out entry))
            {
                entry = new Entry { Model = model };
                entries.Add(model, entry);
                Task.Run(() => Load(entry));
            }
        }

        return entry.Gpu;
    }

    // Render thread. Starts loading a model that will likely be drawn soon (CreateRenderEntity and friends).
    public void StartLoading(string model) => Get(model);

    // Render thread, once per frame before recording: creates GPU buffers for models decoded since the last frame
    public void ProcessReady()
    {
        while (decoded.TryDequeue(out var entry))
        {
            if (entry.State == LoadState.DataReady)
                CreateGpu(entry);
        }
    }

    private void Load(Entry entry)
    {
        try
        {
            var main = MwmLoader.Load(entry.Model);
            var data = new ModelData
            {
                Lods = [main],
                Distances = [],
            };

            if (main.LodModels != null)
            {
                for (var i = 0; i < main.LodModels.Length; i++)
                {
                    // MyModel.LoadFromFile fails the whole model when a LOD cannot be loaded
                    data.Lods.Add(MwmLoader.LoadPath(main.LodModels[i]));
                    data.Distances.Add(main.LodDistances[i]);
                }
            }

            entry.Data = data;
            entry.State = LoadState.DataReady;
            decoded.Enqueue(entry);
        }
        catch (Exception e)
        {
            RenderLog.Warning($"Failed to load model {entry.Model}: {e.Message}");
            entry.State = LoadState.Failed;
        }
    }

    private void CreateGpu(Entry entry)
    {
        var data = entry.Data;
        entry.Data = null;

        var lods = new List<GpuLod>(data.Lods.Count);
        var bounds = BoundingBox.CreateInvalid();
        foreach (var lodData in data.Lods)
        {
            var lod = CreateLod(entry.Model, lodData);
            if (lod == null)
            {
                RenderLog.Warning($"Model {entry.Model} has a LOD without geometry, skipping the model");
                foreach (var created in lods)
                    created.Dispose();
                entry.State = LoadState.Failed;
                return;
            }

            bounds.Include(lod.Bounds);
            lods.Add(lod);
        }

        entry.Gpu = new GpuModel
        {
            Name = entry.Model,
            Lods = lods.ToArray(),
            LodDistances = data.Distances.ToArray(),
            Bounds = bounds,
        };
        entry.State = LoadState.Ready;
        version++;

        var distances = data.Distances.Count > 0 ? ", switch at " + string.Join(", ", data.Distances) : "";
        var transparent = new List<string>();
        foreach (var part in lods[0].Parts)
        {
            if (part.Transparent != null)
                transparent.Add(part.MaterialName + " -> " + part.Transparent.Id.String);
        }

        var skinned = lods[0].Skinned ? $", skinned {lods[0].VertexStride} B/vertex" : "";
        RenderLog.Info($"Model loaded: {entry.Model} ({lods.Count} LODs, LOD0 {lods[0].Parts.Length} parts{distances}{skinned})" +
                       (transparent.Count > 0 ? "; transparent: " + string.Join(", ", transparent) : ""));

    }

    private GpuLod CreateLod(string model, MwmMeshData data)
    {
        if (data.Positions.Length == 0 || data.Indices.Length == 0)
            return null;

        // Runtime models (AddRuntimeModel) reference materials by name only. Stock resolves them
        // through MyMeshMaterials1, which is populated while asset models load; keep the same
        // name -> material/template mapping here.
        foreach (var partSource in data.Parts)
        {
            if (!string.IsNullOrEmpty(partSource.MaterialName))
                materialParts.TryAdd(partSource.MaterialName, partSource);
        }

        var skinned = data.Skinned && data.BoneIndices != null && data.BoneWeights != null &&
                      data.BoneIndices.Length == data.Positions.Length &&
                      data.BoneWeights.Length == data.Positions.Length;
        var vertexCount = data.Positions.Length;
        byte[] vertexBytes;
        var stride = 32;

        if (skinned)
        {
            var vertices = new SkinnedVertex[vertexCount];
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new SkinnedVertex
                {
                    Position = data.Positions[i],
                    BoneWeights = data.BoneWeights[i],
                    Texcoord = data.Texcoords[i],
                    Normal = data.Normals[i],
                    Tangent = data.Tangents[i],
                    BoneIndices = PackBoneIndices(data.BoneIndices[i]),
                };
            }

            vertexBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<SkinnedVertex>(vertices)).ToArray();
            stride = 64;
        }
        else
        {
            var vertices = new ModelVertex[vertexCount];
            for (var i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new ModelVertex
                {
                    Position = data.Positions[i],
                    Texcoord = data.Texcoords[i],
                    Normal = data.Normals[i],
                    Tangent = data.Tangents[i],
                };
            }

            vertexBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(vertices)).ToArray();
        }

        var indexBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(data.Indices)).ToArray();

        var vertexUsage = VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.TransferDst;
        if (skinned)
            vertexUsage |= VkBufferUsageFlags.StorageBuffer; // read by the skinning compute pass
        var vertexBuffer = GpuBuffer.Create(context, (ulong)vertexBytes.Length, vertexUsage, hostVisible: false);
        context.Uploads.UploadBuffer(vertexBuffer, vertexBytes);

        var indexBuffer = GpuBuffer.Create(context, (ulong)indexBytes.Length,
            VkBufferUsageFlags.IndexBuffer | VkBufferUsageFlags.TransferDst, hostVisible: false);
        context.Uploads.UploadBuffer(indexBuffer, indexBytes);

        var parts = new ModelPart[data.Parts.Count];
        for (var i = 0; i < parts.Length; i++)
        {
            var source = data.Parts[i];
            parts[i] = new ModelPart
            {
                Technique = source.Technique,
                MaterialName = source.MaterialName,
                IndexStart = source.IndexStart,
                IndexCount = source.IndexCount,
                ColorMetal = textures.Get(source.ColorMetal, waitTillLoaded: false, TextureCache.TextureKind.ColorMetal),
                NormalGloss = textures.Get(source.NormalGloss, waitTillLoaded: false, TextureCache.TextureKind.NormalGloss),
                Extensions = textures.Get(source.Extensions, waitTillLoaded: false, TextureCache.TextureKind.Extensions),
                Alphamask = textures.Get(source.Alphamask, waitTillLoaded: false, TextureCache.TextureKind.Alphamask),
            };

            if (!source.Technique.IsTransparent())
                continue;

            var transparent = MyTransparentMaterials.GetMaterial(MyStringId.GetOrCompute(source.MaterialName));
            if (transparent == MyTransparentMaterials.ErrorMaterial && !string.IsNullOrEmpty(source.GlassCCW))
                transparent = MyTransparentMaterials.GetMaterial(MyStringId.GetOrCompute(source.GlassCCW));

            if (transparent == MyTransparentMaterials.ErrorMaterial)
                RenderLog.Warning($"Transparent material '{source.MaterialName}' (CCW '{source.GlassCCW}') not found for {model}");

            parts[i].Transparent = transparent;
            parts[i].TransparentColorTexture = textures.Get(NormalizeAssetPath(transparent.Texture),
                waitTillLoaded: false, TextureCache.TextureKind.ColorMetal);
            parts[i].TransparentGlossTexture = textures.Get(NormalizeAssetPath(transparent.GlossTexture),
                waitTillLoaded: false, TextureCache.TextureKind.NormalGloss);
        }

        return new GpuLod
        {
            Vertices = vertexBuffer,
            Indices = indexBuffer,
            Parts = parts,
            Bounds = data.Bounds,
            HasTangents = data.HasTangents,
            Skinned = skinned,
            VertexCount = (uint)vertexCount,
            VertexStride = stride,
        };
    }

    // Render thread: AddRuntimeModel (mods/physics meshes). The mesh may replace an existing model
    // name; stock keeps a remap table, mirrored here as an alias checked by Get().
    public void AddRuntime(MyRenderMessageAddRuntimeModel message)
    {
        if (string.IsNullOrEmpty(message.Name) || message.ModelData.Positions.Count == 0)
            return;

        lock (entries)
        {
            if (entries.ContainsKey(message.Name) || aliases.ContainsKey(message.Name))
                return;
        }

        var source = message.ModelData;
        var count = source.Positions.Count;
        var indices = new uint[source.Indices.Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (uint)Math.Max(0, source.Indices[i]);

        var positions = new Vector4[count];
        var texcoords = new Vector2[count];
        var normals = new uint[count];
        var tangents = new uint[count];
        var hasTangents = source.Tangents.Count == count;
        var up = new Vector3(0, 0, 1);
        var defaultNormal = VF_Packer.PackNormalB4(ref up);
        for (var i = 0; i < count; i++)
        {
            positions[i] = new Vector4(source.Positions[i], 1f);
            texcoords[i] = i < source.TexCoords.Count ? source.TexCoords[i] : Vector2.Zero;

            var normal = i < source.Normals.Count ? source.Normals[i] : up;
            normals[i] = VF_Packer.PackNormalB4(ref normal).PackedValue;
            if (hasTangents)
            {
                // MyVertexFormatTexcoordNormalTangentTexindices packs with w = 1
                var tangentW = new Vector4(source.Tangents[i], 1f);
                tangents[i] = VF_Packer.PackTangentSignB4(ref tangentW).PackedValue;
            }
            else
            {
                tangents[i] = defaultNormal.PackedValue;
            }
        }

        var parts = new List<MwmPartData>(source.Sections.Count);
        foreach (var section in source.Sections)
        {
            var material = ResolveRuntimeMaterial(section.MaterialName);
            parts.Add(new MwmPartData
            {
                Technique = material?.Technique ?? MyMeshDrawTechnique.MESH,
                MaterialName = section.MaterialName ?? "",
                IndexStart = section.IndexStart,
                IndexCount = section.TriCount * 3,
                ColorMetal = material?.ColorMetal,
                NormalGloss = material?.NormalGloss,
                Extensions = material?.Extensions,
                Alphamask = material?.Alphamask,
                GlassCCW = material?.GlassCCW ?? "",
            });
        }

        if (parts.Count == 0)
        {
            parts.Add(new MwmPartData
            {
                Technique = MyMeshDrawTechnique.MESH,
                MaterialName = "",
                IndexStart = 0,
                IndexCount = indices.Length,
            });
        }

        var bounds = source.AABB;
        if (bounds.Min.X > bounds.Max.X)
            bounds = MwmLoader.ComputeBounds(positions);

        var data = new MwmMeshData
        {
            Positions = positions,
            Texcoords = texcoords,
            Normals = normals,
            Tangents = tangents,
            Indices = indices,
            Parts = parts,
            Bounds = bounds,
            HasTangents = hasTangents,
        };

        var lod = CreateLod(message.Name, data);
        if (lod == null)
        {
            RenderLog.Warning($"Runtime model {message.Name} has no usable geometry");
            return;
        }

        var gpu = new GpuModel
        {
            Name = message.Name,
            Lods = [lod],
            LodDistances = [],
            Bounds = bounds,
        };

        lock (entries)
        {
            entries[message.Name] = new Entry
            {
                Model = message.Name,
                State = LoadState.Ready,
                Gpu = gpu,
            };

            if (!string.IsNullOrEmpty(message.ReplacedModel))
                aliases[message.ReplacedModel] = gpu;
        }

        RenderLog.Info($"Runtime model added: {message.Name} ({count} vertices, {indices.Length / 3} triangles, " +
                       $"{parts.Count} sections" +
                       (string.IsNullOrEmpty(message.ReplacedModel) ? "" : $", replaces {message.ReplacedModel}") + ")");
    }

    private MwmPartData ResolveRuntimeMaterial(string name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        if (materialParts.TryGetValue(name, out var material))
            return material;

        // MyPhysicsMesh strips the path before the material name
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && materialParts.TryGetValue(name[(slash + 1)..], out material))
            return material;

        return null;
    }

    private static uint PackBoneIndices(Vector4I indices) =>
        (uint)(indices.X & 0xFF)
        | ((uint)(indices.Y & 0xFF) << 8)
        | ((uint)(indices.Z & 0xFF) << 16)
        | ((uint)(indices.W & 0xFF) << 24);

    private static string NormalizeAssetPath(string path) =>
        string.IsNullOrEmpty(path)
            ? path
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    public void Dispose()
    {
        lock (entries)
        {
            foreach (var entry in entries.Values)
                entry.Gpu?.Dispose();
            entries.Clear();
            aliases.Clear();
            materialParts.Clear();
        }

        decoded.Clear();
    }
}
