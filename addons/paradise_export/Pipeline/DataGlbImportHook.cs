#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace ParadiseGodot.Pipeline
{
    /// <summary>
    /// Editor import hook: whenever a GLB under <c>res://data/</c> is (re)imported, transcode its
    /// embedded textures to KTX2 IN PLACE via <see cref="DataGlbConverter"/>, so a model dropped
    /// into <c>data/</c> is runtime-ready without any manual step. Registered by
    /// <c>ParadiseExportPlugin</c> against <c>EditorFileSystem.resources_reimported</c>.
    ///
    /// Loop-safe: conversion is idempotent (an already-KTX2 GLB is not rewritten, so no further
    /// filesystem change is produced), and an <see cref="_inFlight"/> guard blocks synchronous
    /// re-entrancy from the <c>ReimportFiles</c> we trigger.
    /// </summary>
    internal sealed class DataGlbImportHook
    {
        private bool _inFlight;

        public void OnResourcesReimported(string[] resources)
        {
            if (_inFlight || resources is null)
            {
                return;
            }

            _inFlight = true;
            try
            {
                var reimport = new List<string>();
                foreach (string resPath in resources)
                {
                    if (IsDataGlb(resPath) && DataGlbConverter.Convert(resPath, out bool rewritten) && rewritten)
                    {
                        reimport.Add(resPath);
                    }
                }

                if (reimport.Count > 0)
                {
                    // Re-import so the editor picks up the KTX2 payload. Idempotency terminates any
                    // re-fire this triggers (second pass finds KTX2 → not rewritten → no reimport).
                    EditorInterface.Singleton.GetResourceFilesystem().ReimportFiles(reimport.ToArray());
                }
            }
            finally
            {
                _inFlight = false;
            }
        }

        private static bool IsDataGlb(string resPath) =>
            !string.IsNullOrEmpty(resPath) &&
            resPath.StartsWith("res://data/", StringComparison.Ordinal) &&
            (resPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) ||
             resPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase));
    }
}
#endif
