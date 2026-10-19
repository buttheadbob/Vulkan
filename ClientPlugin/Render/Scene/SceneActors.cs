using System;
using System.Collections.Generic;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Import;
using VRageRender.Messages;

namespace ClientPlugin.Render.Scene;

internal sealed class MaterialOverride
{
    public Vector3? ColorMul;
    public float? Emissivity;
    public RenderFlagsChange? FlagsChange;
}

// A render object created by the game (CreateRenderEntity, CreateRenderCharacter, CreateStaticGroup).
internal sealed class SceneActor
{
    public uint Id;
    public string Model;
    public RenderFlags Flags;
    public MyMeshDrawTechnique Technique;
    public MatrixD WorldMatrix = MatrixD.Identity;
    public float Scale = 1f;
    public bool Visible = true;

    // RenderFlags.SkipInMainView is derived from Flags; kept separately for the visibility message
    public bool Near;

    public Vector3 ColorMaskHsv;
    public Vector3 ColorMul = Vector3.One;
    public float Emissivity = 1f;
    public int DepthBias;
    public float MaxViewDistance = float.MaxValue;
    public BoundingBox? LocalAabb;
    public int Lod;
    public bool VisibilityUpdates; // SetVisibilityUpdates: report this ID to the game when visible

    public Dictionary<string, MaterialOverride> MaterialOverrides;

    // ChangeMaterialTexture: material slot -> replacement textures (cockpit/LCD screen areas)
    public Dictionary<string, MyTextureChange> TextureChanges;

    // CreateStaticGroup: one model drawn once per local matrix
    public Vector3D StaticTranslation;
    public Matrix[] StaticMatrices;

    // SetInstanceBuffer: draw the model once per instance in the given range
    public uint InstanceBufferId;
    public int InstanceStart;
    public int InstanceCount;

    public bool HasInstances => InstanceCount > 0 && InstanceBufferId != 0;

    // Manual cull object hierarchy: the game updates the root (e.g. a character's) world matrix
    // and expects the children to follow it; the child's own matrix is relative to the parent.
    public uint ParentId;
    public Matrix LocalMatrix = Matrix.Identity;

    // GPU skinning: skeleton description, per-frame absolute bone transforms, and the model-bone palette
    public MySkeletonBoneDescription[] SkeletonBones;
    public int[] SkeletonIndices;
    public Matrix[] SkinTransforms;     // per skeleton bone, uploaded once on skeleton change
    public uint[] SkeletonIndexUInts;   // model bone -> skeleton bone (GPU buffer source)
    public Matrix[] BoneTransforms;     // per skeleton bone, per frame
    public ulong SkinnedVertexAddress;  // this frame's compute output

    // Cached absolute world matrix, invalidated by SceneActors.TransformRevision
    public MatrixD CachedWorldMatrix;
    public long WorldRevision = -1;

    // Per-frame GPU actor record needs a refresh (world, visibility, instance range, local bounds)
    public bool StateDirty;

    // Cached parts/material records need a refresh (colour, emissive, flags, texture changes)
    public bool MaterialDirty;
}

// Actors by render object ID. The game owns lifetime; messages mutate this on the render thread only.
internal sealed class SceneActors
{
    private sealed class PendingActor
    {
        public Vector3? ColorMaskHsv;
        public Vector3? ColorMul;
        public Dictionary<string, MyTextureChange> TextureChanges;
        public uint? ParentId;
        public Matrix? LocalMatrix;
        public MatrixD? WorldMatrix;
        public BoundingBox? LocalAabb;
        public uint? InstanceBufferId;
        public int? InstanceStart;
        public int? InstanceCount;
    }

    private readonly Dictionary<uint, SceneActor> actors = new();
    private readonly Dictionary<uint, PendingActor> pending = new();
    private readonly HashSet<uint> reportedMissingTransforms = new();
    private readonly List<SceneActor> skinned = new();
    private readonly List<SceneActor> dirtyActors = new();
    private readonly List<SceneActor> materialDirtyActors = new();
    private readonly List<SceneActor> visibilityUpdates = new();
    private readonly List<SceneActor> createdActors = new();
    private readonly List<uint> removedActorIds = new();
    private readonly List<SceneActor> recreatedActors = new();
    private readonly Dictionary<uint, List<uint>> children = new();
    private long revision;
    private long fullRevision;
    private long transformRevision;
    private long revCreate, revRemove, revEntity, revProps, revTexture, revSkeleton, revInstance, revModel;

    // Diagnostics: which messages trigger part-cache rebuilds.
    public string RevisionCounters =>
        $"create {revCreate}, remove {revRemove}, entity {revEntity}, props {revProps}, " +
        $"texture {revTexture}, skeleton {revSkeleton}, instance {revInstance}, model {revModel}";

    public int Count => actors.Count;

    // Bumped on any change that affects persistent part/material data or the actor list.
    public long Revision => revision;

    // Actors whose skeleton has arrived and which therefore take the skinned compute/direct path.
    public IReadOnlyList<SceneActor> Skinned => skinned;
    public IReadOnlyList<SceneActor> DirtyActors => dirtyActors;
    public IReadOnlyList<SceneActor> MaterialDirtyActors => materialDirtyActors;
    public IReadOnlyList<SceneActor> VisibilityUpdates => visibilityUpdates;

    // Actors added since the last part-cache pass. ModelPass appends them incrementally.
    public IReadOnlyList<SceneActor> CreatedActors => createdActors;
    public void ClearCreatedActors() => createdActors.Clear();
    public void ClearRemovedActors() => removedActorIds.Clear();
    public void ClearRecreatedActors() => recreatedActors.Clear();

    public IReadOnlyList<uint> RemovedActorIds => removedActorIds;
    public IReadOnlyList<SceneActor> RecreatedActors => recreatedActors;

    public void TrimRemovedActors(int count)
    {
        if (count > 0)
            removedActorIds.RemoveRange(0, Math.Min(count, removedActorIds.Count));
    }

    public void TrimRecreatedActors(int count)
    {
        if (count > 0)
            recreatedActors.RemoveRange(0, Math.Min(count, recreatedActors.Count));
    }

    public void TrimCreatedActors(int count)
    {
        if (count > 0)
            createdActors.RemoveRange(0, Math.Min(count, createdActors.Count));
    }

    public void TrimMaterialDirty(int count)
    {
        if (count <= 0)
            return;

        count = Math.Min(count, materialDirtyActors.Count);
        for (var i = 0; i < count; i++)
            materialDirtyActors[i].MaterialDirty = false;
        materialDirtyActors.RemoveRange(0, count);
    }

    public void TrimDirty(int count)
    {
        if (count <= 0)
            return;

        count = Math.Min(count, dirtyActors.Count);
        for (var i = 0; i < count; i++)
            dirtyActors[i].StateDirty = false;
        dirtyActors.RemoveRange(0, count);
    }

    // Changes that require a full part-cache rebuild rather than an incremental append.
    public long FullRevision => fullRevision;

    private void MarkDirty(SceneActor actor)
    {
        if (actor.StateDirty)
            return;

        actor.StateDirty = true;
        dirtyActors.Add(actor);
    }

    // A parent transform moves its whole subtree; cached world matrices are invalidated globally by
    // transformRevision, but every descendant needs its GPU actor record refreshed.
    private void MarkDirtyTree(SceneActor actor, int depth = 0)
    {
        if (depth > 8)
            return;

        MarkDirty(actor);
        if (!children.TryGetValue(actor.Id, out var childIds))
            return;

        foreach (var childId in childIds)
        {
            if (actors.TryGetValue(childId, out var child))
                MarkDirtyTree(child, depth + 1);
        }
    }

    private void MarkMaterialDirty(SceneActor actor)
    {
        if (actor.MaterialDirty)
            return;

        actor.MaterialDirty = true;
        materialDirtyActors.Add(actor);
    }

    public void ClearDirty()
    {
        foreach (var actor in dirtyActors)
            actor.StateDirty = false;
        dirtyActors.Clear();
    }

    public void ClearMaterialDirty()
    {
        foreach (var actor in materialDirtyActors)
            actor.MaterialDirty = false;
        materialDirtyActors.Clear();
    }

    public int InstancedCount
    {
        get
        {
            var count = 0;
            foreach (var actor in actors.Values)
            {
                if (actor.HasInstances)
                    count++;
            }

            return count;
        }
    }

    public IEnumerable<SceneActor> All => actors.Values;

    public void Create(MyRenderMessageCreateRenderEntity message)
    {
        var actor = new SceneActor
        {
            Id = message.ID,
            Model = message.Model,
            Flags = message.Flags,
            Technique = message.Technique,
            WorldMatrix = message.WorldMatrix,
            Scale = message.Rescale != 0f ? message.Rescale : 1f,
            Visible = (message.Flags & RenderFlags.Visible) != 0,
            Near = (message.Flags & RenderFlags.Near) != 0,
            DepthBias = message.DepthBias,
            MaxViewDistance = message.MaxViewDistance,
        };
        ApplyPending(actor);

        actors[message.ID] = actor;
        createdActors.Add(actor);
        revCreate++;
        revision++;
        transformRevision++;
        MarkDirtyTree(actor);
    }

    public void CreateCharacter(MyRenderMessageCreateRenderCharacter message)
    {
        actors[message.ID] = new SceneActor
        {
            Id = message.ID,
            Model = message.Model,
            Flags = message.Flags,
            Technique = MyMeshDrawTechnique.SKINNED,
            WorldMatrix = message.WorldMatrix,
            Visible = (message.Flags & RenderFlags.Visible) != 0,
            ColorMaskHsv = message.ColorMaskHSV ?? Vector3.Zero,
        };
        ApplyPending(actors[message.ID]);
        createdActors.Add(actors[message.ID]);
        revCreate++;
        revision++;
        transformRevision++;
        MarkDirtyTree(actors[message.ID]);
    }

    public void CreateStaticGroup(MyRenderMessageCreateStaticGroup message)
    {
        actors[message.ID] = new SceneActor
        {
            Id = message.ID,
            Model = message.Model,
            Flags = RenderFlags.Visible,
            Technique = MyMeshDrawTechnique.MESH,
            StaticTranslation = message.Translation,
            StaticMatrices = message.LocalMatrices,
            Visible = true,
        };
        ApplyPending(actors[message.ID]);
        createdActors.Add(actors[message.ID]);
        revCreate++;
        revision++;
        transformRevision++;
        MarkDirtyTree(actors[message.ID]);
    }

    public void CreateManualCullObject(MyRenderMessageCreateManualCullObject message)
    {
        actors[message.ID] = new SceneActor
        {
            Id = message.ID,
            WorldMatrix = message.WorldMatrix,
            Visible = true,
        };
        ApplyPending(actors[message.ID]);
        createdActors.Add(actors[message.ID]);
        transformRevision++;
        MarkDirtyTree(actors[message.ID]);
    }

    public void SetParentCullObject(MyRenderMessageSetParentCullObject message)
    {
        var child = Find(message.ID);
        if (child == null)
        {
            // Parenting can be sent before the object is created (large blueprints arrive as a
            // burst of state changes). Keep it and apply it at creation.
            var state = PendingFor(message.ID);
            state.ParentId = message.CullObjectID;
            state.LocalMatrix = message.ChildToParent;
            return;
        }

        if (child.ParentId != 0 && children.TryGetValue(child.ParentId, out var oldChildren))
            oldChildren.Remove(child.Id);
        child.ParentId = message.CullObjectID;
        if (child.ParentId != 0)
        {
            if (!children.TryGetValue(child.ParentId, out var list))
                children[child.ParentId] = list = new List<uint>();
            if (!list.Contains(child.Id))
                list.Add(child.Id);
        }
        if (message.ChildToParent.HasValue)
            child.LocalMatrix = message.ChildToParent.Value;
        transformRevision++;
        MarkDirtyTree(child);
    }

    public SceneActor Find(uint id) => actors.TryGetValue(id, out var actor) ? actor : null;

    // Color/skin updates can arrive before CreateRenderEntity. Stock keeps them on the render object
    // and applies them when the object appears; dropping them leaves the block uncoloured/unskinned.
    private void ApplyPending(SceneActor actor)
    {
        if (!pending.TryGetValue(actor.Id, out var state))
            return;

        pending.Remove(actor.Id);
        if (state.ColorMaskHsv.HasValue)
            actor.ColorMaskHsv = state.ColorMaskHsv.Value;
        if (state.ColorMul.HasValue)
            actor.ColorMul = state.ColorMul.Value;
        if (state.ParentId.HasValue)
        {
            actor.ParentId = state.ParentId.Value;
            if (actor.ParentId != 0)
            {
                if (!children.TryGetValue(actor.ParentId, out var list))
                    children[actor.ParentId] = list = new List<uint>();
                if (!list.Contains(actor.Id))
                    list.Add(actor.Id);
            }
        }
        if (state.LocalMatrix.HasValue)
            actor.LocalMatrix = state.LocalMatrix.Value;
        if (state.WorldMatrix.HasValue)
            actor.WorldMatrix = state.WorldMatrix.Value;
        if (state.LocalAabb.HasValue)
            actor.LocalAabb = state.LocalAabb.Value;
        if (state.InstanceBufferId.HasValue)
            actor.InstanceBufferId = state.InstanceBufferId.Value;
        if (state.InstanceStart.HasValue)
            actor.InstanceStart = state.InstanceStart.Value;
        if (state.InstanceCount.HasValue)
            actor.InstanceCount = state.InstanceCount.Value;
        if (state.TextureChanges != null)
        {
            actor.TextureChanges ??= new Dictionary<string, MyTextureChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in state.TextureChanges)
                actor.TextureChanges[pair.Key] = pair.Value;
        }
    }

    private PendingActor PendingFor(uint id)
    {
        if (!pending.TryGetValue(id, out var state))
            pending[id] = state = new PendingActor();
        return state;
    }

    // Manual cull objects move; their children (characters, optionally grid cells) are drawn
    // relative to them. Depth-limited against malformed/cyclic hierarchies.
    public MatrixD GetWorldMatrix(SceneActor actor)
    {
        if (actor.WorldRevision == transformRevision)
            return actor.CachedWorldMatrix;

        var world = ResolveWorldMatrix(actor, 0);
        actor.CachedWorldMatrix = world;
        actor.WorldRevision = transformRevision;
        return world;
    }

    private MatrixD ResolveWorldMatrix(SceneActor actor, int depth)
    {
        if (depth >= 4 || actor.ParentId == 0 || !actors.TryGetValue(actor.ParentId, out var parent))
            return actor.WorldMatrix;

        return (MatrixD)actor.LocalMatrix * ResolveWorldMatrix(parent, depth + 1);
    }

    public void Update(MyRenderMessageUpdateRenderObject message)
    {
        var actor = Find(message.ID);
        if (actor == null)
        {
            var state = PendingFor(message.ID);
            if (message.Data.HasWorldMatrix)
                state.WorldMatrix = message.Data.WorldMatrix;
            if (message.Data.HasLocalMatrix)
                state.LocalMatrix = message.Data.LocalMatrix;
            if (message.Data.HasLocalAABB)
                state.LocalAabb = message.Data.LocalAABB;
            return;
        }

        // The relative transform (set by SetParentCullObject / a pending local update) is what
        // places subparts; world updates continue to drive the cull-object root and the character.
        if (message.Data.HasWorldMatrix)
        {
            actor.WorldMatrix = message.Data.WorldMatrix;
            transformRevision++;
            MarkDirtyTree(actor);
        }
        if (message.Data.HasLocalMatrix)
        {
            actor.LocalMatrix = message.Data.LocalMatrix;
            LocalMatrixUpdates++;
            transformRevision++;
            MarkDirtyTree(actor);
        }
        if (message.Data.HasLocalAABB)
        {
            actor.LocalAabb = message.Data.LocalAABB;
            MarkDirty(actor);
        }
    }

    public long LocalMatrixUpdates { get; private set; }

    public void SetVisible(uint id, bool visible, bool near)
    {
        var actor = Find(id);
        if (actor != null)
        {
            actor.Visible = visible;
            actor.Near = near;
            MarkDirty(actor);
            MarkMaterialDirty(actor);
        }
    }

    public void UpdateEntity(MyRenderMessageUpdateRenderEntity message)
    {
        var actor = Find(message.ID);
        if (actor == null)
        {
            var state = PendingFor(message.ID);
            if (message.ColorMaskHSV.HasValue)
                state.ColorMaskHsv = message.ColorMaskHSV.Value;
            if (message.DiffuseColor.HasValue)
                state.ColorMul = message.DiffuseColor.Value.ToVector3();
            return;
        }

        if (message.ColorMaskHSV.HasValue)
            actor.ColorMaskHsv = message.ColorMaskHSV.Value;
        MarkDirty(actor);
        if (message.DiffuseColor.HasValue)
        {
            actor.ColorMul = message.DiffuseColor.Value.ToVector3();
            MarkMaterialDirty(actor);
            revEntity++;
        }
    }

    public void SetModelProperties(MyRenderMessageUpdateModelProperties message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        var materialName = message.MaterialName.String;
        if (string.IsNullOrEmpty(materialName))
        {
            if (message.FlagsChange.HasValue)
                actor.Flags = (actor.Flags | message.FlagsChange.Value.Add) & ~message.FlagsChange.Value.Remove;
            if (message.DiffuseColor.HasValue)
                actor.ColorMul = message.DiffuseColor.Value.ToVector3();
            if (message.Emissivity.HasValue)
                actor.Emissivity = message.Emissivity.Value;
            MarkDirty(actor);
            MarkMaterialDirty(actor);
            revProps++;
            return;
        }

        var overrides = actor.MaterialOverrides ??= new Dictionary<string, MaterialOverride>();
        if (!overrides.TryGetValue(materialName, out var material))
            overrides[materialName] = material = new MaterialOverride();

        // Material flags (e.g. screen areas hide/show only their own material, not the whole actor)
        if (message.FlagsChange.HasValue)
            material.FlagsChange = message.FlagsChange.Value;
        if (message.DiffuseColor.HasValue)
            material.ColorMul = message.DiffuseColor.Value.ToVector3();
        if (message.Emissivity.HasValue)
            material.Emissivity = message.Emissivity.Value;
        MarkMaterialDirty(actor);
        revProps++;
    }

    public void SetColorEmissivity(MyRenderMessageUpdateColorEmissivity message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        var materialName = message.MaterialName.String;
        var overrides = actor.MaterialOverrides ??= new Dictionary<string, MaterialOverride>();
        if (!overrides.TryGetValue(materialName, out var material))
            overrides[materialName] = material = new MaterialOverride();

        material.ColorMul = message.DiffuseColor.ToVector3();
        material.Emissivity = message.Emissivity;
        MarkMaterialDirty(actor);
        revProps++;
    }

    public void SetVisibilityUpdates(uint id, bool state)
    {
        var actor = Find(id);
        if (actor == null)
            return;

        actor.VisibilityUpdates = state;
        if (state)
        {
            if (!visibilityUpdates.Contains(actor))
                visibilityUpdates.Add(actor);
        }
        else
        {
            visibilityUpdates.Remove(actor);
        }
    }

    public void SetCharacterSkeleton(MyRenderMessageSetCharacterSkeleton message)
    {
        var actor = Find(message.CharacterID);
        if (actor == null)
            return;

        actor.SkeletonBones = message.SkeletonBones == null
            ? []
            : (MySkeletonBoneDescription[])message.SkeletonBones.Clone();
        actor.SkeletonIndices = message.SkeletonIndices == null
            ? []
            : (int[])message.SkeletonIndices.Clone();

        actor.SkinTransforms = new Matrix[actor.SkeletonBones.Length];
        for (var i = 0; i < actor.SkeletonBones.Length; i++)
            actor.SkinTransforms[i] = actor.SkeletonBones[i].SkinTransform;

        actor.SkeletonIndexUInts = new uint[actor.SkeletonIndices.Length];
        for (var i = 0; i < actor.SkeletonIndices.Length; i++)
            actor.SkeletonIndexUInts[i] = actor.SkeletonIndices[i] >= 0
                ? (uint)actor.SkeletonIndices[i]
                : uint.MaxValue;

        RenderLog.Info($"Character skeleton {message.CharacterID}: {actor.SkeletonBones.Length} bones, " +
                       $"{actor.SkeletonIndices.Length} skin indices");
        if (!skinned.Contains(actor))
            skinned.Add(actor);

        // The GPU part cache must drop this actor once it has a skeleton and move it to the
        // direct skinned path.
        revSkeleton++;
        revision++;
        if (!recreatedActors.Contains(actor))
            recreatedActors.Add(actor);
    }

    public void SetCharacterTransforms(MyRenderMessageSetCharacterTransforms message)
    {
        var actor = Find(message.CharacterID);
        if (actor == null)
        {
            if (reportedMissingTransforms.Add(message.CharacterID))
                RenderLog.Warning($"SetCharacterTransforms: actor {message.CharacterID} not found");
            return;
        }

        if (message.BoneAbsoluteTransforms == null)
            return;

        var source = message.BoneAbsoluteTransforms;
        var first = actor.BoneTransforms == null || actor.BoneTransforms.Length < source.Length;
        if (first)
            actor.BoneTransforms = new Matrix[source.Length];
        Array.Copy(source, actor.BoneTransforms, source.Length);
        if (first)
            RenderLog.Info($"Character transforms {message.CharacterID}: {source.Length} bone transforms per frame");
    }

    public void SetInstanceBuffer(MyRenderMessageSetInstanceBuffer message)
    {
        var actor = Find(message.ID);
        if (actor == null)
        {
            // New grid cells can send the instance range before the render entity exists.
            var state = PendingFor(message.ID);
            state.InstanceBufferId = message.InstanceBufferId;
            state.InstanceStart = message.InstanceStart;
            state.InstanceCount = message.InstanceCount;
            state.LocalAabb = message.LocalAabb;
            return;
        }

        var hadInstances = actor.HasInstances;
        actor.InstanceBufferId = message.InstanceBufferId;
        actor.InstanceStart = message.InstanceStart;
        actor.InstanceCount = message.InstanceCount;
        actor.LocalAabb = message.LocalAabb;

        MarkDirty(actor);

        // Range changes do not alter the persistent part data; only gaining/losing instances
        // changes the material's Instanced flag. Avoid rebuilding the whole cache per grid update.
        if (hadInstances != actor.HasInstances)
        {
            MarkMaterialDirty(actor);
            revInstance++;
        }
    }

    // ChangeMaterialTexture: merge per-material texture replacements; Changes == null clears them
    public void ChangeMaterialTexture(MyRenderMessageChangeMaterialTexture message)
    {
        var actor = Find(message.RenderObjectID);
        if (actor == null)
        {
            var state = PendingFor(message.RenderObjectID);
            if (message.Changes == null)
            {
                state.TextureChanges = null;
                return;
            }

            state.TextureChanges ??= new Dictionary<string, MyTextureChange>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in message.Changes)
                state.TextureChanges[pair.Key.String] = pair.Value;
            revTexture++;
            return;
        }

        if (message.Changes == null)
        {
            actor.TextureChanges = null;
            MarkMaterialDirty(actor);
            revTexture++;
            return;
        }

        var changes = actor.TextureChanges ??= new Dictionary<string, MyTextureChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in message.Changes)
            changes[pair.Key.String] = pair.Value;
        MarkMaterialDirty(actor);
        revTexture++;
    }

    public static RenderFlags GetMaterialFlags(SceneActor actor, string materialName)
    {
        // Runtime visibility (UpdateRenderObjectVisibility) is authoritative over the create flags.
        var flags = actor.Visible
            ? actor.Flags | RenderFlags.Visible
            : actor.Flags & ~RenderFlags.Visible;

        if (!string.IsNullOrEmpty(materialName) && actor.MaterialOverrides != null &&
            actor.MaterialOverrides.TryGetValue(materialName, out var material) && material.FlagsChange.HasValue)
        {
            flags = (flags | material.FlagsChange.Value.Add) & ~material.FlagsChange.Value.Remove;
        }

        return flags;
    }

    public static bool TryGetTextureChange(SceneActor actor, string materialName, out MyTextureChange change)
    {
        change = default;
        return !string.IsNullOrEmpty(materialName) && actor.TextureChanges != null &&
               actor.TextureChanges.TryGetValue(materialName, out change);
    }

    public void ChangeModel(MyRenderMessageChangeModel message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        actor.Model = message.Model;
        actor.Scale = message.Scale != 0f ? message.Scale : 1f;
        revModel++;
        revision++;
        if (!recreatedActors.Contains(actor))
            recreatedActors.Add(actor);
    }

    public void Remove(uint id)
    {
        pending.Remove(id);
        if (!actors.Remove(id, out var removed))
            return;
        skinned.Remove(removed);
        visibilityUpdates.Remove(removed);
        dirtyActors.Remove(removed);
        materialDirtyActors.Remove(removed);
        if (removed.ParentId != 0 && children.TryGetValue(removed.ParentId, out var oldChildren))
            oldChildren.Remove(id);
        children.Remove(id);

        foreach (var actor in actors.Values)
        {
            if (actor.ParentId != id)
                continue;
            actor.ParentId = 0;
            MarkDirty(actor);
        }
        revRemove++;
        revision++;
        removedActorIds.Add(id);
        recreatedActors.Remove(removed);
        transformRevision++;
    }

    public void Clear()
    {
        actors.Clear();
        pending.Clear();
        skinned.Clear();
        dirtyActors.Clear();
        materialDirtyActors.Clear();
        visibilityUpdates.Clear();
        createdActors.Clear();
        removedActorIds.Clear();
        recreatedActors.Clear();
        children.Clear();
        revRemove++;
        revision++;
        fullRevision++;
        transformRevision++;
    }
}
