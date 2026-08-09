#include <array>
#include <cstdint>
#include <vector>

#include "airferry_zxing_core.h"

int main()
{
    std::array<uint8_t, 12> pixels{};
    if (!AirFerryZxing::ValidLuminanceGeometry(
            pixels.data(), pixels.size(), 4, 3, 4)) return 1;
    if (AirFerryZxing::ValidLuminanceGeometry(
            pixels.data(), pixels.size(), 5, 3, 4)) return 2;
    if (AirFerryZxing::ValidLuminanceGeometry(
            pixels.data(), pixels.size() - 1, 4, 3, 4)) return 3;

    std::vector<AirFerryZxing::DecodeResult> decoded = {
        {{0x45, 0x54, 0x01}, {10, 20, 110, 120}},
        {{0x10, 0x20}, {200, 30, 300, 130}},
    };
    const std::vector<uint8_t> packed = AirFerryZxing::PackMultiResults(decoded);
    if (packed.size() != 4 + 4 + 3 + 16 + 4 + 2 + 16) return 4;
    if (packed[0] != 2 || packed[1] != 0 || packed[2] != 0 || packed[3] != 0) return 5;
    if (packed[4] != 3) return 6;
    if (packed[8] != 0x45 || packed[9] != 0x54 || packed[10] != 0x01) return 7;
    return 0;
}
