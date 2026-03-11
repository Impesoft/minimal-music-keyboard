#pragma once

#include <cstdint>
#include <atomic>
#include <string>

#include "audio_renderer.h"
#include "ipc_client.h"
#include "mmf_writer.h"

class Bridge
{
public:
    explicit Bridge(std::uint32_t hostPid);
    void Run();
    void Shutdown();

private:
    void HandleCommand(const std::string& line);

    std::uint32_t hostPid_;
    IpcClient ipc_;
    MmfWriter mmfWriter_;
    AudioRenderer renderer_;
    std::atomic<bool> shutdownRequested_{ false };
};
