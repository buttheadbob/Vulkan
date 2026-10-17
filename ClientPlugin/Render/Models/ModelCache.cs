using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRageMath;
using VRageRender.Import;

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

internal sealed class GpuModel : IDisposable
{
    public string Name;
    public GpuBuffer Vertices;
    public GpuBuffer Indices;
    public ModelPart[] Parts;
    public BoundingBox Bounds;
    public bool HasTangents;
    public bool Skinned;

    public void Dispose()
    {
        Vertices?.Dispose();
        Indices?.Dispose();
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
}

// Model path -> parsed MWM -> GPU vertex/index buffers. Parsing runs on worker threads; GPU buffers are
// created on the render thread in ProcessReady. Only LOD0 is loaded so far.
internal sealed class ModelCache : IDisposable
{
    private enum LoadState
    {
        Loading,
        DataReady,
        Ready,
        Failed,
    }

    private sealed class Entry
    {
        public string Model;
        public volatile LoadState State;
        public MwmMeshData Data;
        public GpuModel Gpu;
    }

    private readonly GpuContext context;
    private readonly TextureCache textures;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Entry> decoded = new();

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
            entry.Data = MwmLoader.Load(entry.Model);
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

        if (data.Positions.Length == 0 || data.Indices.Length == 0)
        {
            RenderLog.Warning($"Model {entry.Model} has no geometry");
            entry.State = LoadState.Failed;
            return;
        }

        var vertices = new ModelVertex[data.Positions.Length];
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

        var vertexBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(vertices)).ToArray();
        var indexBytes = MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(data.Indices)).ToArray();

        var vertexBuffer = GpuBuffer.Create(context, (ulong)vertexBytes.Length,
            VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.TransferDst, hostVisible: false);
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
        }

        entry.Gpu = new GpuModel
        {
            Name = entry.Model,
            Vertices = vertexBuffer,
            Indices = indexBuffer,
            Parts = parts,
            Bounds = data.Bounds,
            HasTangents = data.HasTangents,
            Skinned = data.Skinned,
        };
        entry.State = LoadState.Ready;

        RenderLog.Info($"Model loaded: {entry.Model} ({vertices.Length} vertices, {data.Indices.Length} indices, {parts.Length} parts)");
    }

    public void Dispose()
    {
        lock (entries)
        {
            foreach (var entry in entries.Values)
                entry.Gpu?.Dispose();
            entries.Clear();
        }

        decoded.Clear();
    }
}
