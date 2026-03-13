#include "mmf_writer.h"

#include <algorithm>
#include <cstring>

namespace
{
    constexpr std::int32_t kMmfMagic = 0x4D4D4B56;
    constexpr std::int32_t kMmfVersion = 2;
    constexpr std::size_t kHeaderSize = 20;
}

MmfWriter::~MmfWriter()
{
    Close();
}

bool MmfWriter::Open(DWORD hostPid)
{
    if (IsOpen())
        return true;

    const std::wstring baseName = L"mmk-vst3-audio-" + std::to_wstring(hostPid);
    if (!OpenMappingByName(L"Global\\" + baseName))
    {
        if (!OpenMappingByName(baseName))
            return false;
    }

    auto* header = static_cast<std::int32_t*>(view_);
    if (header[0] != kMmfMagic || header[1] != kMmfVersion || header[2] <= 0 || header[3] <= 0)
    {
        Close();
        return false;
    }

    frameSize_ = header[2];
    ringCapacity_ = header[3];
    writeCounterAddress_ = reinterpret_cast<LONG*>(reinterpret_cast<std::uint8_t*>(view_) + 16);
    audioBuffer_ = reinterpret_cast<float*>(reinterpret_cast<std::uint8_t*>(view_) + kHeaderSize);
    writeCounter_ = *writeCounterAddress_;

    return true;
}

bool MmfWriter::WriteFrame(const float* stereoSamples, int frameCount)
{
    if (!IsOpen() || frameSize_ <= 0 || stereoSamples == nullptr)
        return false;

    const int framesToCopy = std::min(frameCount, frameSize_);
    const int samplesToCopy = framesToCopy * 2;
    const LONG blockCounter = writeCounter_;
    const LONG slot = blockCounter % ringCapacity_;
    float* const blockBuffer = audioBuffer_ + (static_cast<std::size_t>(slot) * frameSize_ * 2);
    std::memcpy(blockBuffer, stereoSamples, samplesToCopy * sizeof(float));

    if (framesToCopy < frameSize_)
    {
        std::fill(blockBuffer + samplesToCopy, blockBuffer + (frameSize_ * 2), 0.0f);
    }

    writeCounter_ = blockCounter + 1;
    InterlockedExchange(writeCounterAddress_, writeCounter_);
    return true;
}

void MmfWriter::Close()
{
    if (view_ != nullptr)
    {
        UnmapViewOfFile(view_);
        view_ = nullptr;
    }

    if (mappingHandle_ != nullptr)
    {
        CloseHandle(mappingHandle_);
        mappingHandle_ = nullptr;
    }

    writeCounterAddress_ = nullptr;
    audioBuffer_ = nullptr;
    frameSize_ = 0;
    ringCapacity_ = 0;
    writeCounter_ = 0;
}

int MmfWriter::FrameSize() const
{
    return frameSize_;
}

bool MmfWriter::IsOpen() const
{
    return mappingHandle_ != nullptr;
}

bool MmfWriter::OpenMappingByName(const std::wstring& name)
{
    mappingHandle_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name.c_str());
    if (mappingHandle_ == nullptr)
        return false;

    view_ = MapViewOfFile(mappingHandle_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
    if (view_ == nullptr)
    {
        CloseHandle(mappingHandle_);
        mappingHandle_ = nullptr;
        return false;
    }

    return true;
}
