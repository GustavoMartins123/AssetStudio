#pragma once
#include <stdint.h>
#include <string.h>
#include "endianness.h"

#if defined(_M_X64) || defined(_M_IX86) || defined(__x86_64__) || defined(__i386__)
#define T2D_ARCH_X86 1
#else
#define T2D_ARCH_X86 0
#endif

#if T2D_ARCH_X86 && defined(__LITTLE_ENDIAN__)
#define T2D_SIMD_X86 1
#if defined(_MSC_VER)
#include <intrin.h>
#include <immintrin.h>
#define T2D_TARGET_SSE2
#define T2D_TARGET_SSE41
#define T2D_TARGET_AVX2
#elif defined(__GNUC__) || defined(__clang__)
#include <cpuid.h>
#include <immintrin.h>
#define T2D_TARGET_SSE2  __attribute__((target("sse2")))
#define T2D_TARGET_SSE41 __attribute__((target("ssse3,sse4.1")))
#define T2D_TARGET_AVX2  __attribute__((target("ssse3,sse4.1,avx,avx2")))
#else
#define T2D_SIMD_X86 0
#define T2D_TARGET_SSE2
#define T2D_TARGET_SSE41
#define T2D_TARGET_AVX2
#endif
#else
#define T2D_SIMD_X86 0
#define T2D_TARGET_SSE2
#define T2D_TARGET_SSE41
#define T2D_TARGET_AVX2
#endif

enum class SimdLevel : uint8_t {
    Scalar = 0,
    SSE2 = 1,
    SSE41 = 2,
    AVX2 = 3
};

SimdLevel t2d_get_simd_level() noexcept;

static inline uint16_t t2d_load_u16_le(const uint8_t* p) noexcept {
    uint16_t v;
    memcpy(&v, p, sizeof(v));
    return lton16(v);
}

static inline uint32_t t2d_load_u32_le(const uint8_t* p) noexcept {
    uint32_t v;
    memcpy(&v, p, sizeof(v));
    return lton32(v);
}

static inline uint64_t t2d_load_u64_le(const uint8_t* p) noexcept {
    uint64_t v;
    memcpy(&v, p, sizeof(v));
    return lton64(v);
}

static inline uint64_t t2d_load_u64_be(const uint8_t* p) noexcept {
    uint64_t v;
    memcpy(&v, p, sizeof(v));
    return bton64(v);
}

static inline void t2d_copy_block_4x4(long bx, long by, long w, long h, const uint32_t* buffer, uint32_t* image) noexcept {
    const long x = bx * 4;
    const long y = by * 4;
    if (x + 4 <= w && y + 4 <= h) {
        uint32_t* dst = image + y * w + x;
        memcpy(dst, buffer, 16);
        memcpy(dst + w, buffer + 4, 16);
        memcpy(dst + w * 2, buffer + 8, 16);
        memcpy(dst + w * 3, buffer + 12, 16);
    } else {
        const long xl = (x + 4 > w ? w - x : 4) * 4;
        const uint32_t* buffer_end = buffer + 16;
        for (long yy = y; buffer < buffer_end && yy < h; buffer += 4, yy++) {
            memcpy(image + yy * w + x, buffer, (size_t)xl);
        }
    }
}
