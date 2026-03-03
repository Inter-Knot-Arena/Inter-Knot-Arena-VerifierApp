#include "ika_native.h"

#include <array>
#include <cstring>
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
