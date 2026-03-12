#include "audio_renderer.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <iostream>
#include <vector>

#include <Windows.h>

#include "host_application.h"
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
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
            return info.category() == kVstAudioEffectClass;
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

    // Activate buses before calling setActive(true) per VST3 spec
    // Return values: kResultOk or kNotImplemented are both acceptable
    component_->activateBus(Steinberg::Vst::kAudio, Steinberg::Vst::kOutput, 0, true);
    component_->activateBus(Steinberg::Vst::kEvent, Steinberg::Vst::kInput, 0, true);

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
    CloseEditor(); // close editor before teardown

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
    processData.numSamples = frameSize;
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

bool AudioRenderer::OpenEditor(HWND parentHwnd, std::string& errorMessage)
{
    if (!controller_)
    {
        errorMessage = "No IEditController — plugin does not support GUI.";
        return false;
    }

    if (editorOpen_.load())
    {
        // Already open — bring to front
        if (editorHwnd_) SetForegroundWindow(editorHwnd_);
        return true;
    }

    Steinberg::IPlugView* viewRaw = controller_->createView(Steinberg::Vst::ViewType::kEditor);
    if (!viewRaw)
    {
        errorMessage = "Plugin returned null for createView(kEditor). No GUI available.";
        return false;
    }
    plugView_ = Steinberg::owned(viewRaw);

    // Check platform support
    if (plugView_->isPlatformTypeSupported(Steinberg::kPlatformTypeHWND) != Steinberg::kResultOk)
    {
        plugView_ = nullptr;
        errorMessage = "Plugin does not support HWND-based GUI.";
        return false;
    }

    // Get preferred size
    Steinberg::ViewRect rect{};
    plugView_->getSize(&rect);
    int w = rect.getWidth();
    int h = rect.getHeight();
    if (w <= 0) w = 800;
    if (h <= 0) h = 600;

    // Win32 rule: window creation, attached(), and message pump must all run on the same thread.
    // Additionally many plugins call SendMessage (or trigger WM_PAINT) from inside attached(),
    // which requires the message pump to be running BEFORE attached() is called.
    // We defer attached() by posting it as WM_APP+1 so it fires after GetMessageW starts.
    auto readyPromise = std::make_shared<std::promise<std::pair<bool, std::string>>>();
    auto readyFuture = readyPromise->get_future();

    editorThread_ = std::thread([this, parentHwnd, w, h, readyPromise]()
    {
        // Register window class on this thread (idempotent)
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"MmkVst3Editor";
        RegisterClassExW(&wc);

        HWND hwnd = CreateWindowExW(
            0, L"MmkVst3Editor", L"VST3 Editor",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            CW_USEDEFAULT, CW_USEDEFAULT, w, h,
            parentHwnd, nullptr, GetModuleHandleW(nullptr), nullptr);

        if (!hwnd)
        {
            readyPromise->set_value({ false, "Failed to create Win32 host window." });
            return;
        }

        editorHwnd_ = hwnd;

        // Post attached() work as a window message so it runs AFTER the pump starts.
        // WM_APP+1 (0x8001) is in the private application range — safe from system conflicts.
        PostMessageW(hwnd, WM_APP + 1, 0, 0);

        // Message loop runs on THIS thread — which owns the window
        MSG msg{};
        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            if (msg.message == WM_APP + 1 && msg.hwnd == hwnd)
            {
                // Pump is running — now call attached()
                if (plugView_->attached(reinterpret_cast<void*>(hwnd), Steinberg::kPlatformTypeHWND) != Steinberg::kResultOk)
                {
                    readyPromise->set_value({ false, "IPlugView::attached() failed." });
                    PostMessageW(hwnd, WM_QUIT, 0, 0);
                    continue;
                }
                editorOpen_ = true;
                readyPromise->set_value({ true, {} });
                continue;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        // Cleanup on editor thread
        editorOpen_ = false;
        if (plugView_)
        {
            plugView_->removed();
            plugView_ = nullptr;
        }
        if (editorHwnd_)
        {
            DestroyWindow(editorHwnd_);
            editorHwnd_ = nullptr;
        }
    });

    // Wait up to 10 seconds for attached() to complete.
    // We use 10s because some complex plugins take a while to initialise their UI.
    if (readyFuture.wait_for(std::chrono::seconds(10)) != std::future_status::ready)
    {
        editorOpen_ = false;
        // DO NOT join — editorThread_ may be stuck inside attached(). Detach to avoid deadlock.
        if (editorHwnd_) PostMessageW(editorHwnd_, WM_QUIT, 0, 0);
        if (editorThread_.joinable()) editorThread_.detach();
        plugView_ = nullptr;
        editorHwnd_ = nullptr;
        errorMessage = "Timed out waiting for editor window (plugin attached() may be blocking).";
        return false;
    }

    auto [ok, error] = readyFuture.get();
    if (!ok)
    {
        if (editorThread_.joinable()) editorThread_.join();
        plugView_ = nullptr;
        errorMessage = error;
        return false;
    }

    return true;
}

void AudioRenderer::CloseEditor()
{
    if (!editorOpen_.load())
        return;

    editorOpen_ = false;

    if (editorHwnd_)
        PostMessageW(editorHwnd_, WM_QUIT, 0, 0);

    if (editorThread_.joinable())
        editorThread_.join();

    // editorThread_ cleans up plugView_ and editorHwnd_ itself;
    // guard here in case the thread exited before cleanup (e.g. window closed by user).
    if (plugView_)
    {
        plugView_->removed();
        plugView_ = nullptr;
    }
    if (editorHwnd_)
    {
        DestroyWindow(editorHwnd_);
        editorHwnd_ = nullptr;
    }
}
