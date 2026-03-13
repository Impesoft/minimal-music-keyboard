#include "ipc_client.h"

#include <string>

IpcClient::~IpcClient()
{
    Close();
}

bool IpcClient::Connect(DWORD hostPid)
{
    if (IsConnected())
        return true;

    const std::wstring pipeName = L"\\\\.\\pipe\\mmk-vst3-" + std::to_wstring(hostPid);

    pipeHandle_ = CreateFileW(
        pipeName.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        0,
        nullptr);

    if (pipeHandle_ == INVALID_HANDLE_VALUE)
    {
        if (GetLastError() != ERROR_PIPE_BUSY)
            return false;

        if (!WaitNamedPipeW(pipeName.c_str(), 5000))
            return false;

        pipeHandle_ = CreateFileW(
            pipeName.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr);
    }

    if (pipeHandle_ == INVALID_HANDLE_VALUE)
        return false;

    DWORD mode = PIPE_READMODE_BYTE;
    SetNamedPipeHandleState(pipeHandle_, &mode, nullptr, nullptr);
    return true;
}

bool IpcClient::ReadLine(std::string& line)
{
    if (!IsConnected())
        return false;

    line.clear();
    char ch = 0;
    DWORD bytesRead = 0;
    while (true)
    {
        const BOOL ok = ReadFile(pipeHandle_, &ch, 1, &bytesRead, nullptr);
        if (!ok || bytesRead == 0)
            return false;

        if (ch == '\n')
            break;

        if (ch != '\r')
            line.push_back(ch);
    }

    return true;
}

bool IpcClient::WriteLine(const std::string& line)
{
    if (!IsConnected())
        return false;

    std::string payload = line;
    payload.push_back('\n');

    DWORD bytesWritten = 0;
    DWORD offset = 0;
    const DWORD total = static_cast<DWORD>(payload.size());
    while (offset < total)
    {
        if (!WriteFile(pipeHandle_, payload.data() + offset, total - offset, &bytesWritten, nullptr))
            return false;

        offset += bytesWritten;
    }

    if (!FlushFileBuffers(pipeHandle_))
        return false;

    return true;
}

void IpcClient::Close()
{
    if (pipeHandle_ != INVALID_HANDLE_VALUE)
    {
        CloseHandle(pipeHandle_);
        pipeHandle_ = INVALID_HANDLE_VALUE;
    }
}

bool IpcClient::IsConnected() const
{
    return pipeHandle_ != INVALID_HANDLE_VALUE;
}
