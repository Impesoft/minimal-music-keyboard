cmake_minimum_required(VERSION 3.20)
project(mmk-vst3-bridge VERSION 0.1.0 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# VST3 SDK: clone https://github.com/steinbergmedia/vst3sdk to extern/vst3sdk
set(VST3_SDK_ROOT "${CMAKE_SOURCE_DIR}/../../../extern/vst3sdk" CACHE PATH "Steinberg VST3 SDK root")

# Verify SDK exists
if(NOT EXISTS "${VST3_SDK_ROOT}/pluginterfaces/vst/ivstaudioprocessor.h")
    message(FATAL_ERROR "VST3 SDK not found at ${VST3_SDK_ROOT}. Clone it with:\n  git clone https://github.com/steinbergmedia/vst3sdk.git --recurse-submodules")
endif()

# Compile VST3 hosting library sources directly (SDK no longer ships per-module CMakeLists)
set(VST3_PLUGIFACE_SOURCES
    ${VST3_SDK_ROOT}/pluginterfaces/base/funknown.cpp
    ${VST3_SDK_ROOT}/pluginterfaces/base/coreiids.cpp
    ${VST3_SDK_ROOT}/pluginterfaces/base/conststringtable.cpp
    ${VST3_SDK_ROOT}/pluginterfaces/base/ustring.cpp
)
set(VST3_BASE_SOURCES
    ${VST3_SDK_ROOT}/base/source/baseiids.cpp
    ${VST3_SDK_ROOT}/base/source/fbuffer.cpp
    ${VST3_SDK_ROOT}/base/source/fobject.cpp
    ${VST3_SDK_ROOT}/base/source/fstreamer.cpp
    ${VST3_SDK_ROOT}/base/source/fstring.cpp
    ${VST3_SDK_ROOT}/base/source/fdynlib.cpp
    ${VST3_SDK_ROOT}/base/thread/source/fcondition.cpp
    ${VST3_SDK_ROOT}/base/thread/source/flock.cpp
)
set(VST3_HOSTING_SOURCES
    ${VST3_SDK_ROOT}/public.sdk/source/common/commonstringconvert.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/module_win32.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/plugprovider.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/hostclasses.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/eventlist.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/parameterchanges.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/processdata.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/connectionproxy.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/hosting/pluginterfacesupport.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/vstinitiids.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/utility/stringconvert.cpp
    ${VST3_SDK_ROOT}/public.sdk/source/vst/vstpresetfile.cpp
)

add_library(vst3_sdk_static STATIC ${VST3_PLUGIFACE_SOURCES} ${VST3_BASE_SOURCES} ${VST3_HOSTING_SOURCES})
target_include_directories(vst3_sdk_static PUBLIC ${VST3_SDK_ROOT})
target_compile_definitions(vst3_sdk_static PUBLIC UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
# Disable C++20 char8_t — VST3 SDK module_win32.cpp uses u8string() as std::string (C++17 semantics)
if(MSVC)
    target_compile_options(vst3_sdk_static PRIVATE /Zc:char8_t-)
endif()

# nlohmann/json: use bundled single-header if vcpkg is not available
if(NOT TARGET nlohmann_json::nlohmann_json)
    find_package(nlohmann_json CONFIG QUIET)
endif()
if(NOT TARGET nlohmann_json::nlohmann_json)
    add_library(nlohmann_json INTERFACE)
    target_include_directories(nlohmann_json INTERFACE "${CMAKE_SOURCE_DIR}/include")
    add_library(nlohmann_json::nlohmann_json ALIAS nlohmann_json)
endif()

add_executable(mmk-vst3-bridge
    src/main.cpp
    src/bridge.cpp
    src/ipc_client.cpp
    src/audio_renderer.cpp
    src/mmf_writer.cpp
)

target_link_libraries(mmk-vst3-bridge PRIVATE nlohmann_json::nlohmann_json vst3_sdk_static)
target_compile_definitions(mmk-vst3-bridge PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
