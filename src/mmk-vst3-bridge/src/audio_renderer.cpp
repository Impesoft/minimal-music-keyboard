#include "audio_renderer.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <vector>

#include <Windows.h>

#include "host_application.h"
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivstmidicontrollers.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <public.sdk/source/common/memorystream.h>
#include <public.sdk/source/vst/hosting/eventlist.h>
#include <public.sdk/source/vst/vstpresetfile.h>

using Steinberg::Vst::Event;

namespace
{
    constexpr int kEventBusIndex = 0;
    constexpr int kEventChannel = 0;

    std::string FormatTResult(Steinberg::tresult result)
    {
        switch (result)
        {
        case Steinberg::kResultOk:
            return "kResultOk";
        case Steinberg::kNoInterface:
            return "kNoInterface";
        case Steinberg::kResultFalse:
            return "kResultFalse";
        case Steinberg::kInvalidArgument:
            return "kInvalidArgument";
        case Steinberg::kNotImplemented:
            return "kNotImplemented";
        default:
            {
                std::ostringstream stream;
                stream << "0x" << std::hex << std::uppercase << static_cast<std::uint32_t>(result);
                return stream.str();
            }
        }
    }

    std::string FormatTuid(const Steinberg::TUID tuid)
    {
        std::ostringstream stream;
        stream << std::hex << std::uppercase << std::setfill('0');
        for (int i = 0; i < 16; ++i)
        {
            stream << std::setw(2) << static_cast<int>(static_cast<unsigned char>(tuid[i]));
            if (i == 3 || i == 5 || i == 7 || i == 9)
                stream << '-';
        }

        return stream.str();
    }

    std::string JoinDiagnostics(const std::vector<std::string>& diagnostics)
    {
        std::ostringstream stream;
        for (std::size_t i = 0; i < diagnostics.size(); ++i)
        {
            if (i > 0)
                stream << ' ';
            stream << diagnostics[i];
        }

        return stream.str();
    }

    std::string FormatLastErrorMessage(DWORD error)
    {
        if (error == 0)
            return "Win32 error 0";

        wchar_t* buffer = nullptr;
        const DWORD flags = FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS;
        const DWORD length = FormatMessageW(
            flags,
            nullptr,
            error,
            MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
            reinterpret_cast<LPWSTR>(&buffer),
            0,
            nullptr);

        std::ostringstream stream;
        stream << "Win32 error " << error;
        if (length > 0 && buffer != nullptr)
        {
            std::wstring message(buffer, length);
            while (!message.empty() && (message.back() == L'\r' || message.back() == L'\n'))
                message.pop_back();

            stream << " (" << std::string(message.begin(), message.end()) << ")";
        }

        if (buffer != nullptr)
            LocalFree(buffer);

        return stream.str();
    }

    void SyncControllerStateFromComponent(
        Steinberg::Vst::IComponent* component,
        Steinberg::Vst::IEditController* controller,
        std::vector<std::string>& diagnostics)
    {
        if (component == nullptr || controller == nullptr)
            return;

        Steinberg::MemoryStream componentStateStream;
        const auto getStateResult = component->getState(&componentStateStream);
        if (getStateResult != Steinberg::kResultOk)
        {
            diagnostics.emplace_back(
                "component getState() for controller sync failed (" + FormatTResult(getStateResult) + ").");
            return;
        }

        componentStateStream.seek(0, Steinberg::IBStream::kIBSeekSet, nullptr);
        const auto setComponentStateResult = controller->setComponentState(&componentStateStream);
        if (setComponentStateResult == Steinberg::kResultOk)
        {
            diagnostics.emplace_back("controller state synchronized from component.");
        }
        else
        {
            diagnostics.emplace_back(
                "controller setComponentState() failed (" + FormatTResult(setComponentStateResult) + ").");
        }
    }

    unsigned int TrySyncControllerStateFromComponentWithStructuredExceptionGuard(
        Steinberg::Vst::IComponent* component,
        Steinberg::Vst::IEditController* controller,
        std::vector<std::string>* diagnostics)
    {
#if defined(_MSC_VER)
        __try
        {
            SyncControllerStateFromComponent(component, controller, *diagnostics);
            return 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            return GetExceptionCode();
        }
#else
        SyncControllerStateFromComponent(component, controller, *diagnostics);
        return 0;
#endif
    }

    class EditorPlugFrame final : public Steinberg::IPlugFrame
    {
    public:
        explicit EditorPlugFrame(HWND hwnd)
            : hwnd_(hwnd)
        {
        }

        Steinberg::tresult PLUGIN_API resizeView(Steinberg::IPlugView* view, Steinberg::ViewRect* newSize) override
        {
            if (hwnd_ == nullptr || newSize == nullptr)
                return Steinberg::kInvalidArgument;

            RECT rect{ newSize->left, newSize->top, newSize->right, newSize->bottom };
            if (!AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE))
                return Steinberg::kResultFalse;

            const int width = rect.right - rect.left;
            const int height = rect.bottom - rect.top;
            if (!SetWindowPos(hwnd_, nullptr, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE))
                return Steinberg::kResultFalse;

            return view != nullptr ? view->onSize(newSize) : Steinberg::kResultOk;
        }

        Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** obj) override
        {
            if (Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::IPlugFrame::iid))
            {
                addRef();
                *obj = static_cast<Steinberg::IPlugFrame*>(this);
                return Steinberg::kResultOk;
            }
            if (Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::FUnknown::iid))
            {
                addRef();
                *obj = static_cast<Steinberg::IPlugFrame*>(this);
                return Steinberg::kResultOk;
            }

            *obj = nullptr;
            return Steinberg::kNoInterface;
        }

        Steinberg::uint32 PLUGIN_API addRef() override
        {
            return ++refCount_;
        }

        Steinberg::uint32 PLUGIN_API release() override
        {
            const auto result = --refCount_;
            if (result == 0)
                delete this;
            return result;
        }

    private:
        std::atomic<Steinberg::uint32> refCount_{ 1 };
        HWND hwnd_{ nullptr };
    };

    LRESULT CALLBACK EditorHostWindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
        case WM_SIZE:
            {
                const auto clientHwnd = static_cast<HWND>(GetPropW(hwnd, L"MmkClient"));
                if (clientHwnd != nullptr)
                    MoveWindow(clientHwnd, 0, 0, LOWORD(lParam), HIWORD(lParam), TRUE);
                return 0;
            }
        case WM_CLOSE:
            {
                // User clicked the X button — clean up the VST3 view properly
                // instead of letting DefWindowProc call DestroyWindow directly.
                auto* renderer = static_cast<AudioRenderer*>(GetPropW(hwnd, L"MmkRenderer"));
                if (renderer)
                    renderer->CloseEditor();
                return 0;
            }
        case WM_NCDESTROY:
            RemovePropW(hwnd, L"MmkClient");
            RemovePropW(hwnd, L"MmkRenderer");
            break;
        default:
            break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    void TryActivateBus(
        Steinberg::Vst::IComponent* component,
        Steinberg::Vst::MediaType mediaType,
        Steinberg::Vst::BusDirection direction,
        Steinberg::int32 index,
        std::vector<std::string>& diagnostics)
    {
        if (component == nullptr)
            return;

        const auto busCount = component->getBusCount(mediaType, direction);
        if (busCount <= index)
        {
            diagnostics.emplace_back(
                "bus activation skipped for " +
                std::string(mediaType == Steinberg::Vst::kAudio ? "audio" : "event") + ' ' +
                std::string(direction == Steinberg::Vst::kInput ? "input" : "output") +
                " bus " + std::to_string(index) + " because only " + std::to_string(busCount) +
                " bus(es) exist.");
            return;
        }

        const auto activateResult = component->activateBus(mediaType, direction, index, true);
        diagnostics.emplace_back(
            "activateBus(" +
            std::string(mediaType == Steinberg::Vst::kAudio ? "audio" : "event") + ", " +
            std::string(direction == Steinberg::Vst::kInput ? "input" : "output") + ", " +
            std::to_string(index) + ") returned " + FormatTResult(activateResult) + ".");
    }
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
    SetLoadStage(LoadStage::ResettingState);
    ResetPluginState();
    supportsEditor_ = false;
    controllerSharesComponent_ = false;
    editorDiagnostics_ = "Editor support not evaluated yet.";

    std::string loadError;
    SetLoadStage(LoadStage::CreatingModule);
    module_ = VST3::Hosting::Module::create(pluginPath, loadError);
    if (!module_)
    {
        errorMessage = loadError.empty() ? "Failed to load VST3 module." : loadError;
        return false;
    }

    SetLoadStage(LoadStage::EnumeratingClasses);
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

    SetLoadStage(LoadStage::CreatingComponent);
    component_ = factory.createInstance<Steinberg::Vst::IComponent>(audioEffectClass->ID());
    if (!component_)
    {
        errorMessage = "Failed to create VST3 component.";
        module_.reset();
        return false;
    }

    hostApp_ = Steinberg::owned(new HostApplication());
    auto* hostContext = static_cast<Steinberg::Vst::IHostApplication*>(hostApp_.get());
    SetLoadStage(LoadStage::InitializingComponent);
    if (component_->initialize(hostContext) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to initialize VST3 component.";
        ResetPluginState();
        return false;
    }

    // Obtain IEditController — VST3 supports two patterns:
    //   1. Single-object: component directly implements IEditController (queryInterface succeeds)
    //   2. Separate-object: component and controller are distinct classes; get the controller
    //      class ID via getControllerClassId() and instantiate it from the factory.
    // We try (1) first, then fall back to (2). Either way, controller_ may remain null
    // for plugins that genuinely have no GUI.
    std::vector<std::string> loadDiagnostics;

    {
        std::vector<std::string> editorDiagnostics;
        editorDiagnostics.emplace_back("Editor controller discovery:");
        SetLoadStage(LoadStage::DiscoveringController);
        Steinberg::Vst::IEditController* controllerRaw = nullptr;
        const auto directQueryResult = component_->queryInterface(
            Steinberg::Vst::IEditController::iid,
            reinterpret_cast<void**>(&controllerRaw));
        if (directQueryResult == Steinberg::kResultOk && controllerRaw != nullptr)
        {
            // Pattern 1: component IS the controller
            controller_ = Steinberg::owned(controllerRaw);
            controllerSharesComponent_ = true;
            editorDiagnostics.emplace_back("direct IEditController query succeeded.");
        }
        else
        {
            editorDiagnostics.emplace_back(
                "direct IEditController query failed (" + FormatTResult(directQueryResult) + ").");

            // Pattern 2: separate controller class
            Steinberg::TUID controllerCID{};
            const auto controllerClassResult = component_->getControllerClassId(controllerCID);
            if (controllerClassResult == Steinberg::kResultOk)
            {
                editorDiagnostics.emplace_back(
                    "controller class ID lookup succeeded (" + FormatTuid(controllerCID) + ").");

                controllerRaw = nullptr;
                const auto controllerCreateResult = factory.get()->createInstance(
                    controllerCID,
                    Steinberg::Vst::IEditController::iid,
                    reinterpret_cast<void**>(&controllerRaw));

                if (controllerCreateResult == Steinberg::kResultOk && controllerRaw != nullptr)
                {
                    controller_ = Steinberg::owned(controllerRaw);
                    controllerSharesComponent_ = false;
                    editorDiagnostics.emplace_back("factory instantiated separate controller successfully.");
                }
                else
                {
                    editorDiagnostics.emplace_back(
                        "factory createInstance for controller failed (" + FormatTResult(controllerCreateResult) + ").");
                }
            }
            else
            {
                editorDiagnostics.emplace_back(
                    "controller class ID lookup failed (" + FormatTResult(controllerClassResult) + ").");
            }
        }

        if (controller_)
        {
            if (controllerSharesComponent_)
            {
                supportsEditor_ = true;
                editorDiagnostics.emplace_back("controller shares the component instance, so no second initialize() call was made.");
                SetLoadStage(LoadStage::SettingComponentHandler);
                const auto setComponentHandlerResult =
                    controller_->setComponentHandler(static_cast<Steinberg::Vst::IComponentHandler*>(hostApp_.get()));
                if (setComponentHandlerResult == Steinberg::kResultOk)
                {
                    editorDiagnostics.emplace_back("controller setComponentHandler() succeeded.");
                }
                else
                {
                    editorDiagnostics.emplace_back(
                        "controller setComponentHandler() failed (" + FormatTResult(setComponentHandlerResult) + ").");
                }
            }
            else
            {
                SetLoadStage(LoadStage::InitializingController);
                const auto controllerInitializeResult = controller_->initialize(hostContext);
                if (controllerInitializeResult != Steinberg::kResultOk)
                {
                    controller_ = nullptr;
                    editorDiagnostics.emplace_back(
                        "controller initialize() failed (" + FormatTResult(controllerInitializeResult) + ").");
                }
                else
                {
                    supportsEditor_ = true;
                    editorDiagnostics.emplace_back("controller initialize() succeeded.");
                    SetLoadStage(LoadStage::SettingComponentHandler);
                    const auto setComponentHandlerResult =
                        controller_->setComponentHandler(static_cast<Steinberg::Vst::IComponentHandler*>(hostApp_.get()));
                    if (setComponentHandlerResult == Steinberg::kResultOk)
                    {
                        editorDiagnostics.emplace_back("controller setComponentHandler() succeeded.");
                    }
                    else
                    {
                        editorDiagnostics.emplace_back(
                            "controller setComponentHandler() failed (" + FormatTResult(setComponentHandlerResult) + ").");
                    }
                    SetLoadStage(LoadStage::SyncingControllerState);
                    const auto controllerStateSyncExceptionCode =
                        TrySyncControllerStateFromComponentWithStructuredExceptionGuard(
                            component_.get(),
                            controller_.get(),
                            &editorDiagnostics);
                    if (controllerStateSyncExceptionCode != 0)
                    {
                        editorDiagnostics.emplace_back(
                            "controller state synchronization crashed with structured exception " +
                            std::string("0x") +
                            [&controllerStateSyncExceptionCode]()
                            {
                                std::ostringstream stream;
                                stream << std::hex << std::uppercase << controllerStateSyncExceptionCode;
                                return stream.str();
                            }() +
                            "; continuing without controller state sync.");
                    }
                }
            }

            // Connect component and controller via IConnectionPoint if supported
            if (supportsEditor_ && !controllerSharesComponent_)
            {
                SetLoadStage(LoadStage::ConnectingController);
                Steinberg::Vst::IConnectionPoint* compCP = nullptr;
                Steinberg::Vst::IConnectionPoint* ctrlCP = nullptr;
                if (component_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&compCP)) == Steinberg::kResultOk &&
                    controller_->queryInterface(Steinberg::Vst::IConnectionPoint::iid, reinterpret_cast<void**>(&ctrlCP)) == Steinberg::kResultOk)
                {
                    compCP->connect(ctrlCP);
                    ctrlCP->connect(compCP);
                    editorDiagnostics.emplace_back("component/controller connection points connected.");
                }
                else
                {
                    editorDiagnostics.emplace_back("component/controller connection points unavailable; continuing without connection.");
                }

                if (compCP) compCP->release();
                if (ctrlCP) ctrlCP->release();
            }
        }

        if (!supportsEditor_)
            editorDiagnostics.emplace_back("editor GUI unavailable after controller discovery.");

        editorDiagnostics_ = JoinDiagnostics(editorDiagnostics);
    }

    SetLoadStage(LoadStage::QueryingProcessor);
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

    SetLoadStage(LoadStage::SettingUpProcessing);
    if (processor_->setupProcessing(setup) != Steinberg::kResultOk)
    {
        errorMessage = "VST3 processor setup failed.";
        ResetPluginState();
        return false;
    }

    // Activate only buses that actually exist before calling setActive(true).
    SetLoadStage(LoadStage::ActivatingAudioBus);
    TryActivateBus(component_.get(), Steinberg::Vst::kAudio, Steinberg::Vst::kOutput, 0, loadDiagnostics);
    SetLoadStage(LoadStage::ActivatingEventBus);
    TryActivateBus(component_.get(), Steinberg::Vst::kEvent, Steinberg::Vst::kInput, 0, loadDiagnostics);

    SetLoadStage(LoadStage::ActivatingComponent);
    if (component_->setActive(true) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to activate VST3 component. " + JoinDiagnostics(loadDiagnostics);
        ResetPluginState();
        return false;
    }

    SetLoadStage(LoadStage::StartingProcessing);
    if (processor_->setProcessing(true) != Steinberg::kResultOk)
    {
        errorMessage = "Failed to start VST3 processing. " + JoinDiagnostics(loadDiagnostics);
        ResetPluginState();
        return false;
    }

    if (!presetPath.empty())
    {
        SetLoadStage(LoadStage::LoadingPreset);
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
            else if (controller_ && !controllerSharesComponent_)
            {
                std::vector<std::string> presetDiagnostics;
                const auto controllerStateSyncExceptionCode =
                    TrySyncControllerStateFromComponentWithStructuredExceptionGuard(
                        component_.get(),
                        controller_.get(),
                        &presetDiagnostics);
                if (controllerStateSyncExceptionCode != 0)
                {
                    std::ostringstream stream;
                    stream << std::hex << std::uppercase << controllerStateSyncExceptionCode;
                    presetDiagnostics.emplace_back(
                        "controller state synchronization after preset load crashed with structured exception 0x" +
                        stream.str() + "; continuing without controller state sync.");
                }
                if (!presetDiagnostics.empty())
                {
                    editorDiagnostics_ += " " + JoinDiagnostics(presetDiagnostics);
                }
            }
        }
        else
        {
            std::cerr << "[AudioRenderer] VST3 preset file not found: " << presetPath << "\n";
        }
    }

    SetLoadStage(LoadStage::Completed);
    return true;
}

void AudioRenderer::Unload()
{
    std::lock_guard<std::mutex> lock(pluginMutex_);
    ResetPluginState();
}

void AudioRenderer::ResetPluginState()
{
    SetLoadStage(LoadStage::Idle);
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
        bool hasCtrlCP = !controllerSharesComponent_ && controller_ &&
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
        if (!controllerSharesComponent_)
            controller_->terminate();
    }

    processor_ = nullptr;
    controller_ = nullptr;
    component_ = nullptr;
    hostApp_ = nullptr;
    controllerSharesComponent_ = false;
    supportsEditor_ = false;
    module_.reset();

    {
        std::lock_guard<std::mutex> eventsLock(eventsMutex_);
        pendingEvents_.clear();
    }
}

const char* AudioRenderer::GetLoadStageDescription() const
{
    switch (static_cast<LoadStage>(loadStage_.load()))
    {
    case LoadStage::Idle:
        return "before VST3 load started";
    case LoadStage::ResettingState:
        return "while resetting prior plugin state";
    case LoadStage::CreatingModule:
        return "while loading the VST3 module from disk";
    case LoadStage::EnumeratingClasses:
        return "while enumerating VST3 factory classes";
    case LoadStage::CreatingComponent:
        return "while creating the VST3 component instance";
    case LoadStage::InitializingComponent:
        return "while initializing the VST3 component";
    case LoadStage::DiscoveringController:
        return "while discovering the VST3 edit controller";
    case LoadStage::InitializingController:
        return "while initializing the VST3 edit controller";
    case LoadStage::SettingComponentHandler:
        return "while wiring the host component handler";
    case LoadStage::SyncingControllerState:
        return "while synchronizing component state to the controller";
    case LoadStage::ConnectingController:
        return "while connecting component/controller message endpoints";
    case LoadStage::QueryingProcessor:
        return "while querying the VST3 audio processor interface";
    case LoadStage::SettingUpProcessing:
        return "while configuring VST3 processing";
    case LoadStage::ActivatingAudioBus:
        return "while activating the VST3 audio output bus";
    case LoadStage::ActivatingEventBus:
        return "while activating the VST3 event input bus";
    case LoadStage::ActivatingComponent:
        return "while activating the VST3 component";
    case LoadStage::StartingProcessing:
        return "while starting VST3 processing";
    case LoadStage::LoadingPreset:
        return "while loading the preset into the VST3 component";
    case LoadStage::Completed:
        return "after VST3 load completed";
    default:
        return "during an unknown VST3 load stage";
    }
}

void AudioRenderer::SetLoadStage(LoadStage stage)
{
    loadStage_.store(static_cast<int>(stage));
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
    ::SetThreadPriority(renderThread_.native_handle(), THREAD_PRIORITY_HIGHEST);
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
        std::chrono::duration<double>(static_cast<double>(frameSize_) / kSampleRate);
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
        errorMessage = editorDiagnostics_;
        return false;
    }

    if (editorOpen_.load())
    {
        if (editorHwnd_) SetForegroundWindow(editorHwnd_);
        return true;
    }

    Steinberg::IPlugView* viewRaw = controller_->createView(Steinberg::Vst::ViewType::kEditor);
    if (!viewRaw)
    {
        errorMessage = "Editor open failed at createView(kEditor): plugin returned null. " + editorDiagnostics_;
        return false;
    }
    plugView_ = Steinberg::owned(viewRaw);

    const auto platformSupportResult = plugView_->isPlatformTypeSupported(Steinberg::kPlatformTypeHWND);
    if (platformSupportResult != Steinberg::kResultOk)
    {
        plugView_ = nullptr;
        errorMessage = "Editor open failed at isPlatformTypeSupported(HWND): " + FormatTResult(platformSupportResult) + ".";
        return false;
    }

    Steinberg::ViewRect rect{};
    plugView_->getSize(&rect);
    int w = rect.getWidth();
    int h = rect.getHeight();
    if (w <= 0) w = 800;
    if (h <= 0) h = 600;

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = EditorHostWindowProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"MmkVst3Editor";
    RegisterClassExW(&wc);

    // AdjustWindowRect: plugin's getSize() returns CLIENT area dimensions.
    RECT winRect{ 0, 0, w, h };
    AdjustWindowRect(&winRect, WS_OVERLAPPEDWINDOW, FALSE);
    const int winW = winRect.right - winRect.left;
    const int winH = winRect.bottom - winRect.top;

    HWND hwnd = CreateWindowExW(
        WS_EX_APPWINDOW,
        L"MmkVst3Editor", L"VST3 Editor",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, winW, winH,
        parentHwnd, nullptr, GetModuleHandleW(nullptr), nullptr);

    if (!hwnd)
    {
        plugView_ = nullptr;
        errorMessage = "Editor open failed at CreateWindowExW: " + FormatLastErrorMessage(GetLastError()) + ".";
        return false;
    }

    editorHwnd_ = hwnd;

    const HWND clientHwnd = CreateWindowExW(
        0, L"STATIC", nullptr,
        WS_CHILD | WS_VISIBLE,
        0, 0, w, h,
        hwnd, nullptr, GetModuleHandleW(nullptr), nullptr);

    if (!clientHwnd)
    {
        const auto err = GetLastError();
        DestroyWindow(hwnd);
        editorHwnd_ = nullptr;
        plugView_ = nullptr;
        errorMessage = "Editor open failed at creating HWND client container: " + FormatLastErrorMessage(err) + ".";
        return false;
    }

    // Store the client HWND and a back-pointer to this renderer so the
    // window proc can handle WM_SIZE (resize child) and WM_CLOSE (clean
    // VST3 teardown) without global state.
    SetPropW(hwnd, L"MmkClient", clientHwnd);
    SetPropW(hwnd, L"MmkRenderer", this);

    plugFrame_ = Steinberg::owned(new EditorPlugFrame(hwnd));
    const auto setFrameResult = plugView_->setFrame(plugFrame_.get());
    if (setFrameResult != Steinberg::kResultOk)
    {
        DestroyWindow(hwnd);
        editorHwnd_ = nullptr;
        plugView_ = nullptr;
        plugFrame_ = nullptr;
        errorMessage = "Editor open failed at IPlugView::setFrame(IPlugFrame): " + FormatTResult(setFrameResult) + ".";
        return false;
    }

    // attached() runs synchronously on the main thread — the same thread
    // that loaded the plugin (Load()) and hosts the Win32 message loop.
    // This is critical for JUCE-based plugins like OB-Xd: JUCE binds its
    // MessageManager to the loading thread, and attached() internally calls
    // callFunctionOnMessageThread().  When run on a *different* thread,
    // JUCE posts to the main thread and blocks — but the main thread was
    // stuck in ReadFile(pipe), causing a deadlock.  The bridge refactor
    // (pipe reader on background thread, main thread running GetMessageW)
    // ensures attached() always runs on the correct thread.
    const auto attachedResult = plugView_->attached(
        reinterpret_cast<void*>(clientHwnd), Steinberg::kPlatformTypeHWND);
    if (attachedResult != Steinberg::kResultOk)
    {
        plugView_->setFrame(nullptr);
        DestroyWindow(hwnd);
        editorHwnd_ = nullptr;
        plugView_ = nullptr;
        plugFrame_ = nullptr;
        errorMessage = "Editor open failed at IPlugView::attached(HWND): " + FormatTResult(attachedResult) + ".";
        return false;
    }

    UpdateWindow(hwnd);
    SetForegroundWindow(hwnd);
    editorOpen_ = true;
    return true;
}

bool AudioRenderer::SupportsEditor() const
{
    return supportsEditor_;
}

const std::string& AudioRenderer::GetEditorDiagnostics() const
{
    return editorDiagnostics_;
}

void AudioRenderer::CloseEditor()
{
    if (!editorOpen_.load())
        return;

    editorOpen_ = false;

    if (plugView_)
    {
        plugView_->setFrame(nullptr);
        plugView_->removed();
        plugView_ = nullptr;
    }
    plugFrame_ = nullptr;

    if (editorHwnd_)
    {
        DestroyWindow(editorHwnd_);
        editorHwnd_ = nullptr;
    }
}
