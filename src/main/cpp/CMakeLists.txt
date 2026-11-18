# CMake 配置：CPU 亲和性 JNI
#
# ★ 这个 so 只做一件事：让检测线程绑到小核（cpu0-3）。
#   推理本身走 MediaPipe Tasks（纯 AAR，自带 native 库），不经过这里。
#
# 为什么必须有 native：
#   Android 禁止 app 改【别的进程】的亲和性（需 CAP_SYS_NICE），
#   但允许线程改【自己的】。Java 没有暴露这个 API，只能走 JNI。

cmake_minimum_required(VERSION 3.22)
project(sentinel_affinity CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(sentinel_affinity SHARED cpu_affinity.cpp)

target_compile_options(sentinel_affinity PRIVATE
    -Wall
    -O2
    -fomit-frame-pointer)

find_library(log-lib log)
find_library(jnigraphics-lib jnigraphics)

target_link_libraries(sentinel_affinity
    ${log-lib}
    ${jnigraphics-lib})
