using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using ClientPlugin.Render.Shaders;
using ClientPlugin.Render.Textures;
using ClientPlugin.Render.Vulkan;
using Vortice.Vulkan;
using VRage;
using VRage.Library;
using VRageMath;
using VRageRender;
using VRageRender.Messages;
using VRageRender.Voxels;

namespace ClientPlugin.Render.Scene;

// Basic voxel/planet renderer. The game's clipmap owns the LOD and meshing work; this pass only
// receives finished cell data, uploads it in a compact 24-byte vertex format, and draws it with a
// flat material palette. It deliberately skips stock triplanar textures: the goal is stable
// planets/asteroids for testing and environment-probe colour, not final voxel material fidelity.
internal sealed unsafe class VoxelPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VoxelVertex
    {
        public Vector3 Position;
        public uint Material;
        public uint PackedNormal;
        public uint ColorShift;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VoxelMaterialGpu
    {
        public uint ColorMetalXZnY;
        public uint ColorMetalY;
        public uint NormalGlossXZnY;
        public uint NormalGlossY;
        public uint ExtensionsXZnY;
        public uint ExtensionsY;
        public float Tiling;
        public uint Flags;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public ulong Frame;
        public ulong Materials;
        public Vector4 World0;
        public Vector4 World1;
        public Vector4 World2;
        public Vector4 World3;
        public Vector4 Color;
        public uint MaterialIndex;
        public Vector3 VoxelOffset;
        public float VoxelScale;
        public uint Pad0;
    }

    private sealed class VoxelPart
    {
        public GpuBuffer Vertices;
        public GpuBuffer Indices;
        public uint StartIndex;
        public uint IndexCount;
        public uint MaterialIndex;
    }

    private sealed class VoxelCell : IMyVoxelActorCell
    {
        private readonly VoxelPass pass;
        public readonly VoxelActor Actor;
        private readonly List<VoxelPart> parts = new();
        private GpuBuffer vertices;
        private GpuBuffer indices;
        private uint indexCount;
        private ulong allocatedBytes;
        private bool visible = true;
        private bool disposed;

        public Vector3D Offset { get; }
        public int Lod { get; }
        public BoundingBox Bounds { get; private set; } = BoundingBox.CreateInvalid();
        public bool Visible => visible;
        public IReadOnlyList<VoxelPart> Parts => parts;

        public VoxelCell(VoxelPass pass, VoxelActor actor, Vector3D offset, int lod)
        {
            this.pass = pass;
            Actor = actor;
            Offset = offset;
            Lod = lod;
        }

        public void UpdateMesh(ref MyVoxelRenderCellData data, ref IMyVoxelUpdateBatch updateBatch)
        {
            if (disposed)
                return;
            Bounds = data.CellBounds;
            if (!pass.Enabled)
            {
                // Safe mode: keep advancing the clipmap (so world loading completes) but release any
                // GPU buffers and do not upload new terrain.
                vertices?.Dispose();
                indices?.Dispose();
                parts.Clear();
                pass.RemoveBytes(allocatedBytes);
                allocatedBytes = 0;
                return;
            }

            if (data.Vertices == null || data.Normals == null || data.Indices == null)
                return;
            var vertexCount = data.VertexCount > 0 ? data.VertexCount : data.Vertices.Size / 16;
            var indexCountLocal = data.IndexCount > 0
                ? data.IndexCount
                : data.Indices.Size / (data.ShortIndices ? 2 : 4);
            if (vertexCount <= 0 || indexCountLocal <= 0)
                return;
            vertexCount = Math.Min(vertexCount, data.Vertices.Size / 16);
            vertexCount = Math.Min(vertexCount, data.Normals.Size / 8);
            indexCountLocal = Math.Min(indexCountLocal,
                data.Indices.Size / (data.ShortIndices ? 2 : 4));
            if (vertexCount <= 0 || indexCountLocal <= 0)
                return;
            // Guard against pathological cells (malformed stitch data or a very high LOD) that
            // would create a multi-million-vertex draw and stall the GPU.
            if (vertexCount > 2_000_000 || indexCountLocal > 6_000_000)
            {
                pass.ReportOversizedCell(vertexCount, indexCountLocal);
                return;
            }

            // Reject malformed cell data before it can become an out-of-bounds GPU vertex fetch.
            // Robust buffer access is not guaranteed on every driver; a bad index can hang the GPU.
            var maxIndex = 0u;
            if (data.ShortIndices)
            {
                var src = (ushort*)data.Indices.Ptr;
                for (var i = 0; i < indexCountLocal; i++)
                    maxIndex = Math.Max(maxIndex, src[i]);
            }
            else
            {
                var src = (uint*)data.Indices.Ptr;
                for (var i = 0; i < indexCountLocal; i++)
                    maxIndex = Math.Max(maxIndex, src[i]);
            }
            if (maxIndex >= (uint)vertexCount)
            {
                pass.ReportInvalidCell(maxIndex, vertexCount);
                return;
            }

            var vertexBytes = (ulong)Math.Max(1, vertexCount) * (ulong)sizeof(VoxelVertex);
            var indexBytes = (ulong)Math.Max(1, indexCountLocal) * sizeof(uint);
            var newBytes = vertexBytes + indexBytes;
            var projected = pass.allocatedBytes + newBytes - allocatedBytes;
            if (projected > VoxelByteBudget)
            {
                pass.ReportBudgetExceeded(projected);
                return;
            }

            var newVertices = GpuBuffer.Create(pass.context, vertexBytes,
                VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.TransferDst, hostVisible: false);
            var newIndices = GpuBuffer.Create(pass.context, indexBytes,
                VkBufferUsageFlags.IndexBuffer | VkBufferUsageFlags.TransferDst, hostVisible: false);

            // Build one interleaved vertex stream and a native u32 index stream, then upload them
            // through the normal transfer path. Device-local buffers keep the GPU from reading
            // per-cell geometry out of host-visible/GTT memory, which is both slow and was causing
            // device faults on RADV.
            var vertexBytesArray = new byte[(int)vertexBytes];
            var srcPositions = (byte*)data.Vertices.Ptr;
            var srcNormals = (byte*)data.Normals.Ptr;
            fixed (byte* dstVertices = vertexBytesArray)
            {
                for (var i = 0; i < vertexCount; i++)
                {
                    Buffer.MemoryCopy(srcPositions + i * 16L, dstVertices + i * sizeof(VoxelVertex),
                        16, 16);
                    Buffer.MemoryCopy(srcNormals + i * 8L,
                        dstVertices + i * sizeof(VoxelVertex) + 16, 8, 8);
                }
            }

            var indexBytesArray = new byte[(int)indexBytes];
            fixed (byte* dstIndicesBytes = indexBytesArray)
            {
                var dstIndices = (uint*)dstIndicesBytes;
                if (data.ShortIndices)
                {
                    var src = (ushort*)data.Indices.Ptr;
                    for (var i = 0; i < indexCountLocal; i++)
                        dstIndices[i] = src[i];
                }
                else
                {
                    Buffer.MemoryCopy(data.Indices.Ptr.ToPointer(), dstIndices,
                        (long)indexCountLocal * sizeof(uint), (long)indexCountLocal * sizeof(uint));
                }
            }
            pass.context.Uploads.UploadBuffer(newVertices, vertexBytesArray);
            pass.context.Uploads.UploadBuffer(newIndices, indexBytesArray);

            vertices?.Dispose();
            indices?.Dispose();
            parts.Clear();
            pass.RemoveBytes(allocatedBytes);

            vertices = newVertices;
            indices = newIndices;
            indexCount = (uint)indexCountLocal;
            allocatedBytes = newBytes;
            pass.AddBytes(newBytes);
            Bounds = data.CellBounds;

            if (data.Parts is { Length: > 0 })
            {
                foreach (var p in data.Parts)
                {
                    if (p.IndexCount <= 0 || p.StartIndex < 0)
                        continue;
                    var start = (uint)p.StartIndex;
                    var count = (uint)p.IndexCount;
                    if (start >= indexCountLocal)
                        continue;
                    var remaining = (uint)indexCountLocal - start;
                    if (count > remaining)
                        count = remaining;
                    if (count == 0)
                        continue;
                    parts.Add(new VoxelPart
                    {
                        Vertices = newVertices,
                        Indices = newIndices,
                        StartIndex = start,
                        IndexCount = count,
                        MaterialIndex = p.Materials.I0 == byte.MaxValue ? 0u : p.Materials.I0,
                    });
                }
            }
            if (parts.Count == 0)
            {
                parts.Add(new VoxelPart
                {
                    Vertices = newVertices,
                    Indices = newIndices,
                    StartIndex = 0,
                    IndexCount = (uint)indexCountLocal,
                    MaterialIndex = 0,
                });
            }
        }

        public void SetVisible(bool visible, bool notify = true)
        {
            this.visible = visible;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            vertices?.Dispose();
            indices?.Dispose();
            parts.Clear();
            pass.RemoveBytes(allocatedBytes);
            allocatedBytes = 0;
        }
    }

    private sealed class VoxelActor : IMyVoxelActor
    {
        private readonly VoxelPass pass;
        private readonly List<VoxelCell> cells = new();
        public IMyLodController Clipmap { get; }
        public MatrixD WorldMatrix { get; private set; }
        public bool Visible { get; set; } = true;

        public uint Id { get; }
        public Vector3I Size { get; set; }
        public MyVoxelActorTransitionMode TransitionMode { get; set; } = MyVoxelActorTransitionMode.Immediate;
        public bool IsBatching { get; private set; }

        public event VisibilityChange CellChange;
        public event ActionRef<MatrixD> Move;

        public IEnumerable<VoxelCell> Cells => cells;

        public VoxelActor(VoxelPass pass, MyRenderMessageVoxelCreate message)
        {
            this.pass = pass;
            Clipmap = message.Clipmap;
            Id = message.Id;
            Size = message.Size;
            WorldMatrix = message.WorldMatrix;
        }

        public IMyVoxelActorCell CreateCell(Vector3D offset, int lod, bool notify = false)
        {
            var cell = new VoxelCell(pass, this, offset, lod);
            cells.Add(cell);
            return cell;
        }

        public void DeleteCell(IMyVoxelActorCell cell, bool notify = false)
        {
            if (cell is VoxelCell voxelCell)
            {
                cells.Remove(voxelCell);
                voxelCell.Dispose();
            }
        }

        public void BeginBatch(MyVoxelActorTransitionMode? switchMode = null)
        {
            IsBatching = true;
        }

        public void EndBatch(bool justLoaded)
        {
            IsBatching = false;
        }

        public void SetWorldMatrix(MatrixD matrix)
        {
            WorldMatrix = matrix;
            Move?.Invoke(ref matrix);
        }

        public void NotifyCellChange(Vector3D offset, int lod, bool visible) =>
            CellChange?.Invoke(offset, lod, visible);

        public void Dispose()
        {
            try
            {
                Clipmap?.Unload();
            }
            catch (Exception e)
            {
                RenderLog.Warning($"Voxel clipmap unload failed: {e.Message}");
            }
            foreach (var cell in cells)
                cell.Dispose();
            cells.Clear();
        }
    }

    private const ulong VoxelByteBudget = 256UL * 1024 * 1024;
    private static readonly string UnstableMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpaceEngineers", "Storage", "Vulkan.voxels-unstable");

    private static readonly VkVertexInputBindingDescription[] VertexBindings =
    [
        new() { binding = 0, stride = (uint)sizeof(VoxelVertex), inputRate = VkVertexInputRate.Vertex },
    ];

    private static readonly VkVertexInputAttributeDescription[] VertexAttributes =
    [
        new() { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 },
        new() { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 },
        new() { location = 2, binding = 0, format = VkFormat.R32Uint, offset = 16 },
        new() { location = 3, binding = 0, format = VkFormat.R32Uint, offset = 20 },
    ];

    private readonly GpuContext context;
    private readonly TextureCache textures;
    private readonly VkPipeline pipeline;
    private readonly VkPipeline probePipeline;
    private readonly Dictionary<uint, VoxelActor> actors = new();
    private readonly Vector4[] palette = new Vector4[256];
    private readonly GpuBuffer materialBuffer;
    private readonly VoxelMaterialGpu[] materialData = new VoxelMaterialGpu[256];
    private MyRenderVoxelMaterialData[] cachedMaterials;
    private long materialGeneration = -1;
    private readonly bool probeVoxels = Environment.GetEnvironmentVariable("VULKAN_VOXEL_PROBE") == "1";
    private readonly bool disabledByEnvironment = Environment.GetEnvironmentVariable("VULKAN_NO_VOXEL") == "1";
    private readonly bool drawDisabledByEnvironment =
        Environment.GetEnvironmentVariable("VULKAN_VOXEL_NO_DRAW") == "1";
    private bool loggedMaterials;
    private bool loggedBudget;
    private bool loggedInvalidCell;
    private bool loggedOversizedCell;
    private bool loggedInvalidBuffer;
    private ulong allocatedBytes;
    private long lastStatsTimestamp;
    private int lastCellCount;
    private int lastDrawCount;
    private bool markerWritten;
    private bool markerCleared;
    private long firstDrawTimestamp;

    public int ActorCount => actors.Count;
    public int DrawnCellCount { get; private set; }
    private bool Enabled => !disabledByEnvironment;

    public VoxelPass(GpuContext context, TextureCache textures, VkSampleCountFlags samples)
    {
        this.context = context;
        this.textures = textures;
        materialBuffer = GpuBuffer.Create(context, (ulong)(materialData.Length * sizeof(VoxelMaterialGpu)),
            VkBufferUsageFlags.StorageBuffer, hostVisible: true);
        // Crash-loop guard: a marker is written when voxel drawing starts and removed after 30 s of
        // stable rendering. If it is still present on the next launch, the previous session died
        // while drawing voxels, so start this session with voxels off instead of risking another
        // GPU reset. Re-enable them in Graphics > Vulkan Renderer when ready to test again.
        try
        {
            if (File.Exists(UnstableMarker))
            {
                File.Delete(UnstableMarker);
                RenderLog.Warning("Previous voxel session ended unexpectedly; the crash marker was " +
                                  "cleared. Voxels remain enabled as required.");
            }
        }
        catch (Exception e)
        {
            RenderLog.Warning($"Could not check voxel crash marker: {e.Message}");
        }
        for (var i = 0; i < palette.Length; i++)
            palette[i] = FallbackColor(i);

        pipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Voxel.Code.ToArray(),
            VertexEntry = ShaderBytecode.Voxel.VertexMain,
            FragmentEntry = ShaderBytecode.Voxel.FragmentScene,
            ColorFormats = [SceneTargets.ColorFormat, SceneTargets.GBufferFormat, SceneTargets.GBufferFormat],
            DepthFormat = SceneTargets.DepthFormat,
            VertexBindings = VertexBindings,
            VertexAttributes = VertexAttributes,
            Samples = samples,
            DepthTest = true,
            DepthWrite = true,
            CullMode = VkCullModeFlags.Back,
            FrontFace = VkFrontFace.Clockwise,
        }.Build(context, "voxel");

        probePipeline = new GraphicsPipelineBuilder
        {
            Code = ShaderBytecode.Voxel.Code.ToArray(),
            VertexEntry = ShaderBytecode.Voxel.VertexMain,
            FragmentEntry = ShaderBytecode.Voxel.FragmentScene,
            ColorFormats = [SceneTargets.ColorFormat, SceneTargets.GBufferFormat, SceneTargets.GBufferFormat],
            DepthFormat = SceneTargets.DepthFormat,
            VertexBindings = VertexBindings,
            VertexAttributes = VertexAttributes,
            Samples = VkSampleCountFlags.Count1,
            DepthTest = true,
            DepthWrite = true,
            CullMode = VkCullModeFlags.Back,
            FrontFace = VkFrontFace.Clockwise,
        }.Build(context, "voxel-probe");
    }

    private void AddBytes(ulong bytes) => allocatedBytes += bytes;
    private void RemoveBytes(ulong bytes) =>
        allocatedBytes = bytes >= allocatedBytes ? 0 : allocatedBytes - bytes;

    private void ReportInvalidCell(uint maxIndex, int vertexCount)
    {
        if (loggedInvalidCell)
            return;
        loggedInvalidCell = true;
        RenderLog.Warning($"Voxel cell rejected: index {maxIndex} >= vertex count {vertexCount}");
    }

    private void ReportInvalidBuffer()
    {
        if (loggedInvalidBuffer)
            return;
        loggedInvalidBuffer = true;
        RenderLog.Warning("Voxel draw skipped: a cell has a null vertex/index buffer address");
    }

    private void ReportOversizedCell(int vertexCount, int indexCount)
    {
        if (loggedOversizedCell)
            return;
        loggedOversizedCell = true;
        RenderLog.Warning($"Voxel cell rejected as oversized: {vertexCount} vertices, {indexCount} indices");
    }

    private void ReportBudgetExceeded(ulong projected)
    {
        if (loggedBudget)
            return;
        loggedBudget = true;
        RenderLog.Warning($"Voxel GPU budget reached: {projected / (1024 * 1024)} MB of " +
                          $"{VoxelByteBudget / (1024 * 1024)} MB; further cell uploads are skipped");
    }

    public void SetMaterials(MyRenderVoxelMaterialData[] materials)
    {
        if (materials == null)
            return;
        cachedMaterials = materials;
        if (!loggedMaterials)
        {
            loggedMaterials = true;
            RenderLog.Info($"Voxel materials: {materials.Length}");
        }
        RefreshMaterials(force: true);
    }

    private void RefreshMaterials(bool force)
    {
        if (cachedMaterials == null)
            return;
        if (!force && materialGeneration == textures.ReadyGeneration)
            return;
        materialGeneration = textures.ReadyGeneration;

        var white = (uint)BindlessDescriptors.TextureSlot.White;
        for (var i = 0; i < materialData.Length; i++)
        {
            materialData[i] = new VoxelMaterialGpu
            {
                ColorMetalXZnY = white,
                ColorMetalY = white,
                NormalGlossXZnY = white,
                NormalGlossY = white,
                ExtensionsXZnY = white,
                ExtensionsY = white,
                Tiling = 1f,
                Color = Vector4.One,
            };
        }

        var count = Math.Min(cachedMaterials.Length, materialData.Length);
        for (var i = 0; i < count; i++)
        {
            var material = cachedMaterials[i];
            var set = material.TextureSets is { Length: > 0 }
                ? material.TextureSets[0]
                : default;
            var cmXZ = TextureSlot(set.ColorMetalXZnY, TextureCache.TextureKind.ColorMetal, white);
            var cmY = TextureSlot(set.ColorMetalY, TextureCache.TextureKind.ColorMetal, cmXZ);
            var ngXZ = TextureSlot(set.NormalGlossXZnY, TextureCache.TextureKind.NormalGloss, white);
            var ngY = TextureSlot(set.NormalGlossY, TextureCache.TextureKind.NormalGloss, ngXZ);
            var extXZ = TextureSlot(set.ExtXZnY, TextureCache.TextureKind.Extensions, white);
            var extY = TextureSlot(set.ExtY, TextureCache.TextureKind.Extensions, extXZ);
            var tiling = material.StandardTilingSetup.TilingScale;
            if (tiling <= 0f)
                tiling = material.SimpleTilingSetup.TilingScale;
            if (tiling <= 0f)
                tiling = 1f;
            var color = material.Far3Color;
            var tint = color.LengthSquared() > 0.0001f
                ? new Vector4(color.X, color.Y, color.Z, 1f)
                : Vector4.One;
            var flags = 0u;
            if (cmXZ != white) flags |= 1u;
            if (ngXZ != white) flags |= 2u;
            if (extXZ != white) flags |= 4u;
            materialData[i] = new VoxelMaterialGpu
            {
                ColorMetalXZnY = cmXZ,
                ColorMetalY = cmY,
                NormalGlossXZnY = ngXZ,
                NormalGlossY = ngY,
                ExtensionsXZnY = extXZ,
                ExtensionsY = extY,
                Tiling = 1f / Math.Max(tiling, 0.001f),
                Flags = flags,
                Color = tint,
            };
            palette[i] = tint;
        }

        fixed (VoxelMaterialGpu* source = materialData)
        {
            Buffer.MemoryCopy(source, materialBuffer.Mapped,
                materialData.Length * sizeof(VoxelMaterialGpu),
                materialData.Length * sizeof(VoxelMaterialGpu));
        }
    }

    private uint TextureSlot(string path, TextureCache.TextureKind kind, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
            return fallback;
        var normalized = path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        var entry = textures.Get(normalized, waitTillLoaded: false, kind);
        return entry is { Status: TextureCache.State.Ready }
            ? TextureCache.SlotOf(entry)
            : fallback;
    }

    public void Create(MyRenderMessageVoxelCreate message)
    {
        if (message.Clipmap == null)
        {
            RenderLog.Warning($"Voxel {message.Id} has no clipmap; skipping");
            return;
        }

        var actor = new VoxelActor(this, message);
        actors[message.Id] = actor;
        try
        {
            // The stock provider owns the temporary native vertex/index arrays. The clipmap fills
            // them and calls our cell's UpdateMesh, where they are copied to GPU buffers.
            var provider = CreateProcessorProvider();
            if (provider == null)
            {
                actors.Remove(message.Id);
                RenderLog.Error($"Voxel {message.Id}: stock render-data provider not found");
                return;
            }
            message.Clipmap.VoxelRenderDataProcessorProvider = provider;
            message.Clipmap.BindToActor(actor);
            actor.SetWorldMatrix(message.WorldMatrix);
            message.Clipmap.DoVisibilityUpdates = true;
        }
        catch (Exception e)
        {
            actors.Remove(message.Id);
            RenderLog.Error($"Failed to bind voxel clipmap {message.Id}", e);
        }
    }

    public void Update(CameraState camera)
    {
        RefreshMaterials(force: false);
        DrawnCellCount = 0;
        if (actors.Count == 0 || !camera.HasValue)
            return;

        var view = MatrixD.Invert(camera.ViewD);
        var frustum = new BoundingFrustumD(camera.WorldFrustumMatrix);
        foreach (var actor in actors.Values)
        {
            if (actor.Clipmap == null)
                continue;
            try
            {
                actor.Clipmap.DoVisibilityUpdates = true;
                actor.Clipmap.Update(ref view, frustum, camera.Far > 0 ? camera.Far : 15000f, true);
                if (actor.Visible)
                    MyRenderProxy.VisibleObjectsWrite.Add(actor.Id);
            }
            catch (Exception e)
            {
                RenderLog.Warning($"Voxel clipmap update failed for {actor.Id}: {e.Message}");
            }
        }
    }

    // Debug-camera helper: frame the nearest voxel/asteroid/planet instead of an MWM actor.
    public bool TryGetNearestActor(Vector3D position, out Vector3D center, out float radius)
    {
        center = Vector3D.Zero;
        radius = 1f;
        var best = double.MaxValue;
        foreach (var actor in actors.Values)
        {
            var candidate = actor.WorldMatrix.Translation;
            var distance = (candidate - position).LengthSquared();
            if (distance >= best)
                continue;
            best = distance;
            center = candidate;
            radius = Math.Max(actor.Size.X, Math.Max(actor.Size.Y, actor.Size.Z)) * 0.5f;
        }
        return best < double.MaxValue;
    }

    public void UpdateObject(MyRenderMessageUpdateRenderObject message)
    {
        if (actors.TryGetValue(message.ID, out var actor) && message.Data.HasWorldMatrix)
            actor.SetWorldMatrix(message.Data.WorldMatrix);
    }

    public void SetVisible(uint id, bool visible)
    {
        if (actors.TryGetValue(id, out var actor))
            actor.Visible = visible;
    }

    public void InvalidateRange(uint id, Vector3I min, Vector3I max)
    {
        if (actors.TryGetValue(id, out var actor))
            actor.Clipmap?.InvalidateRange(min, max);
    }

    public void Remove(uint id)
    {
        if (!actors.Remove(id, out var actor))
            return;
        actor.Dispose();
    }

    public void Record(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        ulong frameConstants)
    {
        RecordInternal(commands, targets, camera, pipeline, frameConstants, 20000f);
    }

    public void RecordProbe(VkCommandBuffer commands, SceneTargets targets, CameraState probeCamera,
        ulong frameConstants)
    {
        if (!probeVoxels)
            return;
        // Keep the probe to the nearby ground/asteroid colour. Planets are huge and drawing every
        // clipmap cell into six cube faces was the main cause of the loading GPU stall.
        RecordInternal(commands, targets, probeCamera, probePipeline, frameConstants, 3000f);
    }

    private void RecordInternal(VkCommandBuffer commands, SceneTargets targets, CameraState camera,
        VkPipeline boundPipeline, ulong frameConstants, float maxDistance)
    {
        if (actors.Count == 0 || !Enabled || drawDisabledByEnvironment)
            return;

        if (!markerWritten)
        {
            markerWritten = true;
            firstDrawTimestamp = Environment.TickCount64;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UnstableMarker)!);
                File.WriteAllText(UnstableMarker, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception e)
            {
                RenderLog.Warning($"Could not write voxel crash marker: {e.Message}");
            }
        }
        else if (!markerCleared && Environment.TickCount64 - firstDrawTimestamp > 30000)
        {
            markerCleared = true;
            try { File.Delete(UnstableMarker); }
            catch (Exception e) { RenderLog.Warning($"Could not clear voxel marker: {e.Message}"); }
        }

        var api = context.Device.Api;
        var viewport = new VkViewport(0, 0, targets.Extent.width, targets.Extent.height, 0, 1);
        var scissor = new VkRect2D(0, 0, targets.Extent.width, targets.Extent.height);
        api.vkCmdSetViewport(commands, 0, 1, &viewport);
        api.vkCmdSetScissor(commands, 0, 1, &scissor);
        api.vkCmdBindPipeline(commands, VkPipelineBindPoint.Graphics, boundPipeline);
        context.Bindless.Bind(commands, VkPipelineBindPoint.Graphics);

        var cameraPosition = camera.Position;
        var frustum = new BoundingFrustum(camera.CullingViewProjection);
        var maxDistanceSq = maxDistance * maxDistance;
        var drawCount = 0;
        foreach (var actor in actors.Values)
        {
            if (!actor.Visible)
                continue;
            var actorMatrix = actor.WorldMatrix;
            foreach (var cell in actor.Cells)
            {
                if (!cell.Visible || cell.Parts.Count == 0)
                    continue;
                var lodScale = 1 << cell.Lod;
                var cellWorld = actorMatrix;
                cellWorld.Translation = actorMatrix.Translation +
                                        Vector3D.TransformNormal(cell.Offset, actorMatrix);
                // The mesher emits coordinates in Lod0 voxels; scale the cell basis by its LOD.
                cellWorld.M11 *= lodScale; cellWorld.M12 *= lodScale; cellWorld.M13 *= lodScale;
                cellWorld.M21 *= lodScale; cellWorld.M22 *= lodScale; cellWorld.M23 *= lodScale;
                cellWorld.M31 *= lodScale; cellWorld.M32 *= lodScale; cellWorld.M33 *= lodScale;
                cellWorld.Translation -= cameraPosition;

                var boundsValid = cell.Bounds.Min.X <= cell.Bounds.Max.X &&
                                  cell.Bounds.Min.Y <= cell.Bounds.Max.Y &&
                                  cell.Bounds.Min.Z <= cell.Bounds.Max.Z;
                if (boundsValid)
                {
                    var worldBounds = cell.Bounds.Transform((Matrix)cellWorld);
                    if (worldBounds.Center.LengthSquared() > maxDistanceSq &&
                        worldBounds.Min.LengthSquared() > maxDistanceSq)
                        continue;
                    if (!frustum.Intersects(worldBounds))
                        continue;
                }

                var first = cell.Parts[0];
                if (first.Vertices == null || first.Indices == null ||
                    first.Vertices.DeviceAddress == 0 || first.Indices.Handle == VkBuffer.Null ||
                    first.Indices.DeviceAddress == 0)
                {
                    ReportInvalidBuffer();
                    continue;
                }
                var world = Matrix.Transpose((Matrix)cellWorld);
                var vertexBuffer = first.Vertices.Handle;
                ulong vertexOffset = 0;
                api.vkCmdBindVertexBuffers(commands, 0, 1, &vertexBuffer, &vertexOffset);
                api.vkCmdBindIndexBuffer(commands, first.Indices.Handle, 0, VkIndexType.Uint32);
                foreach (var part in cell.Parts)
                {
                    if (part.IndexCount == 0)
                        continue;
                    var push = new PushConstants
                    {
                        Frame = frameConstants,
                        Materials = materialBuffer.DeviceAddress,
                        World0 = new Vector4(world.M11, world.M12, world.M13, world.M14),
                        World1 = new Vector4(world.M21, world.M22, world.M23, world.M24),
                        World2 = new Vector4(world.M31, world.M32, world.M33, world.M34),
                        World3 = new Vector4(world.M41, world.M42, world.M43, world.M44),
                        Color = palette[(int)Math.Min(part.MaterialIndex, 255u)],
                        MaterialIndex = part.MaterialIndex,
                        VoxelOffset = new Vector3((float)cell.Offset.X, (float)cell.Offset.Y,
                            (float)cell.Offset.Z),
                        VoxelScale = (float)lodScale,
                    };
                    api.vkCmdPushConstants(commands, context.Bindless.PipelineLayout,
                        VkShaderStageFlags.All, 0, (uint)sizeof(PushConstants), &push);
                    api.vkCmdDrawIndexed(commands, part.IndexCount, 1, part.StartIndex, 0, 0);
                    DrawnCellCount++;
                    if (++drawCount >= 8192)
                    {
                        LogStats(drawCount);
                        return;
                    }
                }
            }
        }
        LogStats(drawCount);
    }

    private void LogStats(int drawCount)
    {
        var now = Environment.TickCount64;
        if (now - lastStatsTimestamp < 5000)
            return;
        lastStatsTimestamp = now;
        lastCellCount = 0;
        foreach (var actor in actors.Values)
            lastCellCount += actor.Cells.Count();
        lastDrawCount = drawCount;
        RenderLog.Info($"Voxels: {actors.Count} actors, {lastCellCount} cells, {lastDrawCount} draws, " +
                       $"{allocatedBytes / (1024 * 1024)} MB GPU buffers");
    }

    private static IMyVoxelRenderDataProcessorProvider CreateProcessorProvider()
    {
        // VRage.Render11 is not publicized for plugin compilation. The type is internal only for
        // assembly-boundary reasons; its constructor and the interface are safe to use at runtime.
        var type = Type.GetType(
            "VRageRender.MyMeshes+MyVoxelRenderDataProcessorProvider, VRage.Render11");
        if (type == null)
            return null;
        try
        {
            return Activator.CreateInstance(type) as IMyVoxelRenderDataProcessorProvider;
        }
        catch (Exception e)
        {
            RenderLog.Error("Could not create the stock voxel render-data provider", e);
            return null;
        }
    }

    private static Vector4 FallbackColor(int index)
    {
        // Neutral rock/regolith for materials whose Far3Color is not populated (common for
        // asteroids). Snow/grass/planet materials carry their own Far3Color and are not replaced.
        var variation = ((index * 37) & 15) / 15f;
        var shade = 0.20f + variation * 0.06f;
        return new Vector4(shade, shade * 0.96f, shade * 0.88f, 1f);
    }

    public void Dispose()
    {
        foreach (var actor in actors.Values)
            actor.Dispose();
        actors.Clear();
        var api = context.Device.Api;
        var main = pipeline;
        var probe = probePipeline;
        context.Release(() => api.vkDestroyPipeline(main, null));
        context.Release(() => api.vkDestroyPipeline(probe, null));
    }
}
