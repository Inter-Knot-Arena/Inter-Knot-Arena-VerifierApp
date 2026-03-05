#include "ika_native.h"

#include <array>
#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstring>
#include <thread>
#include <string>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <bcrypt.h>

#pragma comment(lib, "bcrypt.lib")

namespace {

bool capture_desktop_rgba(std::vector<unsigned char>& output, int width, int height) {
    if (width <= 0 || height <= 0) {
        return false;
    }

    const auto screen_dc = GetDC(nullptr);
    if (screen_dc == nullptr) {
        return false;
    }
    const auto mem_dc = CreateCompatibleDC(screen_dc);
    if (mem_dc == nullptr) {
        ReleaseDC(nullptr, screen_dc);
        return false;
    }

    const auto bitmap = CreateCompatibleBitmap(screen_dc, width, height);
    if (bitmap == nullptr) {
        DeleteDC(mem_dc);
        ReleaseDC(nullptr, screen_dc);
        return false;
    }

    const auto old_bitmap = SelectObject(mem_dc, bitmap);
    SetStretchBltMode(mem_dc, HALFTONE);

    const auto screen_w = GetSystemMetrics(SM_CXSCREEN);
    const auto screen_h = GetSystemMetrics(SM_CYSCREEN);
    const auto copied = StretchBlt(
        mem_dc,
        0,
        0,
        width,
        height,
        screen_dc,
        0,
        0,
        screen_w,
        screen_h,
        SRCCOPY | CAPTUREBLT
    );

    bool ok = false;
    if (copied != FALSE) {
        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;  // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;

        output.resize(static_cast<size_t>(width) * static_cast<size_t>(height) * 4U);
        const auto rows = GetDIBits(
            mem_dc,
            bitmap,
            0,
            static_cast<UINT>(height),
            output.data(),
            &bmi,
            DIB_RGB_COLORS
        );
        ok = rows == height;
    }

    SelectObject(mem_dc, old_bitmap);
    DeleteObject(bitmap);
    DeleteDC(mem_dc);
    ReleaseDC(nullptr, screen_dc);
    return ok;
}

bool sha256_hex(const std::vector<unsigned char>& input, std::string& output) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD object_length = 0;
    DWORD hash_length = 0;
    DWORD bytes = 0;
    std::vector<unsigned char> object;
    std::vector<unsigned char> digest;

    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) {
        return false;
    }
    if (BCryptGetProperty(
            alg,
            BCRYPT_OBJECT_LENGTH,
            reinterpret_cast<PUCHAR>(&object_length),
            sizeof(object_length),
            &bytes,
            0
        ) != 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        return false;
    }
    if (BCryptGetProperty(
            alg,
            BCRYPT_HASH_LENGTH,
            reinterpret_cast<PUCHAR>(&hash_length),
            sizeof(hash_length),
            &bytes,
            0
        ) != 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        return false;
    }

    object.resize(object_length);
    digest.resize(hash_length);
    if (BCryptCreateHash(alg, &hash, object.data(), object_length, nullptr, 0, 0) != 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        return false;
    }
    if (BCryptHashData(hash, const_cast<PUCHAR>(input.data()), static_cast<ULONG>(input.size()), 0) != 0) {
        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(alg, 0);
        return false;
    }
    if (BCryptFinishHash(hash, digest.data(), hash_length, 0) != 0) {
        BCryptDestroyHash(hash);
        BCryptCloseAlgorithmProvider(alg, 0);
        return false;
    }

    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);

    static constexpr char kHex[] = "0123456789abcdef";
    output.resize(static_cast<size_t>(hash_length) * 2U);
    for (size_t i = 0; i < digest.size(); ++i) {
        const auto byte = digest[i];
        output[i * 2] = kHex[(byte >> 4) & 0xF];
        output[i * 2 + 1] = kHex[byte & 0xF];
    }
    return true;
}

std::string trim_copy(const std::string& value) {
    size_t left = 0;
    while (left < value.size() && std::isspace(static_cast<unsigned char>(value[left])) != 0) {
        left += 1;
    }
    size_t right = value.size();
    while (right > left && std::isspace(static_cast<unsigned char>(value[right - 1])) != 0) {
        right -= 1;
    }
    return value.substr(left, right - left);
}

void uppercase_inplace(std::string& value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::toupper(ch));
    });
}

bool resolve_virtual_key(const std::string& token, WORD& key) {
    if (token == "ESC" || token == "ESCAPE") {
        key = VK_ESCAPE;
        return true;
    }
    if (token == "TAB") {
        key = VK_TAB;
        return true;
    }
    if (token == "ENTER" || token == "RETURN") {
        key = VK_RETURN;
        return true;
    }
    if (token == "SPACE") {
        key = VK_SPACE;
        return true;
    }
    if (token == "LEFT") {
        key = VK_LEFT;
        return true;
    }
    if (token == "RIGHT") {
        key = VK_RIGHT;
        return true;
    }
    if (token == "UP") {
        key = VK_UP;
        return true;
    }
    if (token == "DOWN") {
        key = VK_DOWN;
        return true;
    }
    if (token == "F1") {
        key = VK_F1;
        return true;
    }
    if (token == "F2") {
        key = VK_F2;
        return true;
    }
    if (token == "F3") {
        key = VK_F3;
        return true;
    }
    if (token == "F4") {
        key = VK_F4;
        return true;
    }
    if (token == "I") {
        key = 0x49;
        return true;
    }
    if (token == "C") {
        key = 0x43;
        return true;
    }
    return false;
}

bool send_key_press(WORD key) {
    INPUT inputs[2]{};
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].ki.wVk = key;
    inputs[0].ki.dwFlags = 0;

    inputs[1].type = INPUT_KEYBOARD;
    inputs[1].ki.wVk = key;
    inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

    const UINT sent = SendInput(2U, inputs, sizeof(INPUT));
    return sent == 2U;
}

}  // namespace

int ika_native_lock_input() {
    return BlockInput(TRUE) ? 1 : 0;
}

int ika_native_unlock_input() {
    return BlockInput(FALSE) ? 1 : 0;
}

int ika_native_capture_frame_hash(char* output_buffer, int output_buffer_length) {
    if (output_buffer == nullptr || output_buffer_length < 65) {
        return 0;
    }

    std::vector<unsigned char> pixels;
    if (!capture_desktop_rgba(pixels, 320, 180)) {
        return 0;
    }

    std::string hash;
    if (!sha256_hex(pixels, hash)) {
        return 0;
    }

    std::strncpy(output_buffer, hash.c_str(), static_cast<size_t>(output_buffer_length - 1));
    output_buffer[output_buffer_length - 1] = '\0';
    return static_cast<int>(hash.size());
}

int ika_native_execute_scan_script(const char* script, int step_delay_ms) {
    if (script == nullptr) {
        return 0;
    }

    std::string sequence(script);
    if (sequence.empty()) {
        return 1;
    }

    const int delay_ms = step_delay_ms <= 0 ? 120 : step_delay_ms;
    size_t cursor = 0;
    while (cursor <= sequence.size()) {
        size_t next = sequence.find(',', cursor);
        std::string token = next == std::string::npos
            ? sequence.substr(cursor)
            : sequence.substr(cursor, next - cursor);
        token = trim_copy(token);
        uppercase_inplace(token);
        if (!token.empty()) {
            WORD key = 0;
            if (!resolve_virtual_key(token, key)) {
                return 0;
            }
            if (!send_key_press(key)) {
                return 0;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
        }

        if (next == std::string::npos) {
            break;
        }
        cursor = next + 1;
    }

    return 1;
}
