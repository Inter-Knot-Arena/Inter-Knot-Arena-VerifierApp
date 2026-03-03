cmake_minimum_required(VERSION 3.24)
project(ika_native LANGUAGES CXX)

add_library(ika_native SHARED
    src/ika_native.cpp
)

target_include_directories(ika_native
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_features(ika_native PRIVATE cxx_std_20)

target_link_libraries(ika_native PRIVATE
    bcrypt
    Gdi32
    User32
)

if(MSVC)
    target_compile_options(ika_native PRIVATE /W4 /EHsc)
else()
    target_compile_options(ika_native PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(ika_native PROPERTIES
    OUTPUT_NAME "ika_native"
)
