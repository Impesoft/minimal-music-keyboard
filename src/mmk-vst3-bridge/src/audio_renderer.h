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
#include <pluginterfaces/vst/ivstevents.h>
#include <public.sdk/source/vst/hosting/module.h>
#include <public.sdk/source/vst/hosting/plugprovider.h>

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

    void FillBuffer(float* output, int frameSize);

private:
    void RenderLoop();
    void ResetPluginState();

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

    static constexpr int kSampleRate = 48'000;
    static constexpr int kMaxBlockSize = 960;
};
