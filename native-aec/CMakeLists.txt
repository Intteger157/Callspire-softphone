cmake_minimum_required(VERSION 3.16)
project(webrtc_apm CXX)

# The dependency's CMake detects AVX2/SSE implementations based on
# `CMAKE_SYSTEM_PROCESSOR` matching x86/x86_64 regexes. On Windows/MSVC it's
# often `AMD64`, which doesn't match. Force x86_64 so the APM wrapper
# compiles the necessary AVX2/SSE source files.
if (WIN32)
    set(CMAKE_SYSTEM_PROCESSOR "x86_64" CACHE STRING "" FORCE)
endif()

# ─── webrtc-audio-processing dependency ───
#
# Official pulseaudio webrtc-audio-processing builds with Meson.
# For Windows + CMake integration we use a community CMake wrapper repo that
# builds the WebRTC APM code into a static library named `webrtc_apm_wrapper`.
#
# Repo:
#   https://github.com/jiemojiemo/webrtc-audio-processing-cmake
#
# If you prefer vcpkg: there is no official vcpkg port for webrtc-audio-processing
# in current registries, so you'll need either a custom port or FetchContent.

include(FetchContent)

FetchContent_Declare(
    webrtc_audio_processing_cmake
    GIT_REPOSITORY https://github.com/jiemojiemo/webrtc-audio-processing-cmake.git
    GIT_TAG main
    GIT_SHALLOW FALSE
    GIT_SUBMODULES_RECURSE TRUE
)

FetchContent_Populate(webrtc_audio_processing_cmake)

# Patch upstream CMake: add FIRFilter AVX2 sources to COMMON_AUDIO_SRC.
# Otherwise we get a single unresolved external for FIRFilterAVX2.
set(_webtrtc_common_audio_cmake
    "${webrtc_audio_processing_cmake_SOURCE_DIR}/webrtc/common_audio/CMakeLists.txt")
file(READ "${_webtrtc_common_audio_cmake}" _webrtc_common_audio_cmake_text)

if (NOT _webrtc_common_audio_cmake_text MATCHES "fir_filter_avx2\\.cc")
    # Insert after fir_filter_sse.h in the x86/x86_64 block.
    string(REPLACE
        "\"fir_filter_sse.h\""
        "\"fir_filter_sse.h\"\n            \"fir_filter_avx2.cc\"\n            \"fir_filter_avx2.h\""
        _webrtc_common_audio_cmake_text_patched
        "${_webrtc_common_audio_cmake_text}"
    )
    file(WRITE "${_webtrtc_common_audio_cmake}" "${_webrtc_common_audio_cmake_text_patched}")
endif()

# Now that the dependency is patched, add it to the build.
add_subdirectory(
    "${webrtc_audio_processing_cmake_SOURCE_DIR}"
    "${webrtc_audio_processing_cmake_BINARY_DIR}"
)

add_library(webrtc_apm SHARED
    webrtc_apm.cpp
)

# `webrtc_apm_wrapper` provides include directories for WebRTC APM headers.
target_link_libraries(webrtc_apm PRIVATE webrtc_apm_wrapper)

# The upstream WebRTC APM code expects `WEBRTC_WIN` to be defined on Windows.
# The FetchContent wrapper doesn't always set this when building under MSVC,
# which causes it to fall back to GCC/Clang __sync/__atomic builtins.
if (MSVC)
    target_compile_definitions(webrtc_apm_wrapper PRIVATE
        WEBRTC_WIN
        _USE_MATH_DEFINES
        NOMINMAX
        WIN32_LEAN_AND_MEAN
    )

    # WebRTC POSIX synchronization code may still get compiled and it
    # requires pthread.h. On Windows we satisfy it using vcpkg's PThreads4W.
    find_package(PThreads4W CONFIG REQUIRED)
    target_link_libraries(webrtc_apm_wrapper PRIVATE PThreads4W::PThreads4W)

    # Enable AVX2 implementations required by some AEC3 vector math paths.
    # Without /arch:AVX2, AVX2 code paths may be referenced but not
    # compiled into the wrapper library, leading to unresolved externals.
    target_compile_options(webrtc_apm_wrapper PRIVATE /arch:AVX2)
endif()

if (MSVC)
    # timeGetTime is in winmm.lib
    target_link_libraries(webrtc_apm PRIVATE winmm)
endif()

target_include_directories(webrtc_apm PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    # Needed because webrtc_apm.cpp includes WebRTC headers which depend on absl.
    ${CMAKE_CURRENT_BINARY_DIR}/_deps/webrtc_audio_processing_cmake-src/abseil-cpp
)

target_compile_definitions(webrtc_apm PRIVATE
    _USE_MATH_DEFINES
    _CRT_SECURE_NO_WARNINGS
)

if(MSVC)
    target_compile_options(webrtc_apm PRIVATE /W3 /O2)
else()
    target_compile_options(webrtc_apm PRIVATE -Wall -O2)
endif()

# Copy DLL to Callspire output directory after build
set(CALLSPIRE_OUT "${CMAKE_CURRENT_SOURCE_DIR}/../bin/Debug/net8.0-windows10.0.17763/win-x64")
set(CALLSPIRE_OUT_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/../bin/Release/net8.0-windows10.0.17763/win-x64")
set(NATIVE_AEC_RELEASE_STAGING "${CMAKE_CURRENT_BINARY_DIR}/Release")
add_custom_command(TARGET webrtc_apm POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:webrtc_apm>
        ${CALLSPIRE_OUT}/webrtc_apm.dll
    COMMENT "Copying webrtc_apm.dll to Callspire output"
)

add_custom_command(TARGET webrtc_apm POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:webrtc_apm>
        "${NATIVE_AEC_RELEASE_STAGING}/webrtc_apm.dll"
    COMMENT "Staging webrtc_apm.dll for Callspire.csproj (native-aec/build/Release)"
)

add_custom_command(TARGET webrtc_apm POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:webrtc_apm>
        ${CALLSPIRE_OUT_RELEASE}/webrtc_apm.dll
    COMMENT "Copying webrtc_apm.dll to Callspire RELEASE output"
)
