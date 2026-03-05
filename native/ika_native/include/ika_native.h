#pragma once

#ifdef _WIN32
#define IKA_NATIVE_API __declspec(dllexport)
#else
#define IKA_NATIVE_API
#endif

extern "C" {
IKA_NATIVE_API int ika_native_lock_input();
IKA_NATIVE_API int ika_native_unlock_input();
IKA_NATIVE_API int ika_native_capture_frame_hash(char* output_buffer, int output_buffer_length);
IKA_NATIVE_API int ika_native_execute_scan_script(const char* script, int step_delay_ms);
}
