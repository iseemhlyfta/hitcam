cmake_minimum_required(VERSION 3.21)
project(HitCamVCam LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the frame server service must not depend on a VC++ redistributable being installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(HitCamVCam SHARED
    src/Shared.cpp
    src/MediaSource.cpp
    src/Bridge.cpp
    src/Denoiser.cpp
    src/DllMain.cpp
    src/HitCamVCam.def
    # NVIDIA Video Effects SDK loaders (MIT): the runtime itself is a separate NVIDIA install.
    third_party/nvvfx/src/NVVideoEffectsProxy.cpp
    third_party/nvvfx/src/nvCVImageProxy.cpp
)
target_include_directories(HitCamVCam PRIVATE third_party/nvvfx/include)
set_source_files_properties(third_party/nvvfx/src/NVVideoEffectsProxy.cpp third_party/nvvfx/src/nvCVImageProxy.cpp
    PROPERTIES COMPILE_OPTIONS "/W0;/WX-")

target_compile_definitions(HitCamVCam PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 NTDDI_VERSION=0x0A00000C)
target_compile_options(HitCamVCam PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
target_link_libraries(HitCamVCam PRIVATE mfplat mfuuid mf mfsensorgroup wmcodecdspuuid strmiids ole32 oleaut32 advapi32 propsys shell32)

# Manual checks (build.ps1 -Tests); the end-to-end one needs the camera registered (not run in CI).
add_executable(HitCamVCamTest tests/VCamTest.cpp src/Shared.cpp)
target_compile_definitions(HitCamVCamTest PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 NTDDI_VERSION=0x0A00000C)
target_compile_options(HitCamVCamTest PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
target_link_libraries(HitCamVCamTest PRIVATE HitCamVCam mfplat mfuuid mf mfreadwrite mfsensorgroup wmcodecdspuuid ole32 advapi32)
