#if TOOLS
using System;
using System.Collections.Generic;

namespace ParadiseGodot.Documents
{
    /// <summary>Edits to one entity since it was loaded from its document.</summary>
    /// <remarks>
    /// The writer merges only these edits into the file it re-reads, preserving unknown components
    /// and avoiding Variant normalization of untouched values. Add/remove is last-write-wins per ID.
    /// </remarks>
    public sealed class AuthoredEdits
    {
        private readonly HashSet<string> _fields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _added = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

        public bool Any => _fields.Count > 0 || _added.Count > 0 || _removed.Count > 0;

        /// <summary>Added components; the writer takes their full payload because no stored values exist.</summary>
        public IReadOnlyCollection<string> Added => _added;

        public IReadOnlyCollection<string> Removed => _removed;

        /// <summary>Edited fields, keyed as <c>&lt;componentId&gt;/&lt;path&gt;</c>.</summary>
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
            var prefix = componentId + "/";
            _fields.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
        }

        public void FieldChanged(string componentId, string path)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            ArgumentNullException.ThrowIfNull(path);
            // Editing a removed component re-adds it, including edits made by scripts.
            if (_removed.Remove(componentId)) _added.Add(componentId);
            _fields.Add(componentId + "/" + path);
        }

        public bool IsFieldEdited(string componentId, string path) =>
            _fields.Contains(componentId + "/" + path);

        /// <summary>Edited paths for one component, without its ID prefix.</summary>
        public IEnumerable<string> FieldsOf(string componentId)
        {
            ArgumentNullException.ThrowIfNull(componentId);
            var prefix = componentId + "/";
            foreach (var key in _fields)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) yield return key[prefix.Length..];
            }
        }

        /// <summary>Clear the overlay after a successful save.</summary>
        public void Clear()
        {
            _fields.Clear();
            _added.Clear();
            _removed.Clear();
        }
    }
}
#endif
