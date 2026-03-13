#pragma once

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "mmf_writer.h"
#include "host_application.h"

#include <pluginterfaces/base/funknown.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstcomponent.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstevents.h>
#include <pluginterfaces/gui/iplugview.h>
#include <public.sdk/source/vst/hosting/module.h>
#include <public.sdk/source/vst/hosting/plugprovider.h>

#include <Windows.h>

/// Hosts a single VST3 plugin: loads, renders audio, and manages the
/// optional editor window.
///
/// **Threading contract (post-refactor):**
/// All editor operations (OpenEditor / CloseEditor) MUST be called from
/// the thread that loaded the plugin (via Load()), which is the bridge's
/// main thread running the Win32 message loop.  The render thread
/// (RenderLoop / FillBuffer) runs independently and only touches the
/// audio processor through pluginMutex_.
class AudioRenderer
{
public:
    AudioRenderer() = default;
    ~AudioRenderer();

    bool Load(const std::string& pluginPath, const std::string& presetPath, std::string& errorMessage);
    void Unload();

    void Start(MmfWriter* writer);
    void Stop();

    void QueueNoteOn(int channel, int pitch, int velocity);
    void QueueNoteOff(int channel, int pitch);
    void QueueNoteOffAll();
    void QueueSetProgram(int program);

    /// Open the plugin's editor window on the calling thread.
    /// The caller MUST be running a Win32 message loop on this thread
    /// (the bridge's main thread).  Returns synchronously.
    bool OpenEditor(HWND parentHwnd, std::string& errorMessage);
    void CloseEditor();
    bool SupportsEditor() const;
    const std::string& GetEditorDiagnostics() const;
    const char* GetLoadStageDescription() const;

    void FillBuffer(float* output, int frameSize);

private:
    enum class LoadStage
    {
        Idle = 0,
        ResettingState,
        CreatingModule,
        EnumeratingClasses,
        CreatingComponent,
        InitializingComponent,
        DiscoveringController,
        InitializingController,
        SettingComponentHandler,
        SyncingControllerState,
        ConnectingController,
        QueryingProcessor,
        SettingUpProcessing,
        ActivatingAudioBus,
        ActivatingEventBus,
        ActivatingComponent,
        StartingProcessing,
        LoadingPreset,
        Completed
    };

    void RenderLoop();
    void ResetPluginState();
    void SetLoadStage(LoadStage stage);

    std::atomic<bool> running_{ false };
    std::thread renderThread_;
    MmfWriter* writer_ = nullptr;
    int frameSize_ = 0;
    std::mutex eventsMutex_;
    std::vector<Steinberg::Vst::Event> pendingEvents_{};
    std::mutex pluginMutex_;
    VST3::Hosting::Module::Ptr module_{};
    Steinberg::IPtr<HostApplication> hostApp_{};
    Steinberg::IPtr<Steinberg::Vst::IComponent> component_{};
    Steinberg::IPtr<Steinberg::Vst::IAudioProcessor> processor_{};
    Steinberg::IPtr<Steinberg::Vst::IEditController> controller_{};
    Steinberg::IPtr<Steinberg::IPlugView> plugView_{};
    Steinberg::IPtr<Steinberg::IPlugFrame> plugFrame_{};
    bool controllerSharesComponent_{ false };
    bool supportsEditor_{ false };
    std::string editorDiagnostics_{ "Editor support not evaluated yet." };
    std::atomic<int> loadStage_{ static_cast<int>(LoadStage::Idle) };
    HWND editorHwnd_{ nullptr };
    std::atomic<bool> editorOpen_{ false };

    static constexpr int kSampleRate = 48'000;
    static constexpr int kMaxBlockSize = 960;
};
