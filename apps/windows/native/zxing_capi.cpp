#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include "airferry_zxing_core.h"

#if defined(_WIN32)
#define AIRFERRY_ZXING_EXPORT __declspec(dllexport)
#else
#define AIRFERRY_ZXING_EXPORT __attribute__((visibility("default")))
#endif

extern "C" {

AIRFERRY_ZXING_EXPORT uint32_t airferry_zxing_abi_version()
{
    return 1;
}

AIRFERRY_ZXING_EXPORT int airferry_zxing_decode_multi_y(
    const uint8_t* pixels,
    size_t pixel_len,
    int32_t width,
    int32_t height,
    int32_t row_stride,
    const int32_t* hints,
    size_t hint_count,
    float margin_fraction,
    uint8_t** out_buffer,
    size_t* out_len)
{
    if (out_buffer == nullptr || out_len == nullptr) {
        return 0;
    }
    *out_buffer = nullptr;
    *out_len = 0;

    try {
        const auto decoded = hint_count == 0
            ? AirFerryZxing::DecodeMultiFull(
                pixels, pixel_len, width, height, row_stride)
            : AirFerryZxing::DecodeMultiRegions(
                pixels, pixel_len, width, height, row_stride,
                hints, hint_count, margin_fraction);
        const std::vector<uint8_t> packed = AirFerryZxing::PackMultiResults(decoded);
        if (packed.empty()) {
            return 0;
        }
        auto* buffer = static_cast<uint8_t*>(std::malloc(packed.size()));
        if (buffer == nullptr) {
            return 0;
        }
        std::memcpy(buffer, packed.data(), packed.size());
        *out_buffer = buffer;
        *out_len = packed.size();
        return 1;
    } catch (...) {
        return 0;
    }
}

AIRFERRY_ZXING_EXPORT void airferry_zxing_buffer_free(uint8_t* buffer)
{
    std::free(buffer);
}

}  // extern "C"
