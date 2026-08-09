cmake_minimum_required(VERSION 3.22)
project(airferry_zxing LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
add_compile_definitions(NDEBUG)
if(MSVC)
    # Keep the decoder DLL self-contained; do not require a separately installed
    # Visual C++ redistributable just to load the QR front end.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

set(ZXING_COMMIT "8dd1cf5c4fd6fb6211bb96713db926ac6f2cf825")
include(FetchContent)
FetchContent_Declare(
    zxing
    GIT_REPOSITORY https://github.com/zxing-cpp/zxing-cpp.git
    GIT_TAG ${ZXING_COMMIT}
    GIT_SHALLOW FALSE
)
set(ZXING_READERS ON CACHE BOOL "" FORCE)
set(ZXING_WRITERS OFF CACHE STRING "" FORCE)
set(ZXING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(ZXING_BLACKBOX_TESTS OFF CACHE BOOL "" FORCE)
set(ZXING_UNIT_TESTS OFF CACHE BOOL "" FORCE)
set(ZXING_C_API OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
FetchContent_MakeAvailable(zxing)

set(AIRFERRY_ZXING_CORE_DIR "${CMAKE_CURRENT_LIST_DIR}/../../../core/zxing-decoder")
add_library(airferry_zxing SHARED
    zxing_capi.cpp
    "${AIRFERRY_ZXING_CORE_DIR}/airferry_zxing_core.cpp"
)
target_include_directories(airferry_zxing PRIVATE "${AIRFERRY_ZXING_CORE_DIR}")
target_link_libraries(airferry_zxing PRIVATE ZXing::ZXing)

if(MSVC)
    target_compile_options(airferry_zxing PRIVATE /O2 /EHsc)
endif()

include(CTest)
if(BUILD_TESTING)
    add_executable(airferry_zxing_core_tests
        tests/core_tests.cpp
        "${AIRFERRY_ZXING_CORE_DIR}/airferry_zxing_core.cpp"
    )
    target_include_directories(airferry_zxing_core_tests PRIVATE
        "${AIRFERRY_ZXING_CORE_DIR}")
    target_link_libraries(airferry_zxing_core_tests PRIVATE ZXing::ZXing)
    add_test(NAME airferry_zxing_core_tests COMMAND airferry_zxing_core_tests)
endif()
