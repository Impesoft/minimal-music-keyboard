#include "bridge.h"

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
    catch (...)
    {
        return;
    }

    const auto cmd = message.value("cmd", std::string());
    if (cmd == "load")
    {
        const auto path = message.value("path", std::string());
        const auto preset = message.value("preset", std::string());

        std::string error;
        const bool ok = renderer_.Load(path, preset, error);
        nlohmann::json ack;
        ack["ack"] = "load_ack";
        ack["ok"] = ok;
        if (!ok)
            ack["error"] = error.empty() ? "Failed to load VST3 plugin." : error;

        ipc_.WriteLine(ack.dump());
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

    if (cmd == "shutdown")
    {
        shutdownRequested_ = true;
    }
}
