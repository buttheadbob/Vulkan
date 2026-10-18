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
}

// Actors by render object ID. The game owns lifetime; messages mutate this on the render thread only.
internal sealed class SceneActors
{
    private sealed class PendingActor
    {
        public Vector3? ColorMaskHsv;
        public Vector3? ColorMul;
        public Dictionary<string, MyTextureChange> TextureChanges;
    }

    private readonly Dictionary<uint, SceneActor> actors = new();
    private readonly Dictionary<uint, PendingActor> pending = new();
    private readonly HashSet<uint> reportedMissingTransforms = new();
    private long revision;

    public int Count => actors.Count;

    // Bumped on any change that affects persistent part/material data or the actor list.
    public long Revision => revision;

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
        revision++;
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
        revision++;
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
        revision++;
    }

    public void CreateManualCullObject(MyRenderMessageCreateManualCullObject message)
    {
        actors[message.ID] = new SceneActor
        {
            Id = message.ID,
            WorldMatrix = message.WorldMatrix,
            Visible = true,
        };
    }

    public void SetParentCullObject(MyRenderMessageSetParentCullObject message)
    {
        var child = Find(message.ID);
        if (child == null)
            return;

        child.ParentId = message.CullObjectID;
        child.LocalMatrix = message.ChildToParent ?? Matrix.Identity;
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
    public MatrixD GetWorldMatrix(SceneActor actor) => GetWorldMatrix(actor, 0);

    private MatrixD GetWorldMatrix(SceneActor actor, int depth)
    {
        if (depth >= 4 || actor.ParentId == 0 || !actors.TryGetValue(actor.ParentId, out var parent))
            return actor.WorldMatrix;

        return (MatrixD)actor.LocalMatrix * GetWorldMatrix(parent, depth + 1);
    }

    public void Update(MyRenderMessageUpdateRenderObject message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        if (message.Data.HasWorldMatrix)
            actor.WorldMatrix = message.Data.WorldMatrix;
    }

    public void SetVisible(uint id, bool visible, bool near)
    {
        var actor = Find(id);
        if (actor != null)
        {
            actor.Visible = visible;
            actor.Near = near;
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
        if (message.DiffuseColor.HasValue)
            actor.ColorMul = message.DiffuseColor.Value.ToVector3();
        revision++;
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
            revision++;
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
        revision++;
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
        revision++;
    }

    public void SetVisibilityUpdates(uint id, bool state)
    {
        var actor = Find(id);
        if (actor != null)
            actor.VisibilityUpdates = state;
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
            return;

        actor.InstanceBufferId = message.InstanceBufferId;
        actor.InstanceStart = message.InstanceStart;
        actor.InstanceCount = message.InstanceCount;
        actor.LocalAabb = message.LocalAabb;
        revision++;
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
            revision++;
            return;
        }

        if (message.Changes == null)
        {
            actor.TextureChanges = null;
            revision++;
            return;
        }

        var changes = actor.TextureChanges ??= new Dictionary<string, MyTextureChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in message.Changes)
            changes[pair.Key.String] = pair.Value;
        revision++;
    }

    public static RenderFlags GetMaterialFlags(SceneActor actor, string materialName)
    {
        var flags = actor.Flags;
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
        revision++;
    }

    public void Remove(uint id)
    {
        pending.Remove(id);
        if (!actors.Remove(id))
            return;
        foreach (var actor in actors.Values)
        {
            if (actor.ParentId == id)
                actor.ParentId = 0;
        }
        revision++;
    }

    public void Clear()
    {
        actors.Clear();
        pending.Clear();
        revision++;
    }
}
