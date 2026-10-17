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

    public Dictionary<string, MaterialOverride> MaterialOverrides;

    // CreateStaticGroup: one model drawn once per local matrix
    public Vector3D StaticTranslation;
    public Matrix[] StaticMatrices;

    // SetInstanceBuffer: draw the model once per instance in the given range
    public uint InstanceBufferId;
    public int InstanceStart;
    public int InstanceCount;

    public bool HasInstances => InstanceCount > 0 && InstanceBufferId != 0;
}

// Actors by render object ID. The game owns lifetime; messages mutate this on the render thread only.
internal sealed class SceneActors
{
    private readonly Dictionary<uint, SceneActor> actors = new();

    public int Count => actors.Count;

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
        };

        actors[message.ID] = actor;
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
    }

    public SceneActor Find(uint id) => actors.TryGetValue(id, out var actor) ? actor : null;

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
            return;

        if (message.ColorMaskHSV.HasValue)
            actor.ColorMaskHsv = message.ColorMaskHSV.Value;
        if (message.DiffuseColor.HasValue)
            actor.ColorMul = message.DiffuseColor.Value.ToVector3();
    }

    public void SetModelProperties(MyRenderMessageUpdateModelProperties message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        if (message.FlagsChange.HasValue)
            actor.Flags = (actor.Flags | message.FlagsChange.Value.Add) & ~message.FlagsChange.Value.Remove;

        var materialName = message.MaterialName.String;
        if (string.IsNullOrEmpty(materialName))
        {
            if (message.DiffuseColor.HasValue)
                actor.ColorMul = message.DiffuseColor.Value.ToVector3();
            if (message.Emissivity.HasValue)
                actor.Emissivity = message.Emissivity.Value;
            return;
        }

        var overrides = actor.MaterialOverrides ??= new Dictionary<string, MaterialOverride>();
        if (!overrides.TryGetValue(materialName, out var material))
            overrides[materialName] = material = new MaterialOverride();

        if (message.DiffuseColor.HasValue)
            material.ColorMul = message.DiffuseColor.Value.ToVector3();
        if (message.Emissivity.HasValue)
            material.Emissivity = message.Emissivity.Value;
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
    }

    public void SetInstanceBuffer(MyRenderMessageSetInstanceBuffer message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        actor.InstanceBufferId = message.InstanceBufferId;
        actor.InstanceStart = message.InstanceStart;
        actor.InstanceCount = message.InstanceCount;
    }

    public void ChangeModel(MyRenderMessageChangeModel message)
    {
        var actor = Find(message.ID);
        if (actor == null)
            return;

        actor.Model = message.Model;
        actor.Scale = message.Scale != 0f ? message.Scale : 1f;
    }

    public void Remove(uint id) => actors.Remove(id);

    public void Clear() => actors.Clear();
}
