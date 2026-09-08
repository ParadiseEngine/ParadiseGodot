#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    public enum AuthoredValueKind
    {
        /// <summary>An absent field or an unreadable value.</summary>
        None,
        Bool,
        Integer,
        Number,
        Text,
        /// <summary>A fixed-length vector or quaternion.</summary>
        Numbers,
        /// <summary>Four channels in 0..1, from <c>{ r, g, b, a }</c> or a four-float array.</summary>
        Rgba,
        /// <summary><c>{ guid, path }</c>: the GUID survives renames; the path supports manual repair.</summary>
        Reference,
    }

    /// <summary>An authored value that can be used without a running Godot process.</summary>
    /// <remarks>Constructing a Godot <c>Variant</c> outside Godot segfaults the test host.
    /// Conversion to Variant belongs at the editor edge.</remarks>
    public readonly record struct AuthoredValue(
        AuthoredValueKind Kind,
        bool Bool = false,
        long Integer = 0,
        double Number = 0,
        string? Text = null,
        float[]? Numbers = null,
        Guid Identity = default)
    {
        public static AuthoredValue None { get; } = new(AuthoredValueKind.None);

        /// <summary>An asset reference whose <paramref name="path"/> is relative to <c>assets/</c>.</summary>
        public static AuthoredValue Reference(Guid guid, string path) =>
            new(AuthoredValueKind.Reference, Text: path, Identity: guid);
    }

    /// <summary>Reads component payloads using the authoring schema's types.</summary>
    /// <remarks>Missing or unreadable leaves return <see cref="AuthoredValue.None"/> so callers
    /// use schema defaults. Field paths nest with slashes; vectors are float arrays and colours
    /// are <c>{ r, g, b, a }</c>, matching the exporter.</remarks>
    public static class AuthoredPayload
    {
        /// <param name="path">Slash-separated field path from the schema.</param>
        public static AuthoredValue Read(CanonicalTomlTable data, string path, Variant.Type type)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(path);

            return Leaf(data, path) is { } value ? Coerce(value, type) : AuthoredValue.None;
        }

        private static object? Leaf(CanonicalTomlTable data, string path)
        {
            var current = data;
            int start = 0;
            while (true)
            {
                int slash = path.IndexOf('/', start);
                if (slash < 0) return current.Value(path[start..]);

                if (current.Value(path[start..slash]) is not CanonicalTomlTable nested) return null;
                current = nested;
                start = slash + 1;
            }
        }

        private static AuthoredValue Coerce(object value, Variant.Type type) => type switch
        {
            Variant.Type.Bool => value is bool flag
                ? new AuthoredValue(AuthoredValueKind.Bool, Bool: flag)
                : AuthoredValue.None,

            // Reject floats here rather than rounding authored values.
            Variant.Type.Int => value is long integral
                ? new AuthoredValue(AuthoredValueKind.Integer, Integer: integral)
                : AuthoredValue.None,

            // Canonical TOML may encode 1.0 as integer 1, so float fields must accept integers.
            Variant.Type.Float => Numeric(value) is { } number
                ? new AuthoredValue(AuthoredValueKind.Number, Number: number)
                : AuthoredValue.None,

            // References share the string schema type; the inline { guid, path } shape distinguishes them.
            Variant.Type.String => value switch
            {
                string text => new AuthoredValue(AuthoredValueKind.Text, Text: text),
                CanonicalInlineTable inline => Reference(inline),
                _ => AuthoredValue.None,
            },

            Variant.Type.Vector2 => Run(value, 2),
            Variant.Type.Vector3 => Run(value, 3),
            Variant.Type.Quaternion => Run(value, 4),
            Variant.Type.Color => Rgba(value),
            _ => AuthoredValue.None,
        };

        /// <summary>Require the exact vector length; accepting short arrays can silently reset placement.</summary>
        private static AuthoredValue Run(object value, int length)
        {
            if (value is not IReadOnlyList<object> items || items.Count != length) return AuthoredValue.None;

            var numbers = new float[length];
            for (int index = 0; index < length; index++)
            {
                if (Numeric(items[index]) is not { } number) return AuthoredValue.None;
                numbers[index] = (float)number;
            }

            return new AuthoredValue(AuthoredValueKind.Numbers, Numbers: numbers);
        }

        /// <summary>Read <c>{ r, g, b, a }</c> or a hand-authored four-float array.</summary>
        private static AuthoredValue Rgba(object value)
        {
            if (value is CanonicalTomlTable table)
            {
                var channels = new float[4];
                // Omitted alpha means opaque.
                channels[3] = 1f;
                var names = new[] { "r", "g", "b", "a" };
                for (int index = 0; index < names.Length; index++)
                {
                    if (table.Value(names[index]) is not { } channel) continue;
                    if (Numeric(channel) is not { } number) return AuthoredValue.None;
                    channels[index] = (float)number;
                }

                return new AuthoredValue(AuthoredValueKind.Rgba, Numbers: channels);
            }

            var run = Run(value, 4);
            return run.Kind == AuthoredValueKind.Numbers
                ? run with { Kind = AuthoredValueKind.Rgba }
                : AuthoredValue.None;
        }

        /// <summary>Malformed references read as absent, preserving the schema default.</summary>
        private static AuthoredValue Reference(CanonicalInlineTable inline)
        {
            Guid.TryParse(inline.Value("guid") as string, out var guid);
            var path = inline.Value("path") as string ?? "";

            // {} explicitly keeps the GLB's own material; it is distinct from an absent field.
            return inline.Count == 0 || guid != default || path.Length > 0
                ? AuthoredValue.Reference(guid, path)
                : AuthoredValue.None;
        }

        private static double? Numeric(object value) => value switch
        {
            double number => number,
            long integer => integer,
            _ => null,
        };
    }
}
#endif
