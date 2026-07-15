using System.Runtime.InteropServices;
using ParadiseGame.Audio;

namespace ParadiseRuntime;

/// <summary>Wwise implementation of <see cref="IAudioSystem"/>, speaking the WwiseBridge C ABI
/// (<c>bh_wwise_*</c> — the bank-heist bridge; a source copy + build script live under
/// <c>native/Paradise.WwiseBridge/</c> for building against a local Wwise SDK). The bridge
/// dylib is resolved from <c>PARADISE_WWISE_BRIDGE</c> (full path) or the OS loader's default
/// search.
///
/// Two halves, mirroring the UI systems: <see cref="Sink"/> runs on the SIM thread and
/// <see cref="Pump"/> on the render thread, driving <c>RenderAudio()</c> once per frame.
/// Wwise's public API is internally thread-safe, but the BRIDGE keeps its own game-object
/// registration bookkeeping which is not — and posts genuinely arrive from two threads (sim
/// game logic, plus render-thread paths like the startup smoke event and the no-UI click
/// path). Every native call is therefore serialized through <see cref="_nativeLock"/>: it
/// protects any bridge build, including dylibs compiled before the bridge gained its own
/// mutex, at a cost that is irrelevant at audio-command rates.
///
/// Startup banks come from <c>PARADISE_WWISE_STARTUP_BANKS</c> (semicolon list, default
/// <c>Init.bnk</c>); <c>PARADISE_WWISE_STARTUP_EVENT</c> optionally posts one event on the
/// first pump as an audibility smoke check.</summary>
internal sealed partial class WwiseAudio : IAudioSystem
{
    private static bool s_resolverRegistered;

    private readonly object _nativeLock = new();
    private bool _initialized;
    private bool _postedStartupEvent;

    public IAudioSink Sink { get; }

    private WwiseAudio() => Sink = new AudioSinkHalf(this);

    /// <summary>Initialize the bridge against a generated-soundbanks directory; null (with a
    /// console note) when the bridge or banks are unavailable — audio is always optional.</summary>
    public static WwiseAudio? TryCreate(string soundBanksPath)
    {
        if (!Directory.Exists(soundBanksPath))
        {
            Console.WriteLine($"[WwiseAudio] SoundBanks path not found: {soundBanksPath} — audio disabled.");
            return null;
        }

        // SetDllImportResolver throws on re-registration for the same assembly — guard so a
        // second TryCreate degrades gracefully instead of crashing outside the catch below.
        if (!s_resolverRegistered)
        {
            NativeLibrary.SetDllImportResolver(typeof(WwiseAudio).Assembly, ResolveBridge);
            s_resolverRegistered = true;
        }
        var audio = new WwiseAudio();
        try
        {
            var result = Native.Init(soundBanksPath);
            if (result != 0)
            {
                Console.WriteLine($"[WwiseAudio] bridge init failed ({result}) — audio disabled.");
                return null;
            }
            audio._initialized = true;

            var banks = (Environment.GetEnvironmentVariable("PARADISE_WWISE_STARTUP_BANKS") ?? "Init.bnk")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var bank in banks)
            {
                result = Native.LoadBank(bank);
                if (result != 0)
                {
                    Console.WriteLine($"[WwiseAudio] bank '{bank}' failed to load ({result}) — audio disabled.");
                    audio.Dispose();
                    return null;
                }
            }

            Console.WriteLine($"[WwiseAudio] initialized — banks: {soundBanksPath} [{string.Join(", ", banks)}]");
            return audio;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"[WwiseAudio] bridge unavailable ({ex.GetType().Name}) — audio disabled. " +
                              "Set PARADISE_WWISE_BRIDGE to the WwiseBridge dylib path.");
            return null;
        }
    }

    private static IntPtr ResolveBridge(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Native.LibraryName) return IntPtr.Zero;
        var configured = Environment.GetEnvironmentVariable("PARADISE_WWISE_BRIDGE");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? NativeLibrary.Load(configured)
            : IntPtr.Zero; // fall back to the default OS search for the logical name
    }

    /// <summary>Render-thread: consume the sim's queued commands and produce audio.</summary>
    public void Pump()
    {
        if (!_initialized) return;
        if (!_postedStartupEvent)
        {
            _postedStartupEvent = true;
            var smoke = Environment.GetEnvironmentVariable("PARADISE_WWISE_STARTUP_EVENT");
            if (!string.IsNullOrWhiteSpace(smoke))
            {
                Sink.PostEvent(smoke);
            }
        }
        lock (_nativeLock)
        {
            if (_initialized) _ = Native.RenderAudio();
        }
    }

    public void Dispose()
    {
        lock (_nativeLock)
        {
            if (!_initialized) return;
            _initialized = false;
            Native.Term();
        }
    }

    private sealed class AudioSinkHalf(WwiseAudio owner) : IAudioSink
    {
        // Wwise game object 0 is invalid and the bridge's default listener sits at the very
        // TOP of the id space (0xFFFF_FFFF_FFFF_0000); the bridge registers ids on demand, so
        // 100 serves as the default 2D emitter. Callers passing explicit source ids own that
        // id space and should avoid 100.
        private const ulong DefaultSource = 100;

        public void PostEvent(string eventName, ulong sourceId = 0)
        {
            int result;
            lock (owner._nativeLock)
            {
                if (!owner._initialized) return;
                result = Native.PostEvent(eventName, sourceId == 0 ? DefaultSource : sourceId);
            }
            if (result != 0)
            {
                Console.WriteLine($"[WwiseAudio] event '{eventName}' failed ({result}).");
            }
        }

        public void SetParameter(string parameterName, float value, ulong sourceId = 0)
        {
            int result;
            lock (owner._nativeLock)
            {
                if (!owner._initialized) return;
                result = Native.SetRtpcValue(parameterName, value, sourceId == 0 ? DefaultSource : sourceId);
            }
            if (result != 0)
            {
                Console.WriteLine($"[WwiseAudio] parameter '{parameterName}' failed ({result}).");
            }
        }

        public void SetSwitch(string switchGroup, string switchState, ulong sourceId = 0)
        {
            int result;
            lock (owner._nativeLock)
            {
                if (!owner._initialized) return;
                result = Native.SetSwitch(switchGroup, switchState, sourceId == 0 ? DefaultSource : sourceId);
            }
            if (result != 0)
            {
                Console.WriteLine($"[WwiseAudio] switch '{switchGroup}={switchState}' failed ({result}).");
            }
        }

        public void Tick(double simTimeSeconds)
        {
            // Wwise needs no sim-side time step; commands apply when the render half pumps.
        }
    }

    private static partial class Native
    {
        public const string LibraryName = "BankHeist.WwiseBridge";

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_init", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int Init(string soundBankPath);

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_load_bank", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int LoadBank(string bankName);

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_render_audio")]
        public static partial int RenderAudio();

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_post_event", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int PostEvent(string eventName, ulong gameObjectId);

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_set_rtpc_value", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int SetRtpcValue(string rtpcName, float value, ulong gameObjectId);

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_set_switch", StringMarshalling = StringMarshalling.Utf8)]
        public static partial int SetSwitch(string switchGroup, string switchState, ulong gameObjectId);

        [LibraryImport(LibraryName, EntryPoint = "bh_wwise_term")]
        public static partial int Term();
    }
}
