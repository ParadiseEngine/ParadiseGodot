// Vendored from bank-heist's BankHeist.WwiseBridge (native/BankHeist.WwiseBridge/
// BankHeistWwiseBridge.cpp) so this repo can rebuild the bridge against a local Wwise SDK.
// The exported C ABI (bh_wwise_*) is kept verbatim: ParadiseRuntime's WwiseAudio targets it,
// and any bridge built from either repo is drop-in compatible. Build with
// scripts/build-wwise-bridge-macos.sh (PARADISE_WWISE_SDK points at the Wwise SDK root).
#include <AK/SoundEngine/Common/AkMemoryMgrModule.h>
#include <AK/SoundEngine/Common/AkSoundEngine.h>
#include <AK/SoundEngine/Common/AkStreamMgrModule.h>
#include <AK/SoundEngine/Common/IAkStreamMgr.h>
#include <AK/Tools/Common/AkPlatformFuncs.h>

#include <AK/Plugin/AkMeterFXFactory.h>
#include <AK/Plugin/AkOpusDecoderFactory.h>
#include <AK/Plugin/AkVorbisDecoderFactory.h>

#include <AkDefaultIOHookDeferred.h>

#include <unordered_set>

#if defined(AK_APPLE)
#include <AK/SoundEngine/Platforms/Mac/AkMacSoundEngine.h>
#endif

#if defined(_WIN32)
#define BH_WWISE_EXPORT extern "C" __declspec(dllexport)
#else
#define BH_WWISE_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace
{
    constexpr AkGameObjectID DefaultListenerId = 0xFFFF'FFFF'FFFF'0000ULL;

    CAkDefaultIOHookDeferred g_lowLevelIo;
    bool g_memoryInitialized = false;
    bool g_streamInitialized = false;
    bool g_ioInitialized = false;
    bool g_soundEngineInitialized = false;
    bool g_initialized = false;
    std::unordered_set<AkGameObjectID> g_registeredGameObjects;

    int StepFailure(int step, AKRESULT result)
    {
        return -step * 1000 - static_cast<int>(result);
    }

    int BridgeResult(AKRESULT result)
    {
        return result == AK_Success || result == AK_BankAlreadyLoaded ? 0 : static_cast<int>(result);
    }

    int EnsureGameObjectRegistered(AkGameObjectID gameObjectId)
    {
        if (g_registeredGameObjects.find(gameObjectId) != g_registeredGameObjects.end())
        {
            return 0;
        }

        int result = BridgeResult(AK::SoundEngine::RegisterGameObj(gameObjectId, "Bank Heist Audio Object"));
        if (result == 0)
        {
            g_registeredGameObjects.insert(gameObjectId);
        }

        return result;
    }

    void Cleanup()
    {
        if (g_soundEngineInitialized)
        {
            for (AkGameObjectID gameObjectId : g_registeredGameObjects)
            {
                AK::SoundEngine::UnregisterGameObj(gameObjectId);
            }

            g_registeredGameObjects.clear();
            AK::SoundEngine::Term();
            g_soundEngineInitialized = false;
        }

        if (g_ioInitialized)
        {
            g_lowLevelIo.Term();
            g_ioInitialized = false;
        }

        if (g_streamInitialized && AK::IAkStreamMgr::Get())
        {
            AK::IAkStreamMgr::Get()->Destroy();
            g_streamInitialized = false;
        }

        if (g_memoryInitialized)
        {
            AK::MemoryMgr::Term();
            g_memoryInitialized = false;
        }
    }
}

BH_WWISE_EXPORT int bh_wwise_init(const char* soundBankPath)
{
    if (g_initialized)
    {
        return 0;
    }

    AkMemSettings memorySettings;
    AK::MemoryMgr::GetDefaultSettings(memorySettings);
    AKRESULT result = AK::MemoryMgr::Init(&memorySettings);
    if (result != AK_Success)
    {
        return StepFailure(1, result);
    }

    g_memoryInitialized = true;

    AkStreamMgrSettings streamSettings;
    AK::StreamMgr::GetDefaultSettings(streamSettings);
    if (!AK::StreamMgr::Create(streamSettings))
    {
        Cleanup();
        return StepFailure(2, AK_Fail);
    }

    g_streamInitialized = true;

    AkDeviceSettings deviceSettings;
    AK::StreamMgr::GetDefaultDeviceSettings(deviceSettings);
    deviceSettings.bUseStreamCache = true;
    result = g_lowLevelIo.Init(deviceSettings);
    if (result != AK_Success)
    {
        Cleanup();
        return StepFailure(3, result);
    }

    g_ioInitialized = true;

    AkInitSettings initSettings;
    AK::SoundEngine::GetDefaultInitSettings(initSettings);

    AkPlatformInitSettings platformSettings;
    AK::SoundEngine::GetDefaultPlatformInitSettings(platformSettings);
#if defined(AK_APPLE)
    platformSettings.eAudioAPI = AkAudioAPI_AudioUnit;
#endif

    result = AK::SoundEngine::Init(&initSettings, &platformSettings);
    if (result != AK_Success)
    {
        Cleanup();
        return StepFailure(4, result);
    }

    g_soundEngineInitialized = true;

    AkOSChar basePath[AK_MAX_PATH];
    AK_UTF8_TO_OSCHAR(basePath, soundBankPath, AK_MAX_PATH);
    result = g_lowLevelIo.SetBasePath(basePath);
    if (result != AK_Success)
    {
        Cleanup();
        return StepFailure(5, result);
    }

    if (AK::StreamMgr::SetCurrentLanguage(AKTEXT("English(US)")) != AK_Success)
    {
        Cleanup();
        return StepFailure(6, AK_Fail);
    }

    result = AK::SoundEngine::RegisterGameObj(DefaultListenerId, "Bank Heist Listener");
    if (result != AK_Success)
    {
        Cleanup();
        return StepFailure(7, result);
    }

    g_registeredGameObjects.insert(DefaultListenerId);
    AK::SoundEngine::SetDefaultListeners(&DefaultListenerId, 1);
    g_initialized = true;
    return 0;
}

BH_WWISE_EXPORT int bh_wwise_load_bank(const char* bankName)
{
    if (!g_initialized)
    {
        return -1;
    }

    AkBankID bankId;
    return BridgeResult(AK::SoundEngine::LoadBank(bankName, bankId));
}

BH_WWISE_EXPORT int bh_wwise_render_audio()
{
    if (!g_initialized)
    {
        return -1;
    }

    AK::SoundEngine::RenderAudio();
    return 0;
}

BH_WWISE_EXPORT int bh_wwise_post_event(const char* eventName, AkGameObjectID gameObjectId)
{
    if (!g_initialized)
    {
        return -1;
    }

    int result = EnsureGameObjectRegistered(gameObjectId);
    if (result != 0)
    {
        return result;
    }

    AkPlayingID playingId = AK::SoundEngine::PostEvent(eventName, gameObjectId);
    return playingId == AK_INVALID_PLAYING_ID ? static_cast<int>(AK_Fail) : 0;
}

BH_WWISE_EXPORT int bh_wwise_set_rtpc_value(const char* rtpcName, AkRtpcValue value, AkGameObjectID gameObjectId)
{
    if (!g_initialized)
    {
        return -1;
    }

    int result = EnsureGameObjectRegistered(gameObjectId);
    if (result != 0)
    {
        return result;
    }

    return BridgeResult(AK::SoundEngine::SetRTPCValue(rtpcName, value, gameObjectId));
}

BH_WWISE_EXPORT int bh_wwise_set_master_volume(AkReal32 volume)
{
    if (!g_initialized)
    {
        return -1;
    }

    return BridgeResult(AK::SoundEngine::SetOutputVolume(0, volume));
}

BH_WWISE_EXPORT int bh_wwise_set_switch(const char* switchGroup, const char* switchState, AkGameObjectID gameObjectId)
{
    if (!g_initialized)
    {
        return -1;
    }

    int result = EnsureGameObjectRegistered(gameObjectId);
    if (result != 0)
    {
        return result;
    }

    return BridgeResult(AK::SoundEngine::SetSwitch(switchGroup, switchState, gameObjectId));
}

BH_WWISE_EXPORT void bh_wwise_term()
{
    if (!g_initialized)
    {
        return;
    }

    g_initialized = false;
    Cleanup();
}
