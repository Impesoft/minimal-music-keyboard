#include "audio_renderer.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <iostream>
#include <vector>

#include "host_application.h"
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstmidicontrollers.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <public.sdk/source/vst/hosting/eventlist.h>
#include <public.sdk/source/vst/vstpresetfile.h>

using Steinberg::Vst::Event;

namespace
{
    constexpr int kEventBusIndex = 0;
    constexpr int kEventChannel = 0;
}

AudioRenderer::~AudioRenderer()
{
    Stop();
    Unload();
}

bool AudioRenderer::Load(const std::string& pluginPath, const std::string& presetPath, std::string& errorMessage)
{
    if (pluginPath.empty())
    {
        errorMessage = "Missing VST3 plugin path.";
        return false;
    }

    std::lock_guard<std::mutex> lock(pluginMutex_);
    ResetPluginState();

    std::string loadError;
    module_ = VST3::Hosting::Module::create(pluginPath, loadError);
    if (!module_)
    {
        errorMessage = loadError.empty() ? "Failed to load VST3 module." : loadError;
        return false;
    }

    const auto& factory = module_->getFactory();
    const auto classInfos = factory.classInfos();
    auto audioEffectClass = std::find_if(
        classInfos.begin(),
        classInfos.end(),
        [](const VST3::Hosting::ClassInfo& info)
        {
            return info.category() == Steinberg::Vst::kVstAudioEffectClass;
        });

    if (audioEffectClass == classInfos.end())
    {
        errorMessage = "No VST3 audio effect class found.";
        module_.reset();
        return false;
    }

    component_ = factory.createInstance<Steinberg::Vst::IComponent>(audioEffectClass->ID());
    if (!component_)
    {
        errorMessage = "Failed to create VST3 component.";
        module_.reset();
        return false;
    }

    hostApp_ = Steinberg::owned(new HostApplication());
    if (component_->initialize(hostApp_) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to initialize VST3 component.";
        ResetPluginState();
        return false;
    }

    // Query IEditController from component (may be null for some plugins)
    Steinberg::Vst::IEditController* controllerRaw = nullptr;
    if (component_->queryInterface(Steinberg::Vst::IEditController::iid, reinterpret_cast<void**>(&controllerRaw)) == Steinberg::kResultOk)
    {
        controller_ = Steinberg::owned(controllerRaw);
        controller_->initialize(hostApp_);

        // Connect component and controller via IConnectionPoint if supported
        Steinberg::Vst::IConnectionPoint* compCP = nullptr;
        Steinberg::Vst::IConnectionPoint* ctrlCP = nullptr;
        if (component_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&compCP)) == Steinberg::kResultOk &&
            controller_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&ctrlCP)) == Steinberg::kResultOk)
        {
            compCP->connect(ctrlCP);
            ctrlCP->connect(compCP);
        }
        if (compCP) compCP->release();
        if (ctrlCP) ctrlCP->release();
    }

    Steinberg::Vst::IAudioProcessor* processorRaw = nullptr;
    if (component_->queryInterface(Steinberg::Vst::IAudioProcessor::iid, reinterpret_cast<void**>(&processorRaw)) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to query VST3 audio processor.";
        ResetPluginState();
        return false;
    }

    processor_ = Steinberg::owned(processorRaw);

    Steinberg::Vst::ProcessSetup setup{};
    setup.processMode = Steinberg::Vst::kRealtime;
    setup.symbolicSampleSize = Steinberg::Vst::kSample32;
    setup.sampleRate = static_cast<double>(kSampleRate);
    setup.maxSamplesPerBlock = kMaxBlockSize;

    if (processor_->setupProcessing(setup) != Steinberg::kResultOk)
    {
        errorMessage = "VST3 processor setup failed.";
        ResetPluginState();
        return false;
    }

    if (component_->setActive(true) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to activate VST3 component.";
        ResetPluginState();
        return false;
    }

    if (processor_->setProcessing(true) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to start VST3 processing.";
        ResetPluginState();
        return false;
    }

    if (!presetPath.empty())
    {
        auto* stream = Steinberg::Vst::FileStream::open(presetPath.c_str(), "rb");
        if (stream != nullptr)
        {
            const auto classId = Steinberg::FUID::fromTUID(audioEffectClass->ID().data());
            const bool presetOk = Steinberg::Vst::PresetFile::loadPreset(stream, classId, component_.get());
            stream->release();

            if (!presetOk)
            {
                std::cerr << "[AudioRenderer] VST3 preset load failed: " << presetPath << "\n";
            }
        }
        else
        {
            std::cerr << "[AudioRenderer] VST3 preset file not found: " << presetPath << "\n";
        }
    }

    return true;
}

void AudioRenderer::Unload()
{
    std::lock_guard<std::mutex> lock(pluginMutex_);
    ResetPluginState();
}

void AudioRenderer::ResetPluginState()
{
    if (processor_)
    {
        processor_->setProcessing(false);
    }

    if (component_)
    {
        component_->setActive(false);
    }

    // Disconnect IConnectionPoint before terminate
    {
        Steinberg::Vst::IConnectionPoint* compCP = nullptr;
        Steinberg::Vst::IConnectionPoint* ctrlCP = nullptr;
        bool hasCompCP = component_ && 
            component_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&compCP)) == Steinberg::kResultOk;
        bool hasCtrlCP = controller_ && 
            controller_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&ctrlCP)) == Steinberg::kResultOk;
        
        if (hasCompCP && hasCtrlCP)
        {
            if (compCP) compCP->disconnect(ctrlCP);
            if (ctrlCP) ctrlCP->disconnect(compCP);
        }
        if (compCP) compCP->release();
        if (ctrlCP) ctrlCP->release();
    }

    // Now safe to terminate
    if (component_)
    {
        component_->terminate();
    }

    if (controller_)
    {
        controller_->terminate();
    }

    processor_ = nullptr;
    controller_ = nullptr;
    component_ = nullptr;
    hostApp_ = nullptr;
    module_.reset();

    {
        std::lock_guard<std::mutex> eventsLock(eventsMutex_);
        pendingEvents_.clear();
    }
}

void AudioRenderer::Start(MmfWriter* writer)
{
    if (running_.load() || writer == nullptr)
        return;

    writer_ = writer;
    frameSize_ = writer_->FrameSize();
    if (frameSize_ <= 0)
        frameSize_ = kMaxBlockSize;

    running_ = true;
    renderThread_ = std::thread(&AudioRenderer::RenderLoop, this);
}

void AudioRenderer::Stop()
{
    if (!running_.exchange(false))
        return;

    if (renderThread_.joinable())
        renderThread_.join();
}

void AudioRenderer::QueueNoteOn(int channel, int pitch, int velocity)
{
    (void)channel;
    Event evt{};
    evt.busIndex = kEventBusIndex;
    evt.sampleOffset = 0;
    evt.ppqPosition = 0.0;
    evt.flags = 0;
    evt.type = Event::kNoteOnEvent;
    evt.noteOn.channel = kEventChannel;
    evt.noteOn.pitch = static_cast<Steinberg::int16>(std::clamp(pitch, 0, 127));
    evt.noteOn.velocity = std::clamp(static_cast<float>(velocity) / 127.0f, 0.0f, 1.0f);
    evt.noteOn.tuning = 0.0f;
    evt.noteOn.length = 0;
    evt.noteOn.noteId = -1;

    std::lock_guard<std::mutex> lock(eventsMutex_);
    pendingEvents_.push_back(evt);
}

void AudioRenderer::QueueNoteOff(int channel, int pitch)
{
    (void)channel;
    Event evt{};
    evt.busIndex = kEventBusIndex;
    evt.sampleOffset = 0;
    evt.ppqPosition = 0.0;
    evt.flags = 0;
    evt.type = Event::kNoteOffEvent;
    evt.noteOff.channel = kEventChannel;
    evt.noteOff.pitch = static_cast<Steinberg::int16>(std::clamp(pitch, 0, 127));
    evt.noteOff.velocity = 0.0f;
    evt.noteOff.noteId = -1;
    evt.noteOff.tuning = 0.0f;

    std::lock_guard<std::mutex> lock(eventsMutex_);
    pendingEvents_.push_back(evt);
}

void AudioRenderer::QueueNoteOffAll()
{
    std::lock_guard<std::mutex> lock(eventsMutex_);
    pendingEvents_.clear();
    pendingEvents_.reserve(128);

    for (int pitch = 0; pitch < 128; ++pitch)
    {
        Event evt{};
        evt.busIndex = kEventBusIndex;
        evt.sampleOffset = 0;
        evt.ppqPosition = 0.0;
        evt.flags = 0;
        evt.type = Event::kNoteOffEvent;
        evt.noteOff.channel = kEventChannel;
        evt.noteOff.pitch = static_cast<Steinberg::int16>(pitch);
        evt.noteOff.velocity = 0.0f;
        evt.noteOff.noteId = -1;
        evt.noteOff.tuning = 0.0f;
        pendingEvents_.push_back(evt);
    }
}

void AudioRenderer::QueueSetProgram(int program)
{
    // VST3 program change: use kDataEvent with raw MIDI program change (0xC0 + channel, program)
    // kLegacyMIDICCOutEvent is an OUTPUT event (plugin -> host), not an input event.
    // Most VST3 instruments respond to MIDI channel 0 program change via kDataEvent.
    Event evt{};
    evt.busIndex = kEventBusIndex;
    evt.sampleOffset = 0;
    evt.ppqPosition = 0.0;
    evt.flags = Event::kIsLive;
    evt.type = Event::kDataEvent;
    evt.data.type = Steinberg::Vst::DataEvent::kMidiSysEx; // Generic MIDI data
    
    // Raw MIDI program change: status byte (0xC0 + channel) followed by program number
    static thread_local uint8_t midiBytes[2];
    midiBytes[0] = 0xC0 | static_cast<uint8_t>(kEventChannel); // Program change on channel 0
    midiBytes[1] = static_cast<uint8_t>(std::clamp(program, 0, 127));
    
    evt.data.bytes = midiBytes;
    evt.data.size = 2;

    std::lock_guard<std::mutex> lock(eventsMutex_);
    pendingEvents_.push_back(evt);
}

void AudioRenderer::RenderLoop()
{
    if (writer_ == nullptr)
        return;

    std::vector<float> buffer(static_cast<std::size_t>(frameSize_) * 2);
    const auto frameDuration =
        std::chrono::duration<double>(static_cast<double>(kMaxBlockSize) / kSampleRate);
    auto nextTick = std::chrono::steady_clock::now();

    while (running_.load())
    {
        FillBuffer(buffer.data(), frameSize_);
        writer_->WriteFrame(buffer.data(), frameSize_);

        nextTick += std::chrono::duration_cast<std::chrono::steady_clock::duration>(frameDuration);
        std::this_thread::sleep_until(nextTick);
    }
}

void AudioRenderer::FillBuffer(float* output, int frameSize)
{
    if (output == nullptr || frameSize <= 0)
        return;

    std::fill(output, output + (frameSize * 2), 0.0f);

    std::unique_lock<std::mutex> lock(pluginMutex_);
    if (!processor_)
    {
        std::lock_guard<std::mutex> eventsLock(eventsMutex_);
        pendingEvents_.clear();
        return;
    }

    std::vector<Event> events;
    {
        std::lock_guard<std::mutex> eventsLock(eventsMutex_);
        events.swap(pendingEvents_);
    }

    static thread_local Steinberg::Vst::EventList inputEvents(kMaxBlockSize);
    inputEvents.clear();
    for (auto& evt : events)
    {
        inputEvents.addEvent(evt);
    }

    std::array<float, kMaxBlockSize> left{};
    std::array<float, kMaxBlockSize> right{};
    float* channels[2] = { left.data(), right.data() };

    Steinberg::Vst::AudioBusBuffers outputBus{};
    outputBus.numChannels = 2;
    outputBus.channelBuffers32 = channels;
    outputBus.silenceFlags = 0;

    Steinberg::Vst::ProcessData processData{};
    processData.processMode = Steinberg::Vst::kRealtime;
    processData.symbolicSampleSize = Steinberg::Vst::kSample32;
    processData.numSamples = kMaxBlockSize;
    processData.numInputs = 0;
    processData.numOutputs = 1;
    processData.outputs = &outputBus;
    processData.inputEvents = &inputEvents;
    processData.outputEvents = nullptr;
    processData.inputParameterChanges = nullptr;
    processData.outputParameterChanges = nullptr;
    processData.processContext = nullptr;

    processor_->process(processData);
    lock.unlock();

    const int framesToCopy = std::min(frameSize, kMaxBlockSize);
    for (int i = 0; i < framesToCopy; ++i)
    {
        output[(i * 2)] = left[static_cast<std::size_t>(i)];
        output[(i * 2) + 1] = right[static_cast<std::size_t>(i)];
    }
}
