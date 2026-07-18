#if TOOLS
using System;
using Godot;
using Paradise.Export.Authoring;

namespace ParadiseGodot.Authoring
{
    /// <summary>Authoring-side particle render kind; None = no emitter exported. The exported
    /// contract enum (<c>ParticleRenderKind</c>) has no None — absence is the null component.</summary>
    public enum ParticleEmitterExportKind
    {
        None,
        Sprite,
        Voxel,
    }

    /// <summary>
    /// Marks a Godot node as an exportable Paradise entity — the Godot equivalent of
    /// ParadiseUnityEditor's <c>EntityAuthoring</c>. Engine-neutral by design: <see cref="Kind"/>
    /// is a free-form label and <see cref="IsAgent"/> gates movement export.
    ///
    /// The entity GUID is a stable per-placement identity stored in node metadata (persisted in
    /// the .tscn). Its lifecycle (mint + uniqueness) runs on editor save via
    /// <c>NOTIFICATION_EDITOR_PRE_SAVE</c>, mirroring the Unity hooks so exported references stay
    /// stable across scene rebuilds.
    /// </summary>
    [Tool]
    [GlobalClass]
    public partial class EntityExport : Node3D
    {
        private const string GuidMetaKey = "paradise_entity_guid";

        // EnumSuggestion gives a dropdown of common kinds in the inspector while keeping Kind
        // free-form (you can still type any custom value) — the contract treats it as a label.
        [Export(PropertyHint.EnumSuggestion, "Prop,Character,Door,Trigger,Pickup")]
        public string Kind { get; set; } = "Prop";
        [Export] public bool ActiveOnLoad { get; set; } = true;

        [Export(PropertyHint.File, "*.glb,*.gltf,*.tscn,*.scn")]
        public string ModelPath { get; set; } = "";

        [Export] public string InitialAnimation { get; set; } = "";

        [ExportGroup("Physics Body")]
        // Dynamic bodies (balls, debris) export Rigidbody.BodyType = Dynamic so the runtime
        // spawns them as simulated dynamic spheres instead of static scenery.
        [Export] public bool IsDynamicBody { get; set; }
        [Export] public float BodyMass { get; set; } = 1f;
        // Physics material params carried by the contract's Rigidbody fields. Defaults are the
        // constants the exporter always wrote, so existing scenes re-export byte-identical.
        // On a dynamic body: damping = roll decay, restitution = body-body bounce. On a static
        // body: restitution = the bounce dynamic bodies get off this surface (cushions).
        [Export] public float BodyLinearDamping { get; set; } = 0f;
        [Export] public float BodyRestitution { get; set; } = 0.2f;
        [Export] public float BodyFriction { get; set; } = 0.5f;

        [ExportGroup("Collider Export")]
        [Export] public Godot.Collections.Array<NodePath> PhysicsColliders { get; set; } = new();
        [Export] public Godot.Collections.Array<NodePath> InteractionColliders { get; set; } = new();

        [ExportGroup("Sprite Animation")]
        // Flipbook playback for a Sprite3D child. The Sprite3D supplies the sheet texture, the
        // hframes/vframes grid, the quad size (pixel_size × frame pixels) and the billboard
        // mode; these fields add the CLOCK the node doesn't model (the simulation owns sprite
        // time so both hosts show the same frame). Present Sprite3D child = exported component.
        [Export] public float SpriteFps { get; set; } = 10f;
        [Export] public bool SpriteLoop { get; set; } = true;
        // 0 = the full hframes × vframes grid.
        [Export] public int SpriteFrameCount { get; set; }

        [ExportGroup("Particle Emitter")]
        // Deterministic sim-side particles (Kind != None exports the component): Sprite =
        // flipbook camera-facing quads (2D particles), Voxel = solid cubes (3D particles).
        // Emission is a SpreadDegrees cone around this node's +Y; particles live in world space.
        [Export] public ParticleEmitterExportKind ParticleKind { get; set; } = ParticleEmitterExportKind.None;
        [Export] public float ParticleEmitRate { get; set; } = 8f;
        [Export] public float ParticleLifetime { get; set; } = 1.5f;
        [Export] public float ParticleSpeed { get; set; } = 2f;
        [Export] public float ParticleSpreadDegrees { get; set; } = 25f;
        [Export] public float ParticleGravity { get; set; } = -9.8f;
        [Export] public float ParticleDrag { get; set; }
        [Export] public float ParticleStartSize { get; set; } = 0.25f;
        [Export] public float ParticleEndSize { get; set; } = 0.25f;
        [Export] public int ParticleMaxCount { get; set; } = 64;
        // Any nonzero value is valid; negatives wrap to large unsigned seeds at export
        // (the contract stores a uint), which is harmless but surprising — prefer positives.
        [Export] public int ParticleSeed { get; set; } = 1;
        [Export] public Color ParticleColor { get; set; } = Colors.White;
        // Sprite kind only: the flipbook sheet (an image under res://data/, e.g. data/sprites/).
        [Export(PropertyHint.File, "*.png,*.jpg,*.jpeg")] public string ParticleSheet { get; set; } = "";
        [Export] public int ParticleSheetColumns { get; set; } = 1;
        [Export] public int ParticleSheetRows { get; set; } = 1;
        // 0 = the full grid.
        [Export] public int ParticleSheetFrameCount { get; set; }
        // 0 = stretch the flipbook once over each particle's lifetime.
        [Export] public float ParticleSheetFps { get; set; }

        [ExportGroup("Agent (movement)")]
        private bool _isAgent;
        // Toggling IsAgent re-runs _ValidateProperty so the movement fields show/hide live.
        [Export] public bool IsAgent
        {
            get => _isAgent;
            set
            {
                _isAgent = value;
                NotifyPropertyListChanged();
            }
        }

        [Export] public float MoveSpeed { get; set; } = ParadiseAuthoringDefaults.MoveSpeed;
        [Export] public float Acceleration { get; set; } = ParadiseAuthoringDefaults.Acceleration;
        [Export] public string IdleAnimation { get; set; } = "";
        [Export] public string WalkAnimation { get; set; } = "";

        // Resolved (sanitized) accessors used by the exporter — keep export output identical to
        // the Unity tool's defaults/fallbacks.
        public string ResolvedKind => string.IsNullOrWhiteSpace(Kind) ? "Prop" : Kind;
        public float ResolvedMoveSpeed => Sanitize(MoveSpeed, ParadiseAuthoringDefaults.MoveSpeed);
        public float ResolvedAcceleration => Sanitize(Acceleration, ParadiseAuthoringDefaults.Acceleration);

        public string ResolvedIdleAnimation =>
            string.IsNullOrWhiteSpace(IdleAnimation) ? ParadiseAuthoringDefaults.IdleAnimationFallback : IdleAnimation;

        public string ResolvedWalkAnimation =>
            string.IsNullOrWhiteSpace(WalkAnimation) ? ParadiseAuthoringDefaults.WalkAnimationFallback : WalkAnimation;

        public string? ResolvedInitialAnimation =>
            string.IsNullOrWhiteSpace(InitialAnimation) ? null : InitialAnimation;

        /// <summary>Stable per-placement identity; <see cref="Guid.Empty"/> until minted.</summary>
        public Guid EntityGuid =>
            HasMeta(GuidMetaKey) && Guid.TryParse(GetMeta(GuidMetaKey).AsString(), out Guid g) ? g : Guid.Empty;

        /// <summary>Force a specific GUID (used by rebuild pipelines to carry identity across a
        /// destroy/recreate). Rejects <see cref="Guid.Empty"/>.</summary>
        public bool RestoreEntityGuid(Guid value)
        {
            if (value == Guid.Empty)
            {
                return false;
            }

            SetMeta(GuidMetaKey, value.ToString("N"));
            return true;
        }

        // Hide the agent movement fields in the inspector unless IsAgent is set. They keep the
        // Storage flag (only Editor is cleared), so any authored values are retained, not lost.
        public override void _ValidateProperty(Godot.Collections.Dictionary property)
        {
            StringName name = property["name"].AsStringName();
            if (!IsAgent && (
                name == PropertyName.MoveSpeed ||
                name == PropertyName.Acceleration ||
                name == PropertyName.IdleAnimation ||
                name == PropertyName.WalkAnimation))
            {
                var usage = property["usage"].As<PropertyUsageFlags>();
                property["usage"] = (int)(usage & ~PropertyUsageFlags.Editor);
            }
        }

        public override void _Notification(int what)
        {
            if (what == NotificationEditorPreSave)
            {
                EnsureUniqueGuid();
            }
        }

        /// <summary>
        /// Ensure a GUID exists — minting and persisting one if the node has none — and return it.
        /// The exporter calls this so a freshly-placed, never-saved entity still exports a valid,
        /// stable identity instead of <see cref="Guid.Empty"/> (which would collide across entities).
        /// </summary>
        public Guid EnsureEntityGuid()
        {
            Guid current = EntityGuid;
            if (current != Guid.Empty)
            {
                return current;
            }

            Guid minted = Guid.NewGuid();
            SetMeta(GuidMetaKey, minted.ToString("N"));
            return minted;
        }

        // Ensure a GUID exists and is unique among EntityExport nodes in the edited scene; if a
        // collision is found (e.g. a duplicated node), regenerate this node's GUID.
        private void EnsureUniqueGuid()
        {
            EnsureEntityGuid();

            Node? sceneRoot = GetTree()?.EditedSceneRoot;
            if (sceneRoot is null)
            {
                return;
            }

            Guid mine = EntityGuid;
            if (CollidesWithin(sceneRoot, mine))
            {
                SetMeta(GuidMetaKey, Guid.NewGuid().ToString("N"));
            }
        }

        private bool CollidesWithin(Node node, Guid mine)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is EntityExport other && other != this && other.EntityGuid == mine)
                {
                    return true;
                }

                if (CollidesWithin(child, mine))
                {
                    return true;
                }
            }

            return false;
        }

        private static float Sanitize(float value, float fallback) =>
            value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : fallback;
    }
}
#endif
