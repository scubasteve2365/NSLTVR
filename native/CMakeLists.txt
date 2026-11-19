cmake_minimum_required(VERSION 3.20)
project(NSLTVRNative C CXX)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
set(OPENXR_DIR "C:/VRLab/bin/openxr_loader_1.1.65" CACHE PATH "Khronos OpenXR loader folder")
set(MINHOOK_DIR "C:/VRLab/bin/minhook" CACHE PATH "MinHook source folder")
add_library(NSLTVRNative SHARED NSLTVRNative.cpp
    ${MINHOOK_DIR}/src/buffer.c ${MINHOOK_DIR}/src/hook.c ${MINHOOK_DIR}/src/trampoline.c
    ${MINHOOK_DIR}/src/hde/hde64.c ${MINHOOK_DIR}/src/hde/hde32.c)
target_include_directories(NSLTVRNative PRIVATE ${OPENXR_DIR}/include ${MINHOOK_DIR}/include)
target_link_libraries(NSLTVRNative PRIVATE d3d11 dxgi advapi32)
set_target_properties(NSLTVRNative PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/../../build")
add_custom_command(TARGET NSLTVRNative POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${OPENXR_DIR}/x64/bin/openxr_loader.dll" "${CMAKE_SOURCE_DIR}/../../build/openxr_loader.dll")
