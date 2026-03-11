cmake_minimum_required(VERSION 3.20)
project(mmk-vst3-bridge VERSION 0.1.0 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# VST3 SDK: clone https://github.com/steinbergmedia/vst3sdk to extern/vst3sdk

find_package(nlohmann_json CONFIG REQUIRED)

add_executable(mmk-vst3-bridge
    src/main.cpp
    src/bridge.cpp
    src/ipc_client.cpp
    src/audio_renderer.cpp
    src/mmf_writer.cpp
)

target_link_libraries(mmk-vst3-bridge PRIVATE nlohmann_json::nlohmann_json)
target_compile_definitions(mmk-vst3-bridge PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
