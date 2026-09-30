cmake_minimum_required(VERSION 3.21)
project(trenal_wasm C ASM)

# WAMR_ROOT_DIR: a wasm-micro-runtime checkout (see build-wasm.sh for the pinned tag).
if(NOT DEFINED WAMR_ROOT_DIR)
  message(FATAL_ERROR "set -DWAMR_ROOT_DIR=<wasm-micro-runtime checkout>")
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "iOS" OR APPLE)
  set(WAMR_BUILD_PLATFORM "darwin")
else()
  set(WAMR_BUILD_PLATFORM "linux")
endif()
if(CMAKE_OSX_ARCHITECTURES MATCHES "arm64" OR CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64)")
  set(WAMR_BUILD_TARGET "AARCH64")
else()
  set(WAMR_BUILD_TARGET "X86_64")
endif()

# Interpreter only: iOS forbids JIT, and AOT would mean shipping per-tool native code.
set(WAMR_BUILD_INTERP 1)
set(WAMR_BUILD_FAST_INTERP 1)
set(WAMR_BUILD_AOT 0)
set(WAMR_BUILD_JIT 0)
set(WAMR_BUILD_FAST_JIT 0)
set(WAMR_BUILD_LIBC_WASI 1)
set(WAMR_BUILD_LIBC_BUILTIN 0)
set(WAMR_BUILD_BULK_MEMORY 1)
set(WAMR_BUILD_REF_TYPES 1)
set(WAMR_BUILD_SIMD 0)
# Needed so trenal_wasm_terminate (Ctrl+C) can stop a busy loop: the interpreter checks
# suspend flags at branch targets only with thread management on.
set(WAMR_BUILD_THREAD_MGR 1)
set(WAMR_BUILD_LIB_PTHREAD 0)
# No 8 GB guard-page reservations: iOS caps an app's virtual address space.
set(WAMR_DISABLE_HW_BOUND_CHECK 1)
set(WAMR_DISABLE_STACK_HW_BOUND_CHECK 1)

include(${WAMR_ROOT_DIR}/build-scripts/runtime_lib.cmake)

add_library(trenal-wasm SHARED shim.c ${WAMR_RUNTIME_LIB_SOURCE})
target_include_directories(trenal-wasm PRIVATE ${WAMR_ROOT_DIR}/core/iwasm/include)
set_target_properties(trenal-wasm PROPERTIES C_VISIBILITY_PRESET hidden)
if(NOT APPLE)
  target_link_libraries(trenal-wasm PRIVATE pthread m dl)
endif()
