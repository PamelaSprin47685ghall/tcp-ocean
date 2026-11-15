cmake_minimum_required(VERSION 3.22.1)
project(tbtjni)

add_library(tbtjni SHARED tbt_jni.c)

# Single copy of the C ABI header, next to the NativeAOT exports.
# cpp -> main -> src -> app -> vpn -> android -> repo root (6).
set(TBT_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../../src/Tbt.NativeLib")
target_include_directories(tbtjni PRIVATE "${TBT_ROOT}")

add_library(tbt SHARED IMPORTED)
set_target_properties(tbt PROPERTIES IMPORTED_LOCATION
    "${CMAKE_CURRENT_SOURCE_DIR}/../jniLibs/${ANDROID_ABI}/libtbt.so")

find_library(log-lib log)
target_link_libraries(tbtjni tbt ${log-lib})
