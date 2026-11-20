#include "simd_dispatch.h"

#if T2D_SIMD_X86

static inline void t2d_cpuid(int info[4], int leaf, int subleaf = 0) noexcept {
#if defined(_MSC_VER)
    __cpuidex(info, leaf, subleaf);
#elif defined(__GNUC__) || defined(__clang__)
    uint32_t a = 0, b = 0, c = 0, d = 0;
    __cpuid_count(leaf, subleaf, a, b, c, d);
    info[0] = static_cast<int>(a);
    info[1] = static_cast<int>(b);
    info[2] = static_cast<int>(c);
    info[3] = static_cast<int>(d);
#else
    (void)leaf;
    (void)subleaf;
    info[0] = info[1] = info[2] = info[3] = 0;
#endif
}

static inline uint64_t t2d_xgetbv(uint32_t xcr) noexcept {
#if defined(_MSC_VER)
    return _xgetbv(xcr);
#elif defined(__GNUC__) || defined(__clang__)
    uint32_t eax = 0, edx = 0;
    __asm__ __volatile__("xgetbv" : "=a"(eax), "=d"(edx) : "c"(xcr));
    return (static_cast<uint64_t>(edx) << 32) | eax;
#else
    (void)xcr;
    return 0;
#endif
}

static SimdLevel detect_simd_level() noexcept {
    int info[4] = {};
    t2d_cpuid(info, 0, 0);
    const int max_leaf = info[0];
    if (max_leaf < 1) {
        return SimdLevel::Scalar;
    }

    t2d_cpuid(info, 1, 0);
    const uint32_t ecx = static_cast<uint32_t>(info[2]);
    const uint32_t edx = static_cast<uint32_t>(info[3]);

    const bool has_sse2 = (edx & (1u << 26)) != 0;
    if (!has_sse2) {
        return SimdLevel::Scalar;
    }

    const bool has_ssse3 = (ecx & (1u << 9)) != 0;
    const bool has_sse41 = (ecx & (1u << 19)) != 0;
    if (!has_ssse3 || !has_sse41) {
        return SimdLevel::SSE2;
    }

    const bool has_osxsave = (ecx & (1u << 27)) != 0;
    const bool has_avx = (ecx & (1u << 28)) != 0;
    if (has_osxsave && has_avx && max_leaf >= 7) {
        const uint64_t xcr0 = t2d_xgetbv(0);
        // Check that OS saves both XMM (bit 1) and YMM (bit 2) register state
        if ((xcr0 & 0x6u) == 0x6u) {
            int info7[4] = {};
            t2d_cpuid(info7, 7, 0);
            const uint32_t ebx7 = static_cast<uint32_t>(info7[1]);
            const bool has_avx2 = (ebx7 & (1u << 5)) != 0;
            if (has_avx2) {
                return SimdLevel::AVX2;
            }
        }
    }

    return SimdLevel::SSE41;
}

#else

static SimdLevel detect_simd_level() noexcept {
    return SimdLevel::Scalar;
}

#endif

SimdLevel t2d_get_simd_level() noexcept {
    static const SimdLevel s_level = detect_simd_level();
    return s_level;
}
