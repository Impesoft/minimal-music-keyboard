#include "bridge.h"

#include <iostream>
#include <stdexcept>
#include <sstream>

#include <Ole2.h>
#include <nlohmann/json.hpp>

namespace
{
    std::string FormatStructuredExceptionCode(unsigned int code)
    {
        std::ostringstream stream;
        stream << "0x" << std::hex << std::uppercase << code;
        if (code == EXCEPTION_ACCESS_VIOLATION)
            stream << " (access violation)";
        return stream.str();
    }

    unsigned int TryLoadRendererWithStructuredExceptionGuard(
        AudioRenderer* renderer,
        const std::string* path,
        const std::string* preset,
        std::string* error,
        bool* ok)
    {
#if defined(_MSC_VER)
        __try
        {
            *ok = renderer->Load(*path, *preset, *error);
            return 0;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            *ok = false;
            return GetExceptionCode();
        }
#else
        *ok = renderer->Load(*path, *preset, *error);
        return 0;
#endif
    }

    void TryUnloadRendererNoThrow(AudioRenderer& renderer)
    {
        try
        {
            renderer.Unload();
        }
        catch (const std::exception& ex)
        {
            std::cerr << "[Bridge] Renderer cleanup after load failure also failed: " << ex.what() << "\n";
        }
        catch (...)
        {
            std::cerr << "[Bridge] Renderer cleanup after load failure also failed with an unknown exception.\n";
        }
    }
}

Bridge::Bridge(std::uint32_t hostPid)
    : hostPid_(hostPid)
{
}

LRESULT CALLBACK Bridge::MessageWindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    if (msg == WM_BRIDGE_COMMAND || msg == WM_BRIDGE_PIPE_CLOSED)
    {
        auto* bridge = reinterpret_cast<Bridge*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (bridge)
        {
            bridge->DrainCommandQueue();
            if (bridge->shutdownRequested_.load())
                PostQuitMessage(0);
        }
        return 0;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

void Bridge::Run()
{
    if (!ipc_.Connect(hostPid_))
        throw std::runtime_error("Failed to connect to named pipe.");

    if (!mmfWriter_.Open(hostPid_))
        throw std::runtime_error("Failed to open shared memory buffer.");

    renderer_.Start(&mmfWriter_);

    // Initialize OLE/STA on the main thread — required for VST3 editor windows.
    // Many JUCE-based plugins bind their MessageManager to the thread that first
    // initialises COM/OLE, so this MUST happen on the thread that will later
    // host the editor window (i.e. the main thread running the message loop).
    const HRESULT oleResult = OleInitialize(nullptr);
    const bool shouldOleUninitialize = oleResult == S_OK || oleResult == S_FALSE;

    // Hidden message-only window for inter-thread command dispatch.
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = MessageWindowProc;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"MmkBridgeMsg";
    RegisterClassExW(&wc);

    messageHwnd_ = CreateWindowExW(
        0, L"MmkBridgeMsg", nullptr, 0,
        0, 0, 0, 0,
        HWND_MESSAGE, nullptr, GetModuleHandleW(nullptr), nullptr);

    if (messageHwnd_)
        SetWindowLongPtrW(messageHwnd_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));

    // Pipe reading runs on a background thread; it posts WM_BRIDGE_COMMAND
    // to the main thread's message loop whenever a command arrives.
    pipeReaderThread_ = std::thread(&Bridge::PipeReaderLoop, this);

    // Main thread runs the Win32 message loop.  All VST3 plugin loads and
    // editor window operations happen here, on the same STA thread.
    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    // Close the pipe so the reader thread unblocks from ReadFile.
    // (When the pipe comes from host disconnecting, the reader already
    // exited and this is a harmless double-close.)
    ipc_.Close();

    if (pipeReaderThread_.joinable())
        pipeReaderThread_.join();

    Shutdown();

    if (messageHwnd_)
    {
        DestroyWindow(messageHwnd_);
        messageHwnd_ = nullptr;
    }

    if (shouldOleUninitialize)
        OleUninitialize();
}

void Bridge::PipeReaderLoop()
{
    std::string line;
    while (!shutdownRequested_.load())
    {
        if (!ipc_.ReadLine(line))
            break;

        if (line.empty())
            continue;

        {
            std::lock_guard<std::mutex> lock(commandMutex_);
            commandQueue_.push(std::move(line));
            commandPending_ = true;
            line.clear();
        }

        if (messageHwnd_)
            PostMessageW(messageHwnd_, WM_BRIDGE_COMMAND, 0, 0);

        {
            std::unique_lock<std::mutex> lock(commandMutex_);
            commandProcessedCv_.wait(lock, [this]() { return !commandPending_ || shutdownRequested_.load(); });
        }
    }

    // Pipe closed or shutdown — tell the main thread to exit.
    shutdownRequested_ = true;
    if (messageHwnd_)
        PostMessageW(messageHwnd_, WM_BRIDGE_PIPE_CLOSED, 0, 0);
}

void Bridge::DrainCommandQueue()
{
    std::queue<std::string> local;
    {
        std::lock_guard<std::mutex> lock(commandMutex_);
        local.swap(commandQueue_);
    }

    while (!local.empty())
    {
        HandleCommand(local.front());
        local.pop();

        {
            std::lock_guard<std::mutex> lock(commandMutex_);
            commandPending_ = false;
        }
        commandProcessedCv_.notify_all();

        if (shutdownRequested_.load())
            break;
    }
}

void Bridge::Shutdown()
{
    if (shutdownRequested_.exchange(true))
        return;

    renderer_.Stop();
    renderer_.Unload();
    mmfWriter_.Close();
    ipc_.Close();
}

void Bridge::HandleCommand(const std::string& line)
{
    nlohmann::json message;
    try
    {
        message = nlohmann::json::parse(line);
    }
    catch (const std::exception& ex)
    {
        std::cerr << "[Bridge] Failed to parse command JSON: " << ex.what() << "\n";
        return;
    }

    const auto cmd = message.value("cmd", std::string());
    const auto writeAck = [this](nlohmann::json&& ack)
    {
        const auto payload = ack.dump();
        if (!ipc_.WriteLine(payload))
            std::cerr << "[Bridge] Failed to write ACK: " << payload << "\n";
    };
    const auto writeLoadAck = [&writeAck](bool ok, const std::string& error, bool supportsEditor, const std::string& editorDiagnostics)
    {
        nlohmann::json ack;
        ack["ack"] = "load_ack";
        ack["ok"] = ok;
        ack["supportsEditor"] = supportsEditor;
        ack["editorDiagnostics"] = editorDiagnostics;
        if (!ok)
            ack["error"] = error.empty() ? "Failed to load VST3 plugin." : error;

        writeAck(std::move(ack));
    };

    try
    {
        if (cmd == "load")
        {
            const auto path = message.value("path", std::string());
            const auto preset = message.value("preset", std::string());

            std::string error;
            bool ok = false;
            const auto structuredExceptionCode =
                TryLoadRendererWithStructuredExceptionGuard(&renderer_, &path, &preset, &error, &ok);
            if (structuredExceptionCode != 0)
            {
                error = "Native structured exception during VST3 load: " +
                    FormatStructuredExceptionCode(structuredExceptionCode) + " " +
                    renderer_.GetLoadStageDescription() + ".";
                std::cerr << "[Bridge] " << error << "\n";
                writeLoadAck(false, error, false, error);
                return;
            }
            bool supportsEditor = false;
            std::string editorDiagnostics;
            if (ok)
            {
                supportsEditor = renderer_.SupportsEditor();
                editorDiagnostics = renderer_.GetEditorDiagnostics();
            }

            if (!supportsEditor)
            {
                if (editorDiagnostics.empty() || editorDiagnostics == "Editor support not evaluated yet.")
                {
                    editorDiagnostics = error.empty()
                        ? "Plugin editor availability could not be determined during load."
                        : error;
                }
            }

            writeLoadAck(ok, error, supportsEditor, editorDiagnostics);
            return;
        }

        if (cmd == "noteOn")
        {
            renderer_.QueueNoteOn(
                message.value("channel", 0),
                message.value("pitch", 0),
                message.value("velocity", 0));
            return;
        }

        if (cmd == "noteOff")
        {
            renderer_.QueueNoteOff(
                message.value("channel", 0),
                message.value("pitch", 0));
            return;
        }

        if (cmd == "noteOffAll")
        {
            renderer_.QueueNoteOffAll();
            return;
        }

        if (cmd == "setProgram")
        {
            renderer_.QueueSetProgram(message.value("program", 0));
            return;
        }

        if (cmd == "openEditor")
        {
            std::string error;
            // Pass 0 (null) as parent HWND — bridge has no parent window
            const bool ok = renderer_.OpenEditor(nullptr, error);
            nlohmann::json ack;
            ack["ack"] = "editor_opened";
            ack["ok"] = ok;
            if (!ok) ack["error"] = error;
            writeAck(std::move(ack));
            return;
        }

        if (cmd == "closeEditor")
        {
            renderer_.CloseEditor();
            nlohmann::json ack;
            ack["ack"] = "editor_closed";
            ack["ok"] = true;
            writeAck(std::move(ack));
            return;
        }

        if (cmd == "shutdown")
        {
            shutdownRequested_ = true;
        }
    }
    catch (const std::exception& ex)
    {
        if (cmd == "load")
        {
            const std::string error = std::string("Unhandled bridge exception during load: ") + ex.what();
            std::cerr << "[Bridge] " << error << "\n";
            TryUnloadRendererNoThrow(renderer_);
            writeLoadAck(false, error, false, error);
            return;
        }

        std::cerr << "[Bridge] Unhandled exception while processing '" << cmd << "': " << ex.what() << "\n";
    }
    catch (...)
    {
        if (cmd == "load")
        {
            const std::string error = "Unhandled unknown bridge exception during load.";
            std::cerr << "[Bridge] " << error << "\n";
            TryUnloadRendererNoThrow(renderer_);
            writeLoadAck(false, error, false, error);
            return;
        }

        std::cerr << "[Bridge] Unhandled unknown exception while processing '" << cmd << "'.\n";
    }
}
