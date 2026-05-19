cmake_minimum_required(VERSION 3.10)
project(Texture2DDecoderNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED True)

# Macro used in dllexport.h to export functions
add_compile_definitions(_T2D_DLL)

# Source files based on the .vcxproj
set(SOURCES
    astc.cpp
    atc.cpp
    bcn.cpp
    crunch.cpp
    dllmain.cpp
    etc.cpp
    pvrtc.cpp
    unitycrunch.cpp
)

# Create a shared library (.so on Linux, .dll on Windows)
add_library(Texture2DDecoderNative SHARED ${SOURCES})

# Include directories
target_include_directories(Texture2DDecoderNative PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${CMAKE_CURRENT_SOURCE_DIR}/crunch
    ${CMAKE_CURRENT_SOURCE_DIR}/fp16
    ${CMAKE_CURRENT_SOURCE_DIR}/unitycrunch
)
