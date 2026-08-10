# AirFerry WASM decoder (M3 fast path): ZXing-C++ compiled to WebAssembly.
#
# Build with Emscripten:
#   source ~/emsdk/emsdk_env.sh
#   cd core/zxing-decoder
#   emcmake cmake -B build-wasm -DCMAKE_BUILD_TYPE=Release
#   cmake --build build-wasm
#   ./link-wasm.sh build-wasm   # links libs → airferry_zxing.js/.wasm
#
# zxing-cpp is fetched via FetchContent (pinned commit). To reuse an existing
# source tree (e.g. the Android build cache) instead of re-downloading, pass
#   -DZXING_SRC_DIR=<path/to/zxing-src>

cmake_minimum_required(VERSION 3.22.1)
project(airferry_zxing_wasm LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ZXing-C++ contains assert() calls that abort() on partial/corrupted QR frames;
# NDEBUG turns them into no-ops (same as the Android native target).
add_compile_definitions(NDEBUG)

# Emscripten disables exceptions by default; ZXing-C++ throws on some big/partial
# frames. Enabling them everywhere lets the wasm catch and skip gracefully
# instead of trapping (numeric JS exception on 1080p → "扫不出来").
add_compile_options(-fexceptions)

set(ZXING_COMMIT "8dd1cf5c4fd6fb6211bb96713db926ac6f2cf825")
if(NOT ZXING_SRC_DIR)
    set(ZXING_SRC_DIR "${CMAKE_CURRENT_BINARY_DIR}/_deps/zxing-src")
endif()

include(FetchContent)
FetchContent_Declare(
    zxing
    GIT_REPOSITORY https://github.com/zxing-cpp/zxing-cpp.git
    GIT_TAG ${ZXING_COMMIT}
    GIT_SHALLOW FALSE
    SOURCE_DIR ${ZXING_SRC_DIR}
)
set(ZXING_READERS ON CACHE BOOL "" FORCE)
set(ZXING_WRITERS OFF CACHE STRING "" FORCE)
set(ZXING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(ZXING_BLACKBOX_TESTS OFF CACHE BOOL "" FORCE)
set(ZXING_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(ZXING_C_API OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zxing)

# The linker emits the standalone .js/.wasm from the static libs (see
# link-wasm.sh). Keep it a static lib so it can be linked with emcc directly.
add_library(airferry_zxing STATIC
    airferry_zxing_core.cpp
    zxing_wasm.cpp
)
target_link_libraries(airferry_zxing PRIVATE ZXing::ZXing)
# -fexceptions: ZXing-C++ throws on some big/partial frames; without it the
# wasm traps and JS sees a numeric exception. Match link-wasm.sh.
target_compile_options(airferry_zxing PRIVATE -msimd128 -O3 -fexceptions)
set_target_properties(airferry_zxing PROPERTIES
    OUTPUT_NAME "airferry_zxing"
)
