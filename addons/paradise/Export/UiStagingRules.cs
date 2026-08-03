#if TOOLS
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ParadiseGodot.Export
{
    /// <summary>What a XAML attribute points at: a concrete file (an image, a nested XAML) or the
    /// FOLDER half of a NoesisGUI font reference (<c>FontFamily="Fonts/#PT Root UI"</c> names a
    /// directory to scan, not a file — the family name inside it is a font-table lookup we cannot
    /// resolve without Noesis).</summary>
    internal enum UiReferenceKind
    {
        File,
        FontFolder,
    }

    /// <summary>One reference found in a XAML file, as a forward-slashed relative path.</summary>
    internal readonly record struct UiReference(string Path, UiReferenceKind Kind);

    /// <summary>The pure decision rules behind UI asset staging: which files ship, which
    /// directories are authoring-only, and what a XAML file references.
    ///
    /// Deliberately free of Godot AND Noesis types. The addon may only depend on Godot and
    /// Paradise.Export (scripts/check_addon_deps.sh), so reference "validation" here is a
    /// best-effort regex lint rather than a real XAML parse — it catches the mistake that
    /// actually happens (an asset left behind in the authoring folder) and never blocks an
    /// export. Being engine-free also lets Paradise.Sample.Runtime.Tests link this file
    /// directly and unit-test the rules without a Godot runtime.</summary>
    internal static class UiStagingRules
    {
        /// <summary>Runtime-relevant UI assets: XAML markup, fonts, and the image formats
        /// NoesisGUI's texture provider can load from disk.</summary>
        private static readonly string[] StagedExtensions =
            [".xaml", ".ttf", ".otf", ".png", ".jpg", ".jpeg", ".svg"];

        private static readonly string[] FontExtensions = [".ttf", ".otf"];

        /// <summary>Noesis Studio's design-time sidecar folder — authoring state (layouts, undo
        /// history, previews), never shipped.</summary>
        public const string StudioFolderName = ".noesis";

        /// <summary>Matches <c>Source="…"</c> and its prefixed variants (<c>ImageSource</c>,
        /// <c>UriSource</c>, <c>BitmapSource</c>, …) — the attribute family that names a file.</summary>
        private static readonly Regex SourceAttribute =
            new(@"\b\w*Source\s*=\s*""([^""]*)""", RegexOptions.Compiled);

        private static readonly Regex FontFamilyAttribute =
            new(@"\bFontFamily\s*=\s*""([^""]*)""", RegexOptions.Compiled);

        /// <summary>True when a file belongs in the staged tree. Hidden files and Noesis Studio
        /// project sidecars (<c>*.noesis</c>) are authoring-only and never ship.</summary>
        public static bool ShouldStageFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
            {
                return false;
            }

            foreach (string extension in StagedExtensions)
            {
                if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True for directories the walk must not descend into: the Noesis Studio
        /// sidecar folder and every other hidden directory (<c>.git</c>, Godot's <c>.import</c>
        /// caches, …).</summary>
        public static bool ShouldSkipDirectory(string directoryName) =>
            string.IsNullOrWhiteSpace(directoryName) || directoryName.StartsWith('.');

        public static bool IsFontFile(string fileName)
        {
            foreach (string extension in FontExtensions)
            {
                if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Best-effort scan of one XAML document for the assets it references, in
        /// document order and de-duplicated. Values that are not file paths — markup extensions
        /// (<c>{Binding …}</c>), absolute URIs, and bare system font families — are dropped, so a
        /// caller can treat everything returned as "must exist in the staged tree".</summary>
        public static IReadOnlyList<UiReference> ScanReferences(string xaml)
        {
            var references = new List<UiReference>();
            if (string.IsNullOrEmpty(xaml))
            {
                return references;
            }

            foreach (Match match in SourceAttribute.Matches(xaml))
            {
                if (NormalizeFilePath(match.Groups[1].Value) is { } path)
                {
                    Add(references, new UiReference(path, UiReferenceKind.File));
                }
            }

            foreach (Match match in FontFamilyAttribute.Matches(xaml))
            {
                // A family list may mix folder-qualified and system fonts:
                // FontFamily="Fonts/#PT Root UI, Arial" — only the former names a directory.
                foreach (string entry in match.Groups[1].Value.Split(','))
                {
                    int separator = entry.IndexOf("/#", StringComparison.Ordinal);
                    if (separator < 0)
                    {
                        continue;
                    }

                    if (NormalizeFilePath(entry[..separator]) is { } folder)
                    {
                        Add(references, new UiReference(folder, UiReferenceKind.FontFolder));
                    }
                }
            }

            return references;
        }

        private static void Add(List<UiReference> references, UiReference reference)
        {
            if (!references.Contains(reference))
            {
                references.Add(reference);
            }
        }

        /// <summary>Normalize an attribute value to a forward-slashed relative path, or null when
        /// it does not name a file in the staged tree.</summary>
        private static string? NormalizeFilePath(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith('{') ||   // {Binding …}, {StaticResource …}
                trimmed.StartsWith('#') ||   // font family with no folder
                trimmed.Contains("://", StringComparison.Ordinal)) // http://, pack://, res://
            {
                return null;
            }

            string normalized = trimmed.Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            return normalized.Length == 0 ? null : normalized;
        }
    }
}
#endif
