#include "ika_native.h"

#include <array>
#include <chrono>
#include <cstring>
#include <string>

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

namespace {
std::string build_fake_frame_hash() {
    const auto now = std::chrono::high_resolution_clock::now().time_since_epoch().count();
    const auto pid = static_cast<long long>(GetCurrentProcessId());
    const auto tid = static_cast<long long>(GetCurrentThreadId());
    auto seed = static_cast<unsigned long long>(now ^ (pid << 12) ^ (tid << 4));
    std::array<char, 65> buffer{};
    for (auto i = 0; i < 64; ++i) {
        const auto nibble = static_cast<unsigned int>((seed >> (i % 16)) & 0xF);
        buffer[static_cast<size_t>(i)] = "0123456789abcdef"[nibble];
        seed = (seed * 6364136223846793005ULL) + 1;
    }
    buffer[64] = '\0';
    return std::string(buffer.data());
}
}  // namespace

int ika_native_lock_input() {
    // OS-level input lock for autopilot scan mode.
    return BlockInput(TRUE) ? 1 : 0;
}

int ika_native_unlock_input() {
    return BlockInput(FALSE) ? 1 : 0;
}

int ika_native_capture_frame_hash(char* output_buffer, int output_buffer_length) {
    if (output_buffer == nullptr || output_buffer_length < 65) {
        return 0;
    }
    const auto hash = build_fake_frame_hash();
    std::strncpy(output_buffer, hash.c_str(), static_cast<size_t>(output_buffer_length - 1));
    output_buffer[output_buffer_length - 1] = '\0';
    return static_cast<int>(hash.size());
}
