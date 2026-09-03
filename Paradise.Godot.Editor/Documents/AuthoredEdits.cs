#if TOOLS
using System;
using System.Collections.Generic;

namespace ParadiseGodot.Documents
{
    /// <summary>
    /// What an author CHANGED on one entity since it was materialized from its document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not the entity's values — the document already holds those. This is the overlay a save
    /// applies on top of the file it re-reads, and the distinction is the whole reason it exists:
    /// writing back everything the inspector holds would rewrite payloads nobody touched, through a
    /// Godot <c>Variant</c> that normalizes as it goes (an integer becomes a float, a tuple becomes
    /// a list). "Nearly the same value" is a bug in data promised verbatim.
    /// </para>
    /// <para>
    /// It also means a component this addon has never heard of survives a save untouched: the
    /// writer re-reads the document and only edits what is named here, so an unknown payload is
    /// never in a position to be dropped.
    /// </para>
    /// <para>
    /// Add and remove are LAST WRITE WINS on the same id. An author who ticks a component and then
    /// unticks it has made no change, and an overlay that remembered both would ask the writer to
    /// do two contradictory things.
    /// </para>
    /// </remarks>
    public sealed class AuthoredEdits
    {
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _added = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

        /// <summary>Whether anything at all was changed.</summary>
        public bool Any => _fields.Count > 0 || _added.Count > 0 || _removed.Count > 0;

        /// <summary>Components the author turned ON. The writer takes their whole payload, because
        /// a component that was not in the document has no values there to override.</summary>
        public IReadOnlyCollection<string> Added => _added;

        /// <summary>Components the author turned OFF, to be dropped from the document.</summary>
        public IReadOnlyCollection<string> Removed => _removed;

        /// <summary>Fields the author typed into, as <c>&lt;componentId&gt;/&lt;path&gt;</c>.</summary>
        public IReadOnlyCollection<string> Fields => _fields;

        public void ComponentAdded(string componentId)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            _removed.Remove(componentId);
            _added.Add(componentId);
        }

        public void ComponentRemoved(string componentId)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            _added.Remove(componentId);
            _removed.Add(componentId);
            // Its fields go with it. Keeping them would ask the writer to set values on a component
            // it is deleting in the same pass.
            _fields.RemoveWhere(key => Owns(key, componentId));
        }

        public void FieldChanged(string componentId, string path)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            ArgumentNullException.ThrowIfNull(path);
            // A field typed into a component the author had just removed is a re-add: the inspector
            // cannot show a field of a component it is not showing, so this only happens through a
            // script, and taking the field means taking the component back.
            if (_removed.Remove(componentId)) _added.Add(componentId);
            _fields.Add(componentId + "/" + path);
        }

        /// <summary>Whether this exact field was typed into.</summary>
        public bool IsFieldEdited(string componentId, string path) =>
            _fields.Contains(componentId + "/" + path);

        /// <summary>The edited field PATHS of one component, without its id prefix.</summary>
        public IEnumerable<string> FieldsOf(string componentId)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            foreach (var key in _fields)
            {
                if (Owns(key, componentId)) yield return key[(componentId.Length + 1)..];
            }
        }

        /// <summary>Forget everything — what a successful save leaves behind, since the document on
        /// disk now says what the overlay used to.</summary>
        public void Clear()
        {
            _fields.Clear();
            _added.Clear();
            _removed.Clear();
        }

        private static bool Owns(string key, string componentId) =>
            key.Length > componentId.Length &&
            key[componentId.Length] == '/' &&
            key.StartsWith(componentId, StringComparison.Ordinal);
    }
}
#endif
