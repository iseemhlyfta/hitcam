cmake_minimum_required(VERSION 3.21)
project(HitCamVCam LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the frame server service and the apps that load the DirectShow camera must not depend on a
# VC++ redistributable being installed.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Only the DirectShow camera: build.ps1 builds it a second time for x86, for 32-bit apps.
option(HITCAM_DSHOW_ONLY "Build only HitCamDShow (the x86 build)" OFF)

set(SOFTCAM_DIR third_party/softcam/src)

# ---- HitCamDShow.dll: the "HitCam" DirectShow camera for Windows 10 (Softcam, MIT) ----
file(GLOB SOFTCAM_BASECLASSES ${SOFTCAM_DIR}/baseclasses/*.cpp)
add_library(HitCamDShow SHARED
    src/dshow/HitCamDShow.cpp
    src/dshow/HitCamDShow.def
    ${SOFTCAM_BASECLASSES}
    ${SOFTCAM_DIR}/softcamcore/DShowSoftcam.cpp
    ${SOFTCAM_DIR}/softcamcore/FrameBuffer.cpp
    ${SOFTCAM_DIR}/softcamcore/Misc.cpp
    ${SOFTCAM_DIR}/softcamcore/Watchdog.cpp
)
target_include_directories(HitCamDShow PRIVATE ${SOFTCAM_DIR} ${SOFTCAM_DIR}/baseclasses)
target_compile_definitions(HitCamDShow PRIVATE UNICODE _UNICODE WIN32 NDEBUG _CRT_SECURE_NO_WARNINGS)
# Third-party code: its own warning level.
# The DirectShow base classes predate standard conformance (string literals as non-const CHAR*).
target_compile_options(HitCamDShow PRIVATE /W3 /EHsc /utf-8 /wd4996 /permissive /Zc:strictStrings-)
target_link_libraries(HitCamDShow PRIVATE strmiids winmm ole32 oleaut32 uuid advapi32 user32 gdi32)
if(CMAKE_SIZEOF_VOID_P EQUAL 4)
    set_target_properties(HitCamDShow PROPERTIES OUTPUT_NAME HitCamDShow32)
endif()

if(HITCAM_DSHOW_ONLY)
    return()
endif()

# ---- Compute shaders of the picture processing, compiled with the Windows SDK's fxc into headers ----
set(PROGRAM_FILES_X86 "ProgramFiles(x86)")
file(GLOB FXC_CANDIDATES "$ENV{${PROGRAM_FILES_X86}}/Windows Kits/10/bin/*/x64/fxc.exe")
list(SORT FXC_CANDIDATES COMPARE NATURAL ORDER DESCENDING)
# The newest SDK first; a developer prompt's PATH otherwise.
set(FXC_NEWEST_DIR)
if(FXC_CANDIDATES)
    list(GET FXC_CANDIDATES 0 FXC_NEWEST)
    get_filename_component(FXC_NEWEST_DIR "${FXC_NEWEST}" DIRECTORY)
endif()
find_program(FXC fxc HINTS ${FXC_NEWEST_DIR})
if(NOT FXC)
    message(FATAL_ERROR "fxc.exe (Windows SDK) not found")
endif()
set(SHADER_SOURCE ${CMAKE_CURRENT_SOURCE_DIR}/src/shaders/Process.hlsl)
set(SHADER_HEADERS)
foreach(entry TemporalLuma TemporalChroma AdjustLuma AdjustChroma)
    set(header ${CMAKE_CURRENT_BINARY_DIR}/shaders/${entry}.h)
    add_custom_command(OUTPUT ${header}
        COMMAND ${CMAKE_COMMAND} -E make_directory ${CMAKE_CURRENT_BINARY_DIR}/shaders
        COMMAND "${FXC}" /nologo /T cs_5_0 /E ${entry} /O3 /WX /Vn g_${entry} /Fh ${header} ${SHADER_SOURCE}
        DEPENDS ${SHADER_SOURCE}
        COMMENT "fxc ${entry}")
    list(APPEND SHADER_HEADERS ${header})
endforeach()

# ---- HitCamVCam.dll: decoder bridge for HitCam.exe + Media Foundation camera (Windows 11) ----
add_library(HitCamVCam SHARED
    src/Shared.cpp
    src/MediaSource.cpp
    src/Bridge.cpp
    src/DShowOutput.cpp
    src/ArtifactReducer.cpp
    src/GpuProcessor.cpp
    ${SHADER_HEADERS}
    src/DllMain.cpp
    src/HitCamVCam.def
    # NVIDIA Video Effects SDK loaders (MIT): the runtime itself is a separate NVIDIA install.
    third_party/nvvfx/src/NVVideoEffectsProxy.cpp
    third_party/nvvfx/src/nvCVImageProxy.cpp
    # Softcam's sending side (MIT): frames for the DirectShow camera.
    ${SOFTCAM_DIR}/softcamcore/SenderAPI.cpp
    ${SOFTCAM_DIR}/softcamcore/FrameBuffer.cpp
    ${SOFTCAM_DIR}/softcamcore/Misc.cpp
    ${SOFTCAM_DIR}/softcamcore/Watchdog.cpp
)
target_include_directories(HitCamVCam PRIVATE third_party/nvvfx/include ${SOFTCAM_DIR} ${CMAKE_CURRENT_BINARY_DIR})
set_source_files_properties(
    third_party/nvvfx/src/NVVideoEffectsProxy.cpp third_party/nvvfx/src/nvCVImageProxy.cpp
    ${SOFTCAM_DIR}/softcamcore/SenderAPI.cpp ${SOFTCAM_DIR}/softcamcore/FrameBuffer.cpp
    ${SOFTCAM_DIR}/softcamcore/Misc.cpp ${SOFTCAM_DIR}/softcamcore/Watchdog.cpp
    PROPERTIES COMPILE_OPTIONS "/W0;/WX-")
# Softcam uses the multimedia timer API that WIN32_LEAN_AND_MEAN leaves out.
set_source_files_properties(${SOFTCAM_DIR}/softcamcore/Misc.cpp TARGET_DIRECTORY HitCamVCam PROPERTIES COMPILE_OPTIONS "/W0;/WX-;/UWIN32_LEAN_AND_MEAN")

target_compile_definitions(HitCamVCam PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 NTDDI_VERSION=0x0A00000C)
target_compile_options(HitCamVCam PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
target_link_libraries(HitCamVCam PRIVATE mfplat mfuuid mf mfsensorgroup wmcodecdspuuid strmiids ole32 oleaut32 advapi32 propsys shell32 winmm d3d11 delayimp)
# The virtual camera API exists only on Windows 11: loaded on first use, so decoding and the preview still work without it.
# Direct3D is needed only by HitCam.exe with picture processing on, not by the frame server that hosts the camera.
target_link_options(HitCamVCam PRIVATE /DELAYLOAD:mfsensorgroup.dll /DELAYLOAD:d3d11.dll)

# Manual checks (build.ps1 -Tests); the end-to-end one needs the camera registered (not run in CI).
add_executable(HitCamVCamTest tests/VCamTest.cpp src/Shared.cpp)
target_include_directories(HitCamVCamTest PRIVATE src)
target_compile_definitions(HitCamVCamTest PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00 NTDDI_VERSION=0x0A00000C)
target_compile_options(HitCamVCamTest PRIVATE /W4 /WX /permissive- /EHsc /utf-8)
target_link_libraries(HitCamVCamTest PRIVATE HitCamVCam mfplat mfuuid mf mfreadwrite mfsensorgroup wmcodecdspuuid strmiids ole32 oleaut32 advapi32)
add_dependencies(HitCamVCamTest HitCamDShow)
