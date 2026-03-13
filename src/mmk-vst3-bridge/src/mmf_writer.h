#pragma once

#include <cstdint>
#include <string>

#include <windows.h>

class MmfWriter
{
public:
    MmfWriter() = default;
    ~MmfWriter();

    bool Open(DWORD hostPid);
    bool WriteFrame(const float* stereoSamples, int frameCount);
    void Close();

    int FrameSize() const;
    bool IsOpen() const;

private:
    bool OpenMappingByName(const std::wstring& name);

    HANDLE mappingHandle_ = nullptr;
    void* view_ = nullptr;
    LONG* writeCounterAddress_ = nullptr;
    float* audioBuffer_ = nullptr;
    int frameSize_ = 0;
    int ringCapacity_ = 0;
    LONG writeCounter_ = 0;
};
