#if TOOLS
using System;
using Godot;
using ParadiseExport.Authoring;

namespace ParadiseGodot.Authoring
{
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

        [ExportGroup("Collider Export")]
        [Export] public Godot.Collections.Array<NodePath> PhysicsColliders { get; set; } = new();
        [Export] public Godot.Collections.Array<NodePath> InteractionColliders { get; set; } = new();

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
        [Export] public float AngularSpeed { get; set; } = ParadiseAuthoringDefaults.AngularSpeed;
        [Export] public float Acceleration { get; set; } = ParadiseAuthoringDefaults.Acceleration;
        [Export] public string IdleAnimation { get; set; } = "";
        [Export] public string WalkAnimation { get; set; } = "";

        // Resolved (sanitized) accessors used by the exporter — keep export output identical to
        // the Unity tool's defaults/fallbacks.
        public string ResolvedKind => string.IsNullOrWhiteSpace(Kind) ? "Prop" : Kind;
        public float ResolvedMoveSpeed => Sanitize(MoveSpeed, ParadiseAuthoringDefaults.MoveSpeed);
        public float ResolvedAngularSpeed => Sanitize(AngularSpeed, ParadiseAuthoringDefaults.AngularSpeed);
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
                name == PropertyName.AngularSpeed ||
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
