#include "audio_renderer.h"

#include <algorithm>
#include <chrono>
#include <vector>

AudioRenderer::~AudioRenderer()
{
    Stop();
}

void AudioRenderer::Start(MmfWriter* writer)
{
    if (running_.load() || writer == nullptr)
        return;

    writer_ = writer;
    frameSize_ = writer_->FrameSize();
    if (frameSize_ <= 0)
        return;

    running_ = true;
    renderThread_ = std::thread(&AudioRenderer::RenderLoop, this);
}

void AudioRenderer::Stop()
{
    if (!running_.exchange(false))
        return;

    if (renderThread_.joinable())
        renderThread_.join();
}

void AudioRenderer::QueueNoteOn(int channel, int pitch, int velocity)
{
    MidiEvent evt;
    evt.type = MidiEventType::NoteOn;
    evt.channel = channel;
    evt.pitch = pitch;
    evt.velocity = velocity;
    eventQueue_.Push(evt);
}

void AudioRenderer::QueueNoteOff(int channel, int pitch)
{
    MidiEvent evt;
    evt.type = MidiEventType::NoteOff;
    evt.channel = channel;
    evt.pitch = pitch;
    eventQueue_.Push(evt);
}

void AudioRenderer::QueueNoteOffAll()
{
    MidiEvent evt;
    evt.type = MidiEventType::NoteOffAll;
    eventQueue_.Push(evt);
}

void AudioRenderer::QueueSetProgram(int program)
{
    MidiEvent evt;
    evt.type = MidiEventType::SetProgram;
    evt.program = program;
    eventQueue_.Push(evt);
}

bool AudioRenderer::MidiEventQueue::Push(const MidiEvent& evt)
{
    const auto write = writeIndex_.load(std::memory_order_relaxed);
    const auto next = (write + 1) % kCapacity;
    if (next == readIndex_.load(std::memory_order_acquire))
        return false;

    buffer_[write] = evt;
    writeIndex_.store(next, std::memory_order_release);
    return true;
}

bool AudioRenderer::MidiEventQueue::Pop(MidiEvent& evt)
{
    const auto read = readIndex_.load(std::memory_order_relaxed);
    if (read == writeIndex_.load(std::memory_order_acquire))
        return false;

    evt = buffer_[read];
    readIndex_.store((read + 1) % kCapacity, std::memory_order_release);
    return true;
}

void AudioRenderer::RenderLoop()
{
    if (writer_ == nullptr || frameSize_ <= 0)
        return;

    std::vector<float> buffer(static_cast<std::size_t>(frameSize_) * 2);
    const auto frameDuration =
        std::chrono::duration<double>(static_cast<double>(frameSize_) / kSampleRate);
    auto nextTick = std::chrono::steady_clock::now();

    while (running_.load())
    {
        MidiEvent evt;
        while (eventQueue_.Pop(evt))
        {
            // TODO: Route MIDI to VST3 IAudioProcessor.
            (void)evt;
        }

        RenderFrame(buffer.data(), frameSize_);
        writer_->WriteFrame(buffer.data(), frameSize_);

        nextTick += std::chrono::duration_cast<std::chrono::steady_clock::duration>(frameDuration);
        std::this_thread::sleep_until(nextTick);
    }
}

void AudioRenderer::RenderFrame(float* output, int frameSize)
{
    // TODO: Call VST3 IAudioProcessor::process() and fill output.
    std::fill(output, output + (frameSize * 2), 0.0f);
}
