#pragma once

#include <string>

#include <windows.h>

class IpcClient
{
public:
    IpcClient() = default;
    ~IpcClient();

    bool Connect(DWORD hostPid);
    bool ReadLine(std::string& line);
    bool WriteLine(const std::string& line);
    void Close();
    bool IsConnected() const;

private:
    HANDLE pipeHandle_ = INVALID_HANDLE_VALUE;
};
