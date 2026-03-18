#include "ika_native.h"

#include <array>
#include <algorithm>
#include <chrono>
#include <cctype>
#include <cmath>
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

bool is_extended_key(WORD key) {
    switch (key) {
        case VK_LEFT:
        case VK_RIGHT:
        case VK_UP:
        case VK_DOWN:
        case VK_PRIOR:
        case VK_NEXT:
        case VK_END:
        case VK_HOME:
        case VK_INSERT:
        case VK_DELETE:
            return true;
        default:
            return false;
    }
}

bool send_key_press(WORD key) {
    const auto scan_code = static_cast<WORD>(MapVirtualKeyW(key, MAPVK_VK_TO_VSC_EX));
    if (scan_code == 0) {
        return false;
    }

    DWORD base_flags = KEYEVENTF_SCANCODE;
    if (is_extended_key(key)) {
        base_flags |= KEYEVENTF_EXTENDEDKEY;
    }

    INPUT key_down{};
    key_down.type = INPUT_KEYBOARD;
    key_down.ki.wVk = 0;
    key_down.ki.wScan = scan_code;
    key_down.ki.dwFlags = base_flags;

    if (SendInput(1U, &key_down, sizeof(INPUT)) != 1U) {
        return false;
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(35));

    INPUT key_up{};
    key_up.type = INPUT_KEYBOARD;
    key_up.ki.wVk = 0;
    key_up.ki.wScan = scan_code;
    key_up.ki.dwFlags = base_flags | KEYEVENTF_KEYUP;
    return SendInput(1U, &key_up, sizeof(INPUT)) == 1U;
}

bool parse_wait_command(const std::string& token, int& wait_ms) {
    static constexpr std::array<const char*, 2> kPrefixes{ "WAIT:", "SLEEP:" };
    for (const auto* prefix : kPrefixes) {
        const auto prefix_len = std::strlen(prefix);
        if (token.rfind(prefix, 0) != 0) {
            continue;
        }

        const auto raw = trim_copy(token.substr(prefix_len));
        if (raw.empty()) {
            return false;
        }

        try {
            const auto parsed = std::stoi(raw);
            if (parsed < 0) {
                return false;
            }
            wait_ms = parsed;
            return true;
        } catch (...) {
            return false;
        }
    }

    return false;
}

bool try_parse_coordinate_value(const std::string& raw, double& value) {
    const auto trimmed = trim_copy(raw);
    if (trimmed.empty()) {
        return false;
    }

    try {
        size_t consumed = 0;
        value = std::stod(trimmed, &consumed);
        return consumed == trimmed.size();
    } catch (...) {
        return false;
    }
}

bool resolve_click_point(double x_value, double y_value, LONG& x, LONG& y) {
    RECT rect{};
    auto rect_left = 0L;
    auto rect_top = 0L;
    auto rect_width = static_cast<LONG>(GetSystemMetrics(SM_CXSCREEN));
    auto rect_height = static_cast<LONG>(GetSystemMetrics(SM_CYSCREEN));

    const auto foreground_window = GetForegroundWindow();
    if (foreground_window != nullptr) {
        RECT client_rect{};
        POINT client_origin{};
        if (GetClientRect(foreground_window, &client_rect) != FALSE &&
            ClientToScreen(foreground_window, &client_origin) != FALSE) {
            rect_left = client_origin.x;
            rect_top = client_origin.y;
            rect_width = std::max<LONG>(0, client_rect.right - client_rect.left);
            rect_height = std::max<LONG>(0, client_rect.bottom - client_rect.top);
        } else if (GetWindowRect(foreground_window, &rect) != FALSE) {
            rect_left = rect.left;
            rect_top = rect.top;
            rect_width = std::max<LONG>(0, rect.right - rect.left);
            rect_height = std::max<LONG>(0, rect.bottom - rect.top);
        }
    }

    if (rect_width <= 0 || rect_height <= 0) {
        return false;
    }

    const auto use_relative = x_value >= 0.0 && x_value <= 1.0 && y_value >= 0.0 && y_value <= 1.0;
    if (use_relative) {
        x = rect_left + static_cast<LONG>(std::llround(x_value * static_cast<double>(rect_width - 1)));
        y = rect_top + static_cast<LONG>(std::llround(y_value * static_cast<double>(rect_height - 1)));
        return true;
    }

    x = static_cast<LONG>(std::llround(x_value));
    y = static_cast<LONG>(std::llround(y_value));
    return true;
}

bool parse_click_command(const std::string& token, LONG& x, LONG& y, int& click_count) {
    std::string payload;
    if (token.rfind("CLICK:", 0) == 0) {
        payload = token.substr(std::strlen("CLICK:"));
        click_count = 1;
    } else if (token.rfind("DOUBLECLICK:", 0) == 0) {
        payload = token.substr(std::strlen("DOUBLECLICK:"));
        click_count = 2;
    } else if (token.rfind("DBLCLICK:", 0) == 0) {
        payload = token.substr(std::strlen("DBLCLICK:"));
        click_count = 2;
    } else {
        return false;
    }

    const auto separator = payload.find(':');
    if (separator == std::string::npos) {
        return false;
    }

    double x_value = 0.0;
    double y_value = 0.0;
    if (!try_parse_coordinate_value(payload.substr(0, separator), x_value) ||
        !try_parse_coordinate_value(payload.substr(separator + 1), y_value)) {
        return false;
    }

    return resolve_click_point(x_value, y_value, x, y);
}

bool send_left_click(LONG x, LONG y, int click_count) {
    if (click_count <= 0) {
        return false;
    }

    if (SetCursorPos(static_cast<int>(x), static_cast<int>(y)) == FALSE) {
        const auto virtual_left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        const auto virtual_top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        const auto virtual_width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        const auto virtual_height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (virtual_width <= 1 || virtual_height <= 1) {
            return false;
        }

        const auto normalized_x = static_cast<LONG>(
            std::llround(((static_cast<double>(x - virtual_left) * 65535.0) / static_cast<double>(virtual_width - 1)))
        );
        const auto normalized_y = static_cast<LONG>(
            std::llround(((static_cast<double>(y - virtual_top) * 65535.0) / static_cast<double>(virtual_height - 1)))
        );

        INPUT move_input{};
        move_input.type = INPUT_MOUSE;
        move_input.mi.dx = normalized_x;
        move_input.mi.dy = normalized_y;
        move_input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        if (SendInput(1U, &move_input, sizeof(INPUT)) != 1U) {
            return false;
        }
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(40));

    for (int index = 0; index < click_count; ++index) {
        INPUT mouse_down{};
        mouse_down.type = INPUT_MOUSE;
        mouse_down.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
        if (SendInput(1U, &mouse_down, sizeof(INPUT)) != 1U) {
            return false;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(35));

        INPUT mouse_up{};
        mouse_up.type = INPUT_MOUSE;
        mouse_up.mi.dwFlags = MOUSEEVENTF_LEFTUP;
        if (SendInput(1U, &mouse_up, sizeof(INPUT)) != 1U) {
            return false;
        }

        if (index + 1 < click_count) {
            std::this_thread::sleep_for(std::chrono::milliseconds(85));
        }
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
            int wait_ms = 0;
            if (parse_wait_command(token, wait_ms)) {
                std::this_thread::sleep_for(std::chrono::milliseconds(wait_ms));
            } else {
                LONG click_x = 0;
                LONG click_y = 0;
                int click_count = 0;
                if (parse_click_command(token, click_x, click_y, click_count)) {
                    if (!send_left_click(click_x, click_y, click_count)) {
                        return 0;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
                } else {
                    WORD key = 0;
                    if (!resolve_virtual_key(token, key)) {
                        return 0;
                    }
                    if (!send_key_press(key)) {
                        return 0;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(delay_ms));
                }
            }
        }

        if (next == std::string::npos) {
            break;
        }
        cursor = next + 1;
    }

    return 1;
}
