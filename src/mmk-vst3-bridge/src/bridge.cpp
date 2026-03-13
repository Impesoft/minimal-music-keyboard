#include "bridge.h"

#include <iostream>
#include <stdexcept>

#include <nlohmann/json.hpp>

Bridge::Bridge(std::uint32_t hostPid)
    : hostPid_(hostPid)
{
}

void Bridge::Run()
{
    if (!ipc_.Connect(hostPid_))
        throw std::runtime_error("Failed to connect to named pipe.");

    if (!mmfWriter_.Open(hostPid_))
        throw std::runtime_error("Failed to open shared memory buffer.");

    renderer_.Start(&mmfWriter_);

    std::string line;
    while (!shutdownRequested_.load())
    {
        if (!ipc_.ReadLine(line))
            break;

        if (line.empty())
            continue;

        HandleCommand(line);
    }

    Shutdown();
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
            const bool ok = renderer_.Load(path, preset, error);
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
            renderer_.Unload();
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
            renderer_.Unload();
            writeLoadAck(false, error, false, error);
            return;
        }

        std::cerr << "[Bridge] Unhandled unknown exception while processing '" << cmd << "'.\n";
    }
}
