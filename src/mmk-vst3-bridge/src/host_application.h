#pragma once

#include <atomic>
#include <cstring>
#include <pluginterfaces/vst/ivsthostapplication.h>
#include <pluginterfaces/base/funknown.h>

// Minimal IHostApplication stub required by VST3 spec.
// Some plugins refuse to load when host context is nullptr.
class HostApplication : public Steinberg::Vst::IHostApplication
{
public:
    // IHostApplication
    Steinberg::tresult PLUGIN_API getName(Steinberg::Vst::String128 name) override
    {
        const char16_t src[] = u"Minimal Music Keyboard";
        const size_t len = sizeof(src) / sizeof(src[0]);
        for (size_t i = 0; i < len && i < 128; ++i)
        {
            name[i] = src[i];
        }
        return Steinberg::kResultOk;
    }

    Steinberg::tresult PLUGIN_API createInstance(Steinberg::TUID /*cid*/, Steinberg::TUID /*_iid*/, void** obj) override
    {
        *obj = nullptr;
        return Steinberg::kNotImplemented;
    }

    // IUnknown / FUnknown
    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID _iid, void** obj) override
    {
        if (Steinberg::FUnknownPrivate::iidEqual(_iid, Steinberg::Vst::IHostApplication::iid) ||
            Steinberg::FUnknownPrivate::iidEqual(_iid, Steinberg::FUnknown::iid))
        {
            addRef();
            *obj = this;
            return Steinberg::kResultOk;
        }
        *obj = nullptr;
        return Steinberg::kNoInterface;
    }

    Steinberg::uint32 PLUGIN_API addRef() override
    {
        return ++refCount_;
    }

    Steinberg::uint32 PLUGIN_API release() override
    {
        const auto r = --refCount_;
        if (r == 0)
        {
            delete this;
        }
        return r;
    }

private:
    std::atomic<Steinberg::uint32> refCount_{ 1 };
};
