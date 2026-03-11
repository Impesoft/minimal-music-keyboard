#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <thread>

#include "mmf_writer.h"

class AudioRenderer
{
public:
    AudioRenderer() = default;
    ~AudioRenderer();

    void Start(MmfWriter* writer);
    void Stop();

    void QueueNoteOn(int channel, int pitch, int velocity);
    void QueueNoteOff(int channel, int pitch);
    void QueueNoteOffAll();
    void QueueSetProgram(int program);

private:
    enum class MidiEventType : std::uint8_t
    {
        NoteOn,
        NoteOff,
        NoteOffAll,
        SetProgram
    };

    struct MidiEvent
    {
        MidiEventType type{};
        int channel = 0;
        int pitch = 0;
        int velocity = 0;
        int program = 0;
    };

    class MidiEventQueue
    {
    public:
        bool Push(const MidiEvent& evt);
        bool Pop(MidiEvent& evt);

    private:
        static constexpr std::size_t kCapacity = 256;
        std::array<MidiEvent, kCapacity> buffer_{};
        std::atomic<std::size_t> writeIndex_{ 0 };
        std::atomic<std::size_t> readIndex_{ 0 };
    };

    void RenderLoop();
    void RenderFrame(float* output, int frameSize);

    std::atomic<bool> running_{ false };
    std::thread renderThread_;
    MmfWriter* writer_ = nullptr;
    int frameSize_ = 0;
    MidiEventQueue eventQueue_{};

    static constexpr int kSampleRate = 48'000;
};
