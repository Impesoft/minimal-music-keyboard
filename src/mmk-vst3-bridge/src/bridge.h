#pragma once

#include <cstdint>
#include <atomic>
#include <mutex>
#include <queue>
#include <string>
#include <thread>

#include <Windows.h>

#include "audio_renderer.h"
#include "ipc_client.h"
#include "mmf_writer.h"

/// Bridge between the managed host and the native VST3 renderer.
///
/// Architecture: the main thread runs a Win32 message loop so that VST3
/// editor windows (which require STA + message pump on the thread that
/// loaded the plugin) work correctly.  Pipe I/O runs on a background
/// thread that posts commands to the main thread via a hidden window.
class Bridge
{
public:
    explicit Bridge(std::uint32_t hostPid);
    void Run();
    void Shutdown();

private:
    void HandleCommand(const std::string& line);
    void PipeReaderLoop();
    void DrainCommandQueue();

    static LRESULT CALLBACK MessageWindowProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam);

    static constexpr UINT WM_BRIDGE_COMMAND    = WM_APP + 100;
    static constexpr UINT WM_BRIDGE_PIPE_CLOSED = WM_APP + 101;

    std::uint32_t hostPid_;
    IpcClient ipc_;
    MmfWriter mmfWriter_;
    AudioRenderer renderer_;
    std::atomic<bool> shutdownRequested_{ false };

    HWND messageHwnd_{ nullptr };
    std::thread pipeReaderThread_;
    std::mutex commandMutex_;
    std::queue<std::string> commandQueue_;
};
