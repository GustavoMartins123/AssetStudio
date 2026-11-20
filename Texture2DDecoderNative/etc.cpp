#include "etc.h"
#include <stdint.h>
#include <string.h>
#include "color.h"
#include "simd_dispatch.h"

static const uint_fast8_t WriteOrderTable[16] = {0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15};
static const uint_fast8_t WriteOrderTableRev[16] = {15, 11, 7, 3, 14, 10, 6, 2, 13, 9, 5, 1, 12, 8, 4, 0};
static const uint_fast8_t Etc1ModifierTable[8][2] = {{2, 8},   {5, 17},  {9, 29},   {13, 42},
                                              {18, 60}, {24, 80}, {33, 106}, {47, 183}};
static const uint_fast8_t Etc2aModifierTable[2][8][2] = {
  {{0, 8}, {0, 17}, {0, 29}, {0, 42}, {0, 60}, {0, 80}, {0, 106}, {0, 183}},
  {{2, 8}, {5, 17}, {9, 29}, {13, 42}, {18, 60}, {24, 80}, {33, 106}, {47, 183}}};
static const uint_fast8_t Etc1SubblockTable[2][16] = {{0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1},
                                               {0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1}};
static const uint_fast8_t Etc2DistanceTable[8] = {3, 6, 11, 16, 23, 32, 41, 64};
static const int_fast8_t Etc2AlphaModTable[16][8] = {
  {-3, -6, -9, -15, 2, 5, 8, 14}, {-3, -7, -10, -13, 2, 6, 9, 12}, {-2, -5, -8, -13, 1, 4, 7, 12},
  {-2, -4, -6, -13, 1, 3, 5, 12}, {-3, -6, -8, -12, 2, 5, 7, 11},  {-3, -7, -9, -11, 2, 6, 8, 10},
  {-4, -7, -8, -11, 3, 6, 7, 10}, {-3, -5, -8, -11, 2, 4, 7, 10},  {-2, -6, -8, -10, 1, 5, 7, 9},
  {-2, -5, -8, -10, 1, 4, 7, 9},  {-2, -4, -8, -10, 1, 3, 7, 9},   {-2, -5, -7, -10, 1, 4, 6, 9},
  {-3, -4, -7, -10, 2, 3, 6, 9},  {-1, -2, -3, -10, 0, 1, 2, 9},   {-4, -6, -8, -9, 3, 5, 7, 8},
  {-3, -5, -7, -9, 2, 4, 6, 8}};

static inline uint_fast8_t clamp(const int n) noexcept {
    return static_cast<uint_fast8_t>(n < 0 ? 0 : n > 255 ? 255 : n);
}

static inline uint_fast8_t to_u8(const int n) noexcept {
    return static_cast<uint_fast8_t>(n);
}

static inline uint32_t applicate_color(const uint_fast8_t c[3], int_fast16_t m) noexcept {
    return static_cast<uint32_t>(color(clamp(c[0] + m), clamp(c[1] + m), clamp(c[2] + m), 255));
}

static inline uint32_t applicate_color_alpha(const uint_fast8_t c[3], int_fast16_t m, int transparent) noexcept {
    return static_cast<uint32_t>(color(clamp(c[0] + m), clamp(c[1] + m), clamp(c[2] + m), transparent ? 0 : 255));
}

static inline uint32_t applicate_color_raw(const uint_fast8_t c[3]) noexcept {
    return static_cast<uint32_t>(color(c[0], c[1], c[2], 255));
}

static inline void build_etc1_subblock_palettes(const uint_fast8_t c[2][3], const uint_fast8_t code[2], uint32_t pal[2][4]) noexcept {
#if T2D_SIMD_X86
    for (int s = 0; s < 2; s++) {
        const uint32_t base = static_cast<uint32_t>(color(c[s][0], c[s][1], c[s][2], 255));
        const int m0 = static_cast<int>(Etc1ModifierTable[code[s]][0]) * 0x00010101;
        const int m1 = static_cast<int>(Etc1ModifierTable[code[s]][1]) * 0x00010101;
        const __m128i vbase = _mm_set1_epi32(static_cast<int>(base));
        const __m128i vadd = _mm_setr_epi32(m0, m1, 0, 0);
        const __m128i vsub = _mm_setr_epi32(0, 0, m0, m1);
        const __m128i vpal = _mm_subs_epu8(_mm_adds_epu8(vbase, vadd), vsub);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(pal[s]), vpal);
    }
#else
    for (int s = 0; s < 2; s++) {
        const int_fast16_t m0 = Etc1ModifierTable[code[s]][0];
        const int_fast16_t m1 = Etc1ModifierTable[code[s]][1];
        pal[s][0] = applicate_color(c[s], m0);
        pal[s][1] = applicate_color(c[s], m1);
        pal[s][2] = applicate_color(c[s], -m0);
        pal[s][3] = applicate_color(c[s], -m1);
    }
#endif
}

static inline void write_etc1_subblocks(const uint32_t pal[2][4], const uint_fast8_t* table, uint_fast16_t j, uint_fast32_t k, uint32_t* outbuf) noexcept {
    k <<= 1;
    for (int i = 0; i < 16; i++, j >>= 1, k >>= 1) {
        outbuf[WriteOrderTable[i]] = pal[table[i]][(k & 2) | (j & 1)];
    }
}

static void decode_etc1_block(const uint8_t *data, uint32_t *outbuf) noexcept {
    const uint_fast8_t code[2] = {to_u8(data[3] >> 5), to_u8(data[3] >> 2 & 7)};
    const uint_fast8_t *table = Etc1SubblockTable[data[3] & 1];
    uint_fast8_t c[2][3];
    if (data[3] & 2) {
        c[0][0] = data[0] & 0xf8;
        c[0][1] = data[1] & 0xf8;
        c[0][2] = data[2] & 0xf8;
        c[1][0] = static_cast<uint8_t>(c[0][0] + (data[0] << 3 & 0x18) - (data[0] << 3 & 0x20));
        c[1][1] = static_cast<uint8_t>(c[0][1] + (data[1] << 3 & 0x18) - (data[1] << 3 & 0x20));
        c[1][2] = static_cast<uint8_t>(c[0][2] + (data[2] << 3 & 0x18) - (data[2] << 3 & 0x20));
        c[0][0] |= c[0][0] >> 5;
        c[0][1] |= c[0][1] >> 5;
        c[0][2] |= c[0][2] >> 5;
        c[1][0] |= c[1][0] >> 5;
        c[1][1] |= c[1][1] >> 5;
        c[1][2] |= c[1][2] >> 5;
    } else {
        c[0][0] = (data[0] & 0xf0) | (data[0] >> 4);
        c[1][0] = static_cast<uint8_t>((data[0] & 0x0f) | (data[0] << 4));
        c[0][1] = (data[1] & 0xf0) | (data[1] >> 4);
        c[1][1] = static_cast<uint8_t>((data[1] & 0x0f) | (data[1] << 4));
        c[0][2] = (data[2] & 0xf0) | (data[2] >> 4);
        c[1][2] = static_cast<uint8_t>((data[2] & 0x0f) | (data[2] << 4));
    }

    uint32_t pal[2][4];
    build_etc1_subblock_palettes(c, code, pal);
    const uint_fast16_t j = static_cast<uint_fast16_t>((data[6] << 8) | data[7]);
    const uint_fast32_t k = static_cast<uint_fast32_t>((data[4] << 8) | data[5]);
    write_etc1_subblocks(pal, table, j, k, outbuf);
}

static inline void decode_etc2_planar(const uint_fast8_t c[3][3], uint32_t* outbuf) noexcept {
#if T2D_SIMD_X86
    const int c0r = c[0][0], c0g = c[0][1], c0b = c[0][2];
    const int dxr = c[1][0] - c0r, dxg = c[1][1] - c0g, dxb = c[1][2] - c0b;
    const int dyr = c[2][0] - c0r, dyg = c[2][1] - c0g, dyb = c[2][2] - c0b;

    const __m128i vx = _mm_setr_epi16(0, 1, 2, 3, 0, 0, 0, 0);
    const __m128i v_rx = _mm_add_epi16(_mm_mullo_epi16(vx, _mm_set1_epi16(static_cast<short>(dxr))), _mm_set1_epi16(static_cast<short>(4 * c0r + 2)));
    const __m128i v_gx = _mm_add_epi16(_mm_mullo_epi16(vx, _mm_set1_epi16(static_cast<short>(dxg))), _mm_set1_epi16(static_cast<short>(4 * c0g + 2)));
    const __m128i v_bx = _mm_add_epi16(_mm_mullo_epi16(vx, _mm_set1_epi16(static_cast<short>(dxb))), _mm_set1_epi16(static_cast<short>(4 * c0b + 2)));
    const __m128i v_ff = _mm_set1_epi16(255);

    for (int y = 0; y < 4; y++) {
        const __m128i vr = _mm_srai_epi16(_mm_add_epi16(v_rx, _mm_set1_epi16(static_cast<short>(y * dyr))), 2);
        const __m128i vg = _mm_srai_epi16(_mm_add_epi16(v_gx, _mm_set1_epi16(static_cast<short>(y * dyg))), 2);
        const __m128i vb = _mm_srai_epi16(_mm_add_epi16(v_bx, _mm_set1_epi16(static_cast<short>(y * dyb))), 2);

        const __m128i b_g = _mm_packus_epi16(vb, vg); // low 4 bytes: B0..B3, bytes 8..11: G0..G3
        const __m128i r_a = _mm_packus_epi16(vr, v_ff); // low 4 bytes: R0..R3, bytes 8..11: A0..A3
        const __m128i bg = _mm_unpacklo_epi8(b_g, _mm_srli_si128(b_g, 8));
        const __m128i ra = _mm_unpacklo_epi8(r_a, _mm_srli_si128(r_a, 8));
        const __m128i bgra = _mm_unpacklo_epi16(bg, ra);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(outbuf + y * 4), bgra);
    }
#else
    for (int y = 0, i = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++, i++) {
            uint8_t r = clamp((x * (c[1][0] - c[0][0]) + y * (c[2][0] - c[0][0]) + 4 * c[0][0] + 2) >> 2);
            uint8_t g = clamp((x * (c[1][1] - c[0][1]) + y * (c[2][1] - c[0][1]) + 4 * c[0][1] + 2) >> 2);
            uint8_t b = clamp((x * (c[1][2] - c[0][2]) + y * (c[2][2] - c[0][2]) + 4 * c[0][2] + 2) >> 2);
            outbuf[i] = static_cast<uint32_t>(color(r, g, b, 255));
        }
    }
#endif
}

static void decode_etc2_block(const uint8_t *data, uint32_t *outbuf) noexcept {
    uint_fast16_t j = static_cast<uint_fast16_t>((data[6] << 8) | data[7]);
    uint_fast32_t k = static_cast<uint_fast32_t>((data[4] << 8) | data[5]);
    uint_fast8_t c[3][3] = {};

    if (data[3] & 2) {
        uint_fast8_t r = data[0] & 0xf8;
        int_fast16_t dr = (data[0] << 3 & 0x18) - (data[0] << 3 & 0x20);
        uint_fast8_t g = data[1] & 0xf8;
        int_fast16_t dg = (data[1] << 3 & 0x18) - (data[1] << 3 & 0x20);
        uint_fast8_t b = data[2] & 0xf8;
        int_fast16_t db = (data[2] << 3 & 0x18) - (data[2] << 3 & 0x20);
        if (r + dr < 0 || r + dr > 255) {
            // T
            c[0][0] = (data[0] << 3 & 0xc0) | (data[0] << 4 & 0x30) | (data[0] >> 1 & 0xc) | (data[0] & 3);
            c[0][1] = (data[1] & 0xf0) | data[1] >> 4;
            c[0][2] = (data[1] & 0x0f) | data[1] << 4;
            c[1][0] = (data[2] & 0xf0) | data[2] >> 4;
            c[1][1] = (data[2] & 0x0f) | data[2] << 4;
            c[1][2] = (data[3] & 0xf0) | data[3] >> 4;
            const uint_fast8_t d = Etc2DistanceTable[(data[3] >> 1 & 6) | (data[3] & 1)];
            uint32_t color_set[4] = {applicate_color_raw(c[0]), applicate_color(c[1], d),
                                     applicate_color_raw(c[1]), applicate_color(c[1], -d)};
            k <<= 1;
            for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                outbuf[WriteOrderTable[i]] = color_set[(k & 2) | (j & 1)];
        } else if (g + dg < 0 || g + dg > 255) {
            // H
            c[0][0] = (data[0] << 1 & 0xf0) | (data[0] >> 3 & 0xf);
            c[0][1] = (data[0] << 5 & 0xe0) | (data[1] & 0x10);
            c[0][1] |= c[0][1] >> 4;
            c[0][2] = (data[1] & 8) | (data[1] << 1 & 6) | data[2] >> 7;
            c[0][2] |= c[0][2] << 4;
            c[1][0] = (data[2] << 1 & 0xf0) | (data[2] >> 3 & 0xf);
            c[1][1] = (data[2] << 5 & 0xe0) | (data[3] >> 3 & 0x10);
            c[1][1] |= c[1][1] >> 4;
            c[1][2] = (data[3] << 1 & 0xf0) | (data[3] >> 3 & 0xf);
            uint_fast8_t d = (data[3] & 4) | (data[3] << 1 & 2);
            if (c[0][0] > c[1][0] ||
                (c[0][0] == c[1][0] && (c[0][1] > c[1][1] || (c[0][1] == c[1][1] && c[0][2] >= c[1][2]))))
                ++d;
            d = Etc2DistanceTable[d];
            uint32_t color_set[4] = {applicate_color(c[0], d), applicate_color(c[0], -d), applicate_color(c[1], d),
                                     applicate_color(c[1], -d)};
            k <<= 1;
            for (int i = 0; i < 16; i++, j >>= 1, k >>= 1)
                outbuf[WriteOrderTable[i]] = color_set[(k & 2) | (j & 1)];
        } else if (b + db < 0 || b + db > 255) {
            // planar
            c[0][0] = (data[0] << 1 & 0xfc) | (data[0] >> 5 & 3);
            c[0][1] = (data[0] << 7 & 0x80) | (data[1] & 0x7e) | (data[0] & 1);
            c[0][2] = (data[1] << 7 & 0x80) | (data[2] << 2 & 0x60) | (data[2] << 3 & 0x18) | (data[3] >> 5 & 4);
            c[0][2] |= c[0][2] >> 6;
            c[1][0] = (data[3] << 1 & 0xf8) | (data[3] << 2 & 4) | (data[3] >> 5 & 3);
            c[1][1] = (data[4] & 0xfe) | data[4] >> 7;
            c[1][2] = (data[4] << 7 & 0x80) | (data[5] >> 1 & 0x7c);
            c[1][2] |= c[1][2] >> 6;
            c[2][0] = (data[5] << 5 & 0xe0) | (data[6] >> 3 & 0x1c) | (data[5] >> 1 & 3);
            c[2][1] = (data[6] << 3 & 0xf8) | (data[7] >> 5 & 0x6) | (data[6] >> 4 & 1);
            c[2][2] = data[7] << 2 | (data[7] >> 4 & 3);
            decode_etc2_planar(c, outbuf);
        } else {
            // differential
            const uint_fast8_t code[2] = {to_u8(data[3] >> 5), to_u8(data[3] >> 2 & 7)};
            const uint_fast8_t *table = Etc1SubblockTable[data[3] & 1];
            uint_fast8_t cd[2][3];
            cd[0][0] = r | r >> 5;
            cd[0][1] = g | g >> 5;
            cd[0][2] = b | b >> 5;
            cd[1][0] = r + dr;
            cd[1][1] = g + dg;
            cd[1][2] = b + db;
            cd[1][0] |= cd[1][0] >> 5;
            cd[1][1] |= cd[1][1] >> 5;
            cd[1][2] |= cd[1][2] >> 5;
            uint32_t pal[2][4];
            build_etc1_subblock_palettes(cd, code, pal);
            write_etc1_subblocks(pal, table, j, k, outbuf);
        }
    } else {
        // individual (diff bit == 0)
        const uint_fast8_t code[2] = {to_u8(data[3] >> 5), to_u8(data[3] >> 2 & 7)};
        const uint_fast8_t *table = Etc1SubblockTable[data[3] & 1];
        uint_fast8_t ci[2][3];
        ci[0][0] = (data[0] & 0xf0) | data[0] >> 4;
        ci[1][0] = (data[0] & 0x0f) | data[0] << 4;
        ci[0][1] = (data[1] & 0xf0) | data[1] >> 4;
        ci[1][1] = (data[1] & 0x0f) | data[1] << 4;
        ci[0][2] = (data[2] & 0xf0) | data[2] >> 4;
        ci[1][2] = (data[2] & 0x0f) | data[2] << 4;
        uint32_t pal[2][4];
        build_etc1_subblock_palettes(ci, code, pal);
        write_etc1_subblocks(pal, table, j, k, outbuf);
    }
}

static void decode_etc2a1_block(const uint8_t *data, uint32_t *outbuf) noexcept {
    uint_fast16_t j = static_cast<uint_fast16_t>((data[6] << 8) | data[7]);
    uint_fast32_t k = static_cast<uint_fast32_t>((data[4] << 8) | data[5]);
    uint_fast8_t c[3][3] = {};

    int obaq = data[3] >> 1 & 1;

    uint_fast8_t r = data[0] & 0xf8;
    int_fast16_t dr = (data[0] << 3 & 0x18) - (data[0] << 3 & 0x20);
    uint_fast8_t g = data[1] & 0xf8;
    int_fast16_t dg = (data[1] << 3 & 0x18) - (data[1] << 3 & 0x20);
    uint_fast8_t b = data[2] & 0xf8;
    int_fast16_t db = (data[2] << 3 & 0x18) - (data[2] << 3 & 0x20);
    if (r + dr < 0 || r + dr > 255) {
        // T
        c[0][0] = (data[0] << 3 & 0xc0) | (data[0] << 4 & 0x30) | (data[0] >> 1 & 0xc) | (data[0] & 3);
        c[0][1] = (data[1] & 0xf0) | data[1] >> 4;
        c[0][2] = (data[1] & 0x0f) | data[1] << 4;
        c[1][0] = (data[2] & 0xf0) | data[2] >> 4;
        c[1][1] = (data[2] & 0x0f) | data[2] << 4;
        c[1][2] = (data[3] & 0xf0) | data[3] >> 4;
        const uint_fast8_t d = Etc2DistanceTable[(data[3] >> 1 & 6) | (data[3] & 1)];
        uint32_t color_set[4] = {applicate_color_raw(c[0]), applicate_color(c[1], d), applicate_color_raw(c[1]),
                                 applicate_color(c[1], -d)};
        if (!obaq)
            color_set[2] &= TRANSPARENT_MASK;
        k <<= 1;
        for (int i = 0; i < 16; i++, j >>= 1, k >>= 1) {
            outbuf[WriteOrderTable[i]] = color_set[(k & 2) | (j & 1)];
        }
    } else if (g + dg < 0 || g + dg > 255) {
        // H
        c[0][0] = (data[0] << 1 & 0xf0) | (data[0] >> 3 & 0xf);
        c[0][1] = (data[0] << 5 & 0xe0) | (data[1] & 0x10);
        c[0][1] |= c[0][1] >> 4;
        c[0][2] = (data[1] & 8) | (data[1] << 1 & 6) | data[2] >> 7;
        c[0][2] |= c[0][2] << 4;
        c[1][0] = (data[2] << 1 & 0xf0) | (data[2] >> 3 & 0xf);
        c[1][1] = (data[2] << 5 & 0xe0) | (data[3] >> 3 & 0x10);
        c[1][1] |= c[1][1] >> 4;
        c[1][2] = (data[3] << 1 & 0xf0) | (data[3] >> 3 & 0xf);
        uint_fast8_t d = (data[3] & 4) | (data[3] << 1 & 2);
        if (c[0][0] > c[1][0] ||
            (c[0][0] == c[1][0] && (c[0][1] > c[1][1] || (c[0][1] == c[1][1] && c[0][2] >= c[1][2]))))
            ++d;
        d = Etc2DistanceTable[d];
        uint32_t color_set[4] = {applicate_color(c[0], d), applicate_color(c[0], -d), applicate_color(c[1], d),
                                 applicate_color(c[1], -d)};
        if (!obaq)
            color_set[2] &= TRANSPARENT_MASK;
        k <<= 1;
        for (int i = 0; i < 16; i++, j >>= 1, k >>= 1) {
            outbuf[WriteOrderTable[i]] = color_set[(k & 2) | (j & 1)];
        }
    } else if (b + db < 0 || b + db > 255) {
        // planar
        c[0][0] = (data[0] << 1 & 0xfc) | (data[0] >> 5 & 3);
        c[0][1] = (data[0] << 7 & 0x80) | (data[1] & 0x7e) | (data[0] & 1);
        c[0][2] = (data[1] << 7 & 0x80) | (data[2] << 2 & 0x60) | (data[2] << 3 & 0x18) | (data[3] >> 5 & 4);
        c[0][2] |= c[0][2] >> 6;
        c[1][0] = (data[3] << 1 & 0xf8) | (data[3] << 2 & 4) | (data[3] >> 5 & 3);
        c[1][1] = (data[4] & 0xfe) | data[4] >> 7;
        c[1][2] = (data[4] << 7 & 0x80) | (data[5] >> 1 & 0x7c);
        c[1][2] |= c[1][2] >> 6;
        c[2][0] = (data[5] << 5 & 0xe0) | (data[6] >> 3 & 0x1c) | (data[5] >> 1 & 3);
        c[2][1] = (data[6] << 3 & 0xf8) | (data[7] >> 5 & 0x6) | (data[6] >> 4 & 1);
        c[2][2] = data[7] << 2 | (data[7] >> 4 & 3);
        decode_etc2_planar(c, outbuf);
    } else {
        // differential
        const uint_fast8_t code[2] = {to_u8(data[3] >> 5), to_u8(data[3] >> 2 & 7)};
        const uint_fast8_t *table = Etc1SubblockTable[data[3] & 1];
        c[0][0] = r | r >> 5;
        c[0][1] = g | g >> 5;
        c[0][2] = b | b >> 5;
        c[1][0] = r + dr;
        c[1][1] = g + dg;
        c[1][2] = b + db;
        c[1][0] |= c[1][0] >> 5;
        c[1][1] |= c[1][1] >> 5;
        c[1][2] |= c[1][2] >> 5;
        uint32_t pal[2][4];
        for (int s = 0; s < 2; s++) {
            const int_fast16_t m0 = Etc2aModifierTable[obaq][code[s]][0];
            const int_fast16_t m1 = Etc2aModifierTable[obaq][code[s]][1];
            pal[s][0] = applicate_color_alpha(c[s], m0, 0);
            pal[s][1] = applicate_color_alpha(c[s], m1, 0);
            pal[s][2] = applicate_color_alpha(c[s], -m0, !obaq);
            pal[s][3] = applicate_color_alpha(c[s], -m1, 0);
        }
        write_etc1_subblocks(pal, table, j, k, outbuf);
    }
}

static void decode_etc2a8_block(const uint8_t *data, uint32_t *outbuf) noexcept {
    if (data[1] & 0xf0) {
        const int multiplier = data[1] >> 4;
        const int_fast8_t *table = Etc2AlphaModTable[data[1] & 0xf];
        uint8_t alut[8];
        const int base = data[0];
        for (int idx = 0; idx < 8; idx++) {
            alut[idx] = clamp(base + multiplier * table[idx]);
        }
        uint64_t l = t2d_load_u64_be(data);
        for (int i = 0; i < 16; i++, l >>= 3)
            reinterpret_cast<uint8_t*>(outbuf + WriteOrderTableRev[i])[3] = alut[l & 7];
    } else {
        const uint8_t a0 = data[0];
        for (int i = 0; i < 16; i++, outbuf++)
            reinterpret_cast<uint8_t*>(outbuf)[3] = a0;
    }
}

static void decode_eac_block(const uint8_t *data, int channel, uint32_t *outbuf) noexcept {
    int multiplier = (data[1] >> 1) & 0x78;
    if (multiplier == 0)
        multiplier = 1;
    const int_fast8_t *table = Etc2AlphaModTable[data[1] & 0xf];
    uint8_t lut[8];
    const int base8 = data[0] * 8 + 4;
    for (int idx = 0; idx < 8; idx++) {
        const int val = base8 + multiplier * table[idx];
        lut[idx] = static_cast<uint8_t>(val < 0 ? 0 : val >= 2048 ? 0xff : (val >> 3));
    }
    uint64_t l = t2d_load_u64_be(data);
    for (int i = 0; i < 16; i++, l >>= 3) {
        reinterpret_cast<uint8_t*>(outbuf + WriteOrderTableRev[i])[channel] = lut[l & 7];
    }
}

static void decode_eac_signed_block(const uint8_t *data, int channel, uint32_t *outbuf) noexcept {
    const int8_t base = static_cast<int8_t>(data[0]);
    int multiplier = (data[1] >> 1) & 0x78;
    if (multiplier == 0)
        multiplier = 1;
    const int_fast8_t *table = Etc2AlphaModTable[data[1] & 0xf];
    uint8_t lut[8];
    const int base8 = base * 8 + 1023;
    for (int idx = 0; idx < 8; idx++) {
        const int val = base8 + multiplier * table[idx];
        lut[idx] = static_cast<uint8_t>(val < 0 ? 0 : val >= 2048 ? 0xff : (val >> 3));
    }
    uint64_t l = t2d_load_u64_be(data);
    for (int i = 0; i < 16; i++, l >>= 3) {
        reinterpret_cast<uint8_t*>(outbuf + WriteOrderTableRev[i])[channel] = lut[l & 7];
    }
}

int decode_etc1(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 8) {
            decode_etc1_block(data, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_etc2(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 8) {
            decode_etc2_block(data, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_etc2a1(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 8) {
            decode_etc2a1_block(data, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_etc2a8(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 16) {
            decode_etc2_block(data + 8, buffer);
            decode_etc2a8_block(data, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_eacr(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    alignas(16) uint32_t base_buffer[16];
    for (int i = 0; i < 16; i++)
        base_buffer[i] = static_cast<uint32_t>(color(0, 0, 0, 255));
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 8) {
            memcpy(buffer, base_buffer, sizeof(buffer));
            decode_eac_block(data, 2, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_eacr_signed(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    alignas(16) uint32_t base_buffer[16];
    for (int i = 0; i < 16; i++)
        base_buffer[i] = static_cast<uint32_t>(color(0, 0, 0, 255));
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 8) {
            memcpy(buffer, base_buffer, sizeof(buffer));
            decode_eac_signed_block(data, 2, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_eacrg(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    alignas(16) uint32_t base_buffer[16];
    for (int i = 0; i < 16; i++)
        base_buffer[i] = static_cast<uint32_t>(color(0, 0, 0, 255));
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 16) {
            memcpy(buffer, base_buffer, sizeof(buffer));
            decode_eac_block(data, 2, buffer);
            decode_eac_block(data + 8, 1, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}

int decode_eacrg_signed(const uint8_t *data, const long w, const long h, uint32_t *image) {
    if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
        return 0;
    long num_blocks_x = (w + 3) / 4;
    long num_blocks_y = (h + 3) / 4;
    alignas(16) uint32_t buffer[16];
    alignas(16) uint32_t base_buffer[16];
    for (int i = 0; i < 16; i++)
        base_buffer[i] = static_cast<uint32_t>(color(0, 0, 0, 255));
    for (long by = 0; by < num_blocks_y; by++) {
        for (long bx = 0; bx < num_blocks_x; bx++, data += 16) {
            memcpy(buffer, base_buffer, sizeof(buffer));
            decode_eac_signed_block(data, 2, buffer);
            decode_eac_signed_block(data + 8, 1, buffer);
            t2d_copy_block_4x4(bx, by, w, h, buffer, image);
        }
    }
    return 1;
}
