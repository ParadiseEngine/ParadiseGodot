#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Paradise.Assets.Documents;

namespace ParadiseGodot.Documents
{
    /// <summary>What kind of thing an <see cref="AuthoredValue"/> holds.</summary>
    public enum AuthoredValueKind
    {
        /// <summary>The field was absent, or present in a shape it cannot be read as.</summary>
        None,
        Bool,
        Integer,
        Number,
        Text,
        /// <summary>A fixed-length float run: a vector, or a quaternion.</summary>
        Numbers,
        /// <summary>Four channels in 0..1, from <c>{ r, g, b, a }</c> or a four-float array.</summary>
        Rgba,
    }

    /// <summary>
    /// One authored leaf, in a form that carries no Godot <c>Variant</c>.
    /// </summary>
    /// <remarks>
    /// A neutral union rather than a <c>Variant</c> because CONSTRUCTING a Variant outside a
    /// running Godot process segfaults the host — see <c>.claude/lessons.md</c>. Keeping the
    /// conversion pure is what makes the shape rules below testable at all; turning one of these
    /// into a Variant is a one-line switch at the editor edge.
    /// </remarks>
    public readonly record struct AuthoredValue(
        AuthoredValueKind Kind,
        bool Bool = false,
        long Integer = 0,
        double Number = 0,
        string? Text = null,
        float[]? Numbers = null)
    {
        public static AuthoredValue None { get; } = new(AuthoredValueKind.None);
    }

    /// <summary>
    /// Reads a document's component payload at the types the authoring schema declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of what the exporter writes, and deliberately written as the inverse: a field's
    /// path is slash-separated and nests, a vector is a float run, and a colour is
    /// <c>{ r, g, b, a }</c> — the shapes the generated reader parses.
    /// </para>
    /// <para>
    /// A leaf that is absent, or present in a shape the field cannot take, reads as
    /// <see cref="AuthoredValue.None"/> rather than as a zero. The difference matters: the caller
    /// falls back to the schema's own default, so a payload written by a newer build with a field
    /// this one does not understand leaves that field at its default instead of silently zeroing
    /// what an author set.
    /// </para>
    /// </remarks>
    public static class AuthoredPayload
    {
        /// <summary>Read one field out of a payload.</summary>
        /// <param name="data">The component's payload.</param>
        /// <param name="path">Slash-separated field path, as the schema spells it.</param>
        /// <param name="type">What the schema says the field is.</param>
        public static AuthoredValue Read(CanonicalTomlTable data, string path, Variant.Type type)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(path);

            return Leaf(data, path) is { } value ? Coerce(value, type) : AuthoredValue.None;
        }

        /// <summary>Walk a slash-separated path through nested tables to its leaf, or null.</summary>
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

            // A TOML integer where a float is wanted is not a mistake: canonical TOML widens 1.0 to
            // 1, so a whole number arrives as a long and refusing it would drop every round value.
            Variant.Type.Int => Integral(value) is { } integral
                ? new AuthoredValue(AuthoredValueKind.Integer, Integer: integral)
                : AuthoredValue.None,

            Variant.Type.Float => Numeric(value) is { } number
                ? new AuthoredValue(AuthoredValueKind.Number, Number: number)
                : AuthoredValue.None,

            Variant.Type.String => value is string text
                ? new AuthoredValue(AuthoredValueKind.Text, Text: text)
                : AuthoredValue.None,

            Variant.Type.Vector2 => Run(value, 2),
            Variant.Type.Vector3 => Run(value, 3),
            Variant.Type.Quaternion => Run(value, 4),
            Variant.Type.Color => Rgba(value),
            _ => AuthoredValue.None,
        };

        /// <summary>A fixed-length float run. WRONG LENGTH IS NOT SHORT: <c>Position = [0, 1.5]</c>
        /// once baked silently as the origin, and a reader that accepted it would put that back.</summary>
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

        /// <summary>A colour, from the <c>{ r, g, b, a }</c> table the contract writes — or from a
        /// four-float array, which is what a hand-edited document tends to contain.</summary>
        private static AuthoredValue Rgba(object value)
        {
            if (value is CanonicalTomlTable table)
            {
                var channels = new float[4];
                // Alpha defaults to opaque: a colour written without one is not transparent.
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

        private static double? Numeric(object value) => value switch
        {
            double number => number,
            long integer => integer,
            _ => null,
        };

        /// <summary>An integer field takes a TOML integer only. A float here would have to round,
        /// and silently rounding an authored value is worse than leaving the default.</summary>
        private static long? Integral(object value) => value is long integer ? integer : null;
    }
}
#endif
