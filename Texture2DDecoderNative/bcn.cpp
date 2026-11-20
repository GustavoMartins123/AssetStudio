#include "bcn.h"
#include <stdint.h>
#include <assert.h>
#include <math.h>
#include <string.h>
#include <algorithm>
#include "color.h"
#include "fp16.h"
#include "simd_dispatch.h"

static inline void decode_bc1_colors(const uint8_t* data, uint32_t c[4], uint8_t alpha) noexcept {
	uint8_t r0, g0, b0, r1, g1, b1;
	const uint16_t q0 = t2d_load_u16_le(data);
	const uint16_t q1 = t2d_load_u16_le(data + 2);
	rgb565_le(q0, &r0, &g0, &b0);
	rgb565_le(q1, &r1, &g1, &b1);
	c[0] = static_cast<uint32_t>(color(r0, g0, b0, alpha));
	c[1] = static_cast<uint32_t>(color(r1, g1, b1, alpha));
	if (q0 > q1) {
		c[2] = static_cast<uint32_t>(color(((r0 * 2 + r1) * 21846) >> 16, ((g0 * 2 + g1) * 21846) >> 16, ((b0 * 2 + b1) * 21846) >> 16, alpha));
		c[3] = static_cast<uint32_t>(color(((r0 + r1 * 2) * 21846) >> 16, ((g0 + g1 * 2) * 21846) >> 16, ((b0 + b1 * 2) * 21846) >> 16, alpha));
	}
	else {
		c[2] = static_cast<uint32_t>(color((r0 + r1) >> 1, (g0 + g1) >> 1, (b0 + b1) >> 1, alpha));
		c[3] = static_cast<uint32_t>(color(0, 0, 0, alpha));
	}
}

static inline void decode_bc2_colors(const uint8_t* data, uint32_t c[4]) noexcept {
	uint8_t r0, g0, b0, r1, g1, b1;
	const uint16_t q0 = t2d_load_u16_le(data);
	const uint16_t q1 = t2d_load_u16_le(data + 2);
	rgb565_le(q0, &r0, &g0, &b0);
	rgb565_le(q1, &r1, &g1, &b1);
	c[0] = static_cast<uint32_t>(color(r0, g0, b0, 0));
	c[1] = static_cast<uint32_t>(color(r1, g1, b1, 0));
	c[2] = static_cast<uint32_t>(color(((r0 * 2 + r1) * 21846) >> 16, ((g0 * 2 + g1) * 21846) >> 16, ((b0 * 2 + b1) * 21846) >> 16, 0));
	c[3] = static_cast<uint32_t>(color(((r0 + r1 * 2) * 21846) >> 16, ((g0 + g1 * 2) * 21846) >> 16, ((b0 + b1 * 2) * 21846) >> 16, 0));
}

static inline void build_bc3_alpha_lut(const uint8_t* data, uint8_t a[16]) noexcept {
	const uint32_t a0 = data[0];
	const uint32_t a1 = data[1];
	a[0] = static_cast<uint8_t>(a0);
	a[1] = static_cast<uint8_t>(a1);
	if (a0 > a1) {
		a[2] = static_cast<uint8_t>(((a0 * 6 + a1) * 9363) >> 16);
		a[3] = static_cast<uint8_t>(((a0 * 5 + a1 * 2) * 9363) >> 16);
		a[4] = static_cast<uint8_t>(((a0 * 4 + a1 * 3) * 9363) >> 16);
		a[5] = static_cast<uint8_t>(((a0 * 3 + a1 * 4) * 9363) >> 16);
		a[6] = static_cast<uint8_t>(((a0 * 2 + a1 * 5) * 9363) >> 16);
		a[7] = static_cast<uint8_t>(((a0 + a1 * 6) * 9363) >> 16);
	}
	else {
		a[2] = static_cast<uint8_t>(((a0 * 4 + a1) * 13108) >> 16);
		a[3] = static_cast<uint8_t>(((a0 * 3 + a1 * 2) * 13108) >> 16);
		a[4] = static_cast<uint8_t>(((a0 * 2 + a1 * 3) * 13108) >> 16);
		a[5] = static_cast<uint8_t>(((a0 + a1 * 4) * 13108) >> 16);
		a[6] = 0;
		a[7] = 255;
	}
	memset(a + 8, 0, 8);
}

static inline void decode_bc1_block(const uint8_t* data, uint32_t* outbuf) noexcept {
	uint32_t c[4];
	decode_bc1_colors(data, c, 255);
	uint32_t d = t2d_load_u32_le(data + 4);
	for (int i = 0; i < 16; i++, d >>= 2)
		outbuf[i] = c[d & 3];
}

static inline void decode_bc2_block(const uint8_t* data, uint32_t* outbuf) noexcept {
	uint32_t c[4];
	decode_bc2_colors(data + 8, c);
	uint64_t alpha_data = t2d_load_u64_le(data);
	uint32_t d = t2d_load_u32_le(data + 12);
	for (int i = 0; i < 16; i++, d >>= 2, alpha_data >>= 4) {
		const uint32_t a4 = static_cast<uint32_t>(alpha_data & 0x0Fu);
		const uint32_t a8 = (a4 << 4) | a4;
		outbuf[i] = c[d & 3] | (a8 << 24);
	}
}

void decode_bc3_alpha(const uint8_t* data, uint32_t* outbuf, int channel) {
	uint8_t a[16];
	build_bc3_alpha_lut(data, a);
	uint8_t* dst = reinterpret_cast<uint8_t*>(outbuf);
	uint64_t d = t2d_load_u64_le(data) >> 16;
	for (int i = 0; i < 16; i++, d >>= 3)
		dst[i * 4 + channel] = a[d & 7];
}

static inline void decode_bc3_block(const uint8_t* data, uint32_t* outbuf) noexcept {
	decode_bc1_block(data + 8, outbuf);
	decode_bc3_alpha(data, outbuf, 3);
}

static inline void decode_bc4_block(const uint8_t* data, uint32_t* outbuf) noexcept {
	decode_bc3_alpha(data, outbuf, 2);
}

static inline void decode_bc5_block(const uint8_t* data, uint32_t* outbuf) noexcept {
	decode_bc3_alpha(data, outbuf, 2);
	decode_bc3_alpha(data + 8, outbuf, 1);
}

#if T2D_SIMD_X86

T2D_TARGET_SSE41
static void decode_bc1_sse41(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(16) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	const __m128i mult = _mm_setr_epi32(1 << 6, 1 << 4, 1 << 2, 1);
	const __m128i mask_c = _mm_set1_epi32(0x0C);
	const __m128i rep_byte = _mm_set1_epi32(0x01010101);
	const __m128i byte_offs = _mm_set1_epi32(0x03020100);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 8) {
			decode_bc1_colors(d, c, 255);
			const __m128i pal = _mm_load_si128(reinterpret_cast<const __m128i*>(c));
			for (int r = 0; r < 4; r++) {
				const __m128i vb = _mm_set1_epi32(d[4 + r]);
				const __m128i idx4 = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(vb, mult), 4), mask_c);
				const __m128i shuf = _mm_add_epi8(_mm_mullo_epi32(idx4, rep_byte), byte_offs);
				_mm_store_si128(reinterpret_cast<__m128i*>(buffer + r * 4), _mm_shuffle_epi8(pal, shuf));
			}
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_AVX2
static void decode_bc1_avx2(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(32) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	const __m256i shifts01 = _mm256_setr_epi32(0, 2, 4, 6, 8, 10, 12, 14);
	const __m256i shifts23 = _mm256_setr_epi32(16, 18, 20, 22, 24, 26, 28, 30);
	const __m256i mask3 = _mm256_set1_epi32(3);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 8) {
			decode_bc1_colors(d, c, 255);
			const __m256i pal256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(c)));
			const __m256i vd = _mm256_set1_epi32(static_cast<int>(t2d_load_u32_le(d + 4)));
			const __m256i out01 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts01), mask3));
			const __m256i out23 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts23), mask3));
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 0), out01);
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 8), out23);
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_SSE41
static void decode_bc2_sse41(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(16) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	const __m128i mult_c = _mm_setr_epi32(1 << 6, 1 << 4, 1 << 2, 1);
	const __m128i mask_c = _mm_set1_epi32(0x0C);
	const __m128i rep_byte = _mm_set1_epi32(0x01010101);
	const __m128i byte_offs = _mm_set1_epi32(0x03020100);
	const __m128i mult_a = _mm_setr_epi32(1 << 12, 1 << 8, 1 << 4, 1);
	const __m128i mask_a = _mm_set1_epi32(0x0F);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc2_colors(d + 8, c);
			const __m128i pal = _mm_load_si128(reinterpret_cast<const __m128i*>(c));
			for (int r = 0; r < 4; r++) {
				const __m128i vb = _mm_set1_epi32(d[12 + r]);
				const __m128i idx4 = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(vb, mult_c), 4), mask_c);
				const __m128i shuf = _mm_add_epi8(_mm_mullo_epi32(idx4, rep_byte), byte_offs);
				const __m128i col = _mm_shuffle_epi8(pal, shuf);

				const uint16_t a16 = t2d_load_u16_le(d + r * 2);
				const __m128i va = _mm_set1_epi32(a16);
				const __m128i nib = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(va, mult_a), 12), mask_a);
				const __m128i alpha = _mm_or_si128(_mm_slli_epi32(nib, 28), _mm_slli_epi32(nib, 24));
				_mm_store_si128(reinterpret_cast<__m128i*>(buffer + r * 4), _mm_or_si128(col, alpha));
			}
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_AVX2
static void decode_bc2_avx2(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(32) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	const __m256i shifts01 = _mm256_setr_epi32(0, 2, 4, 6, 8, 10, 12, 14);
	const __m256i shifts23 = _mm256_setr_epi32(16, 18, 20, 22, 24, 26, 28, 30);
	const __m256i mask3 = _mm256_set1_epi32(3);
	const __m256i ashifts = _mm256_setr_epi32(0, 4, 8, 12, 16, 20, 24, 28);
	const __m256i mask15 = _mm256_set1_epi32(0x0F);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc2_colors(d + 8, c);
			const __m256i pal256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(c)));
			const __m256i vd = _mm256_set1_epi32(static_cast<int>(t2d_load_u32_le(d + 12)));
			const __m256i col01 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts01), mask3));
			const __m256i col23 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts23), mask3));

			const __m256i nib01 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(t2d_load_u32_le(d))), ashifts), mask15);
			const __m256i nib23 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(t2d_load_u32_le(d + 4))), ashifts), mask15);
			const __m256i alpha01 = _mm256_or_si256(_mm256_slli_epi32(nib01, 28), _mm256_slli_epi32(nib01, 24));
			const __m256i alpha23 = _mm256_or_si256(_mm256_slli_epi32(nib23, 28), _mm256_slli_epi32(nib23, 24));

			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 0), _mm256_or_si256(col01, alpha01));
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 8), _mm256_or_si256(col23, alpha23));
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_SSE41
static void decode_bc3_sse41(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(16) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	alignas(16) uint8_t a[16];
	const __m128i mult_c = _mm_setr_epi32(1 << 6, 1 << 4, 1 << 2, 1);
	const __m128i mask_c = _mm_set1_epi32(0x0C);
	const __m128i rep_byte = _mm_set1_epi32(0x01010101);
	const __m128i byte_offs = _mm_set1_epi32(0x03020100);
	const __m128i mult_a = _mm_setr_epi32(1 << 9, 1 << 6, 1 << 3, 1);
	const __m128i mask7 = _mm_set1_epi32(7);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc1_colors(d + 8, c, 0);
			build_bc3_alpha_lut(d, a);
			const __m128i pal = _mm_load_si128(reinterpret_cast<const __m128i*>(c));
			const __m128i alut = _mm_load_si128(reinterpret_cast<const __m128i*>(a));
			const uint64_t idx48 = t2d_load_u64_le(d) >> 16;
			for (int r = 0; r < 4; r++) {
				const __m128i vb = _mm_set1_epi32(d[12 + r]);
				const __m128i idx4 = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(vb, mult_c), 4), mask_c);
				const __m128i shuf = _mm_add_epi8(_mm_mullo_epi32(idx4, rep_byte), byte_offs);
				const __m128i col = _mm_shuffle_epi8(pal, shuf);

				const int a12 = static_cast<int>((idx48 >> (r * 12)) & 0xFFFu);
				const __m128i aidx = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(_mm_set1_epi32(a12), mult_a), 9), mask7);
				const __m128i alpha = _mm_slli_epi32(_mm_shuffle_epi8(alut, aidx), 24);
				_mm_store_si128(reinterpret_cast<__m128i*>(buffer + r * 4), _mm_or_si128(col, alpha));
			}
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_AVX2
static void decode_bc3_avx2(const uint8_t* data, const long w, const long h, uint32_t* image) noexcept {
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	alignas(32) uint32_t buffer[16];
	alignas(16) uint32_t c[4];
	alignas(16) uint8_t a[16];
	const __m256i shifts01 = _mm256_setr_epi32(0, 2, 4, 6, 8, 10, 12, 14);
	const __m256i shifts23 = _mm256_setr_epi32(16, 18, 20, 22, 24, 26, 28, 30);
	const __m256i mask3 = _mm256_set1_epi32(3);
	const __m256i shifts3 = _mm256_setr_epi32(0, 3, 6, 9, 12, 15, 18, 21);
	const __m256i mask7 = _mm256_set1_epi32(7);
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc1_colors(d + 8, c, 0);
			build_bc3_alpha_lut(d, a);
			const __m256i pal256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(c)));
			const __m256i alut256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(a)));

			const __m256i vd = _mm256_set1_epi32(static_cast<int>(t2d_load_u32_le(d + 12)));
			const __m256i col01 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts01), mask3));
			const __m256i col23 = _mm256_permutevar8x32_epi32(pal256, _mm256_and_si256(_mm256_srlv_epi32(vd, shifts23), mask3));

			const uint64_t idx48 = t2d_load_u64_le(d) >> 16;
			const __m256i ai01 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(idx48 & 0xFFFFFFu)), shifts3), mask7);
			const __m256i ai23 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>((idx48 >> 24) & 0xFFFFFFu)), shifts3), mask7);
			const __m256i alpha01 = _mm256_slli_epi32(_mm256_shuffle_epi8(alut256, ai01), 24);
			const __m256i alpha23 = _mm256_slli_epi32(_mm256_shuffle_epi8(alut256, ai23), 24);

			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 0), _mm256_or_si256(col01, alpha01));
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 8), _mm256_or_si256(col23, alpha23));
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
}

T2D_TARGET_SSE41
static void decode_bc4_sse41(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) noexcept {
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	alignas(16) uint32_t buffer[16];
	alignas(16) uint8_t a[16];
	const __m128i mult_a = _mm_setr_epi32(1 << 9, 1 << 6, 1 << 3, 1);
	const __m128i mask7 = _mm_set1_epi32(7);
	const __m128i mask_ff = _mm_set1_epi32(0xFF);
	const __m128i alpha_mask_v = _mm_set1_epi32(static_cast<int>(0xFF000000u));
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 8) {
			build_bc3_alpha_lut(data, a);
			const __m128i alut = _mm_load_si128(reinterpret_cast<const __m128i*>(a));
			const uint64_t idx48 = t2d_load_u64_le(data) >> 16;
			for (int r = 0; r < 4; r++) {
				const int a12 = static_cast<int>((idx48 >> (r * 12)) & 0xFFFu);
				const __m128i aidx = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(_mm_set1_epi32(a12), mult_a), 9), mask7);
				const __m128i red = _mm_slli_epi32(_mm_and_si128(_mm_shuffle_epi8(alut, aidx), mask_ff), 16);
				_mm_store_si128(reinterpret_cast<__m128i*>(buffer + r * 4), _mm_or_si128(red, alpha_mask_v));
			}
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
}

T2D_TARGET_AVX2
static void decode_bc4_avx2(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) noexcept {
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	alignas(32) uint32_t buffer[16];
	alignas(16) uint8_t a[16];
	const __m256i shifts3 = _mm256_setr_epi32(0, 3, 6, 9, 12, 15, 18, 21);
	const __m256i mask7 = _mm256_set1_epi32(7);
	const __m256i mask_ff = _mm256_set1_epi32(0xFF);
	const __m256i alpha_mask_v = _mm256_set1_epi32(static_cast<int>(0xFF000000u));
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 8) {
			build_bc3_alpha_lut(data, a);
			const __m256i alut256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(a)));
			const uint64_t idx48 = t2d_load_u64_le(data) >> 16;
			const __m256i ai01 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(idx48 & 0xFFFFFFu)), shifts3), mask7);
			const __m256i ai23 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>((idx48 >> 24) & 0xFFFFFFu)), shifts3), mask7);
			const __m256i red01 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(alut256, ai01), mask_ff), 16);
			const __m256i red23 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(alut256, ai23), mask_ff), 16);
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 0), _mm256_or_si256(red01, alpha_mask_v));
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 8), _mm256_or_si256(red23, alpha_mask_v));
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
}

T2D_TARGET_SSE41
static void decode_bc5_sse41(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) noexcept {
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	alignas(16) uint32_t buffer[16];
	alignas(16) uint8_t ar[16];
	alignas(16) uint8_t ag[16];
	const __m128i mult_a = _mm_setr_epi32(1 << 9, 1 << 6, 1 << 3, 1);
	const __m128i mask7 = _mm_set1_epi32(7);
	const __m128i mask_ff = _mm_set1_epi32(0xFF);
	const __m128i alpha_mask_v = _mm_set1_epi32(static_cast<int>(0xFF000000u));
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 16) {
			build_bc3_alpha_lut(data, ar);
			build_bc3_alpha_lut(data + 8, ag);
			const __m128i rlut = _mm_load_si128(reinterpret_cast<const __m128i*>(ar));
			const __m128i glut = _mm_load_si128(reinterpret_cast<const __m128i*>(ag));
			const uint64_t idxR48 = t2d_load_u64_le(data) >> 16;
			const uint64_t idxG48 = t2d_load_u64_le(data + 8) >> 16;
			for (int r = 0; r < 4; r++) {
				const int r12 = static_cast<int>((idxR48 >> (r * 12)) & 0xFFFu);
				const int g12 = static_cast<int>((idxG48 >> (r * 12)) & 0xFFFu);
				const __m128i ridx = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(_mm_set1_epi32(r12), mult_a), 9), mask7);
				const __m128i gidx = _mm_and_si128(_mm_srli_epi32(_mm_mullo_epi32(_mm_set1_epi32(g12), mult_a), 9), mask7);
				const __m128i red = _mm_slli_epi32(_mm_and_si128(_mm_shuffle_epi8(rlut, ridx), mask_ff), 16);
				const __m128i grn = _mm_slli_epi32(_mm_and_si128(_mm_shuffle_epi8(glut, gidx), mask_ff), 8);
				_mm_store_si128(reinterpret_cast<__m128i*>(buffer + r * 4), _mm_or_si128(_mm_or_si128(red, grn), alpha_mask_v));
			}
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
}

T2D_TARGET_AVX2
static void decode_bc5_avx2(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) noexcept {
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	alignas(32) uint32_t buffer[16];
	alignas(16) uint8_t ar[16];
	alignas(16) uint8_t ag[16];
	const __m256i shifts3 = _mm256_setr_epi32(0, 3, 6, 9, 12, 15, 18, 21);
	const __m256i mask7 = _mm256_set1_epi32(7);
	const __m256i mask_ff = _mm256_set1_epi32(0xFF);
	const __m256i alpha_mask_v = _mm256_set1_epi32(static_cast<int>(0xFF000000u));
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 16) {
			build_bc3_alpha_lut(data, ar);
			build_bc3_alpha_lut(data + 8, ag);
			const __m256i rlut256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(ar)));
			const __m256i glut256 = _mm256_broadcastsi128_si256(_mm_load_si128(reinterpret_cast<const __m128i*>(ag)));
			const uint64_t idxR48 = t2d_load_u64_le(data) >> 16;
			const uint64_t idxG48 = t2d_load_u64_le(data + 8) >> 16;

			const __m256i ri01 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(idxR48 & 0xFFFFFFu)), shifts3), mask7);
			const __m256i ri23 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>((idxR48 >> 24) & 0xFFFFFFu)), shifts3), mask7);
			const __m256i gi01 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>(idxG48 & 0xFFFFFFu)), shifts3), mask7);
			const __m256i gi23 = _mm256_and_si256(_mm256_srlv_epi32(_mm256_set1_epi32(static_cast<int>((idxG48 >> 24) & 0xFFFFFFu)), shifts3), mask7);

			const __m256i red01 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(rlut256, ri01), mask_ff), 16);
			const __m256i red23 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(rlut256, ri23), mask_ff), 16);
			const __m256i grn01 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(glut256, gi01), mask_ff), 8);
			const __m256i grn23 = _mm256_slli_epi32(_mm256_and_si256(_mm256_shuffle_epi8(glut256, gi23), mask_ff), 8);

			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 0), _mm256_or_si256(_mm256_or_si256(red01, grn01), alpha_mask_v));
			_mm256_store_si256(reinterpret_cast<__m256i*>(buffer + 8), _mm256_or_si256(_mm256_or_si256(red23, grn23), alpha_mask_v));
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
}

#endif

int decode_bc1(const uint8_t* data, const long w, const long h, uint32_t* image) {
	if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
		return 0;
#if T2D_SIMD_X86
	const SimdLevel simd = t2d_get_simd_level();
	if (simd >= SimdLevel::AVX2) {
		decode_bc1_avx2(data, w, h, image);
		return 1;
	}
	if (simd >= SimdLevel::SSE41) {
		decode_bc1_sse41(data, w, h, image);
		return 1;
	}
#endif
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	uint32_t buffer[16];
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 8) {
			decode_bc1_block(d, buffer);
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
	return 1;
}

int decode_bc2(const uint8_t* data, const long w, const long h, uint32_t* image) {
	if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
		return 0;
#if T2D_SIMD_X86
	const SimdLevel simd = t2d_get_simd_level();
	if (simd >= SimdLevel::AVX2) {
		decode_bc2_avx2(data, w, h, image);
		return 1;
	}
	if (simd >= SimdLevel::SSE41) {
		decode_bc2_sse41(data, w, h, image);
		return 1;
	}
#endif
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	uint32_t buffer[16];
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc2_block(d, buffer);
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
	return 1;
}

int decode_bc3(const uint8_t* data, const long w, const long h, uint32_t* image) {
	if (data == nullptr || image == nullptr || w <= 0 || h <= 0)
		return 0;
#if T2D_SIMD_X86
	const SimdLevel simd = t2d_get_simd_level();
	if (simd >= SimdLevel::AVX2) {
		decode_bc3_avx2(data, w, h, image);
		return 1;
	}
	if (simd >= SimdLevel::SSE41) {
		decode_bc3_sse41(data, w, h, image);
		return 1;
	}
#endif
	const long num_blocks_x = (w + 3) / 4;
	const long num_blocks_y = (h + 3) / 4;
	uint32_t buffer[16];
	const uint8_t* d = data;
	for (long by = 0; by < num_blocks_y; by++) {
		for (long bx = 0; bx < num_blocks_x; bx++, d += 16) {
			decode_bc3_block(d, buffer);
			t2d_copy_block_4x4(bx, by, w, h, buffer, image);
		}
	}
	return 1;
}

int decode_bc4(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) {
	if (data == nullptr || image == nullptr || m_width == 0 || m_height == 0)
		return 0;
#if T2D_SIMD_X86
	const SimdLevel simd = t2d_get_simd_level();
	if (simd >= SimdLevel::AVX2) {
		decode_bc4_avx2(data, m_width, m_height, image);
		return 1;
	}
	if (simd >= SimdLevel::SSE41) {
		decode_bc4_sse41(data, m_width, m_height, image);
		return 1;
	}
#endif
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	uint32_t buffer[16];
	for (uint32_t i = 0; i < 16; i++)
		buffer[i] = 0xff000000;
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 8) {
			decode_bc4_block(data, buffer);
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
	return 1;
}

int decode_bc5(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) {
	if (data == nullptr || image == nullptr || m_width == 0 || m_height == 0)
		return 0;
#if T2D_SIMD_X86
	const SimdLevel simd = t2d_get_simd_level();
	if (simd >= SimdLevel::AVX2) {
		decode_bc5_avx2(data, m_width, m_height, image);
		return 1;
	}
	if (simd >= SimdLevel::SSE41) {
		decode_bc5_sse41(data, m_width, m_height, image);
		return 1;
	}
#endif
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	uint32_t buffer[16];
	for (uint32_t i = 0; i < 16; i++)
		buffer[i] = 0xff000000;
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 16) {
			decode_bc5_block(data, buffer);
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
	return 1;
}

struct BitReader
{
	BitReader(const uint8_t* _data, uint16_t _bitPos = 0) noexcept
		: m_lo(t2d_load_u64_le(_data))
		, m_hi(t2d_load_u64_le(_data + 8))
		, m_bitPos(_bitPos)
	{
	}

	uint16_t extract(uint16_t bitPos, uint8_t numBits) const noexcept
	{
		if (numBits == 0 || bitPos >= 128)
			return 0;
		const uint64_t mask = (1ULL << numBits) - 1ULL;
		if (bitPos >= 64)
			return uint16_t((m_hi >> (bitPos - 64)) & mask);
		if (bitPos + numBits <= 64)
			return uint16_t((m_lo >> bitPos) & mask);
		return uint16_t(((m_lo >> bitPos) | (m_hi << (64 - bitPos))) & mask);
	}

	uint16_t read(uint8_t _numBits) noexcept
	{
		const uint16_t res = extract(m_bitPos, _numBits);
		m_bitPos += _numBits;
		return res;
	}

	uint16_t peek(uint16_t _offset, uint8_t _numBits) const noexcept
	{
		return static_cast<uint8_t>(extract(m_bitPos + _offset, _numBits));
	}

	uint64_t m_lo;
	uint64_t m_hi;
	uint16_t m_bitPos;
};

static const uint16_t s_bptcP2[] =
{ //  3210     0000000000   1111111111   2222222222   3333333333
	0xcccc, // 0, 0, 1, 1,  0, 0, 1, 1,  0, 0, 1, 1,  0, 0, 1, 1
	0x8888, // 0, 0, 0, 1,  0, 0, 0, 1,  0, 0, 0, 1,  0, 0, 0, 1
	0xeeee, // 0, 1, 1, 1,  0, 1, 1, 1,  0, 1, 1, 1,  0, 1, 1, 1
	0xecc8, // 0, 0, 0, 1,  0, 0, 1, 1,  0, 0, 1, 1,  0, 1, 1, 1
	0xc880, // 0, 0, 0, 0,  0, 0, 0, 1,  0, 0, 0, 1,  0, 0, 1, 1
	0xfeec, // 0, 0, 1, 1,  0, 1, 1, 1,  0, 1, 1, 1,  1, 1, 1, 1
	0xfec8, // 0, 0, 0, 1,  0, 0, 1, 1,  0, 1, 1, 1,  1, 1, 1, 1
	0xec80, // 0, 0, 0, 0,  0, 0, 0, 1,  0, 0, 1, 1,  0, 1, 1, 1
	0xc800, // 0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 1,  0, 0, 1, 1
	0xffec, // 0, 0, 1, 1,  0, 1, 1, 1,  1, 1, 1, 1,  1, 1, 1, 1
	0xfe80, // 0, 0, 0, 0,  0, 0, 0, 1,  0, 1, 1, 1,  1, 1, 1, 1
	0xe800, // 0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 1,  0, 1, 1, 1
	0xffe8, // 0, 0, 0, 1,  0, 1, 1, 1,  1, 1, 1, 1,  1, 1, 1, 1
	0xff00, // 0, 0, 0, 0,  0, 0, 0, 0,  1, 1, 1, 1,  1, 1, 1, 1
	0xfff0, // 0, 0, 0, 0,  1, 1, 1, 1,  1, 1, 1, 1,  1, 1, 1, 1
	0xf000, // 0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0,  1, 1, 1, 1
	0xf710, // 0, 0, 0, 0,  1, 0, 0, 0,  1, 1, 1, 0,  1, 1, 1, 1
	0x008e, // 0, 1, 1, 1,  0, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0
	0x7100, // 0, 0, 0, 0,  0, 0, 0,  0, 1, 0, 0, 0,  1, 1, 1, 0
	0x08ce, // 0, 1, 1, 1,  0, 0, 1, 1,  0, 0, 0, 1,  0, 0, 0, 0
	0x008c, // 0, 0, 1, 1,  0, 0, 0, 1,  0, 0, 0, 0,  0, 0, 0, 0
	0x7310, // 0, 0, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0,  1, 1, 1, 0
	0x3100, // 0, 0, 0, 0,  0, 0, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0
	0x8cce, // 0, 1, 1, 1,  0, 0, 1, 1,  0, 0, 1, 1,  0, 0, 0, 1
	0x088c, // 0, 0, 1, 1,  0, 0, 0, 1,  0, 0, 0, 1,  0, 0, 0, 0
	0x3110, // 0, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0,  1, 1, 0, 0
	0x6666, // 0, 1, 1, 0,  0, 1, 1, 0,  0, 1, 1, 0,  0, 1, 1, 0
	0x366c, // 0, 0, 1, 1,  0, 1, 1, 0,  0, 1, 1, 0,  1, 1, 0, 0
	0x17e8, // 0, 0, 0, 1,  0, 1, 1, 1,  1, 1, 1, 0,  1, 0, 0, 0
	0x0ff0, // 0, 0, 0, 0,  1, 1, 1, 1,  1, 1, 1, 1,  0, 0, 0, 0
	0x718e, // 0, 1, 1, 1,  0, 0, 0, 1,  1, 0, 0, 0,  1, 1, 1, 0
	0x399c, // 0, 0, 1, 1,  1, 0, 0, 1,  1, 0, 0, 1,  1, 1, 0, 0
	0xaaaa, // 0, 1, 0, 1,  0, 1, 0, 1,  0, 1, 0, 1,  0, 1, 0, 1
	0xf0f0, // 0, 0, 0, 0,  1, 1, 1, 1,  0, 0, 0, 0,  1, 1, 1, 1
	0x5a5a, // 0, 1, 0, 1,  1, 0, 1, 0,  0, 1, 0, 1,  1, 0, 1, 0
	0x33cc, // 0, 0, 1, 1,  0, 0, 1, 1,  1, 1, 0, 0,  1, 1, 0, 0
	0x3c3c, // 0, 0, 1, 1,  1, 1, 0, 0,  0, 0, 1, 1,  1, 1, 0, 0
	0x55aa, // 0, 1, 0, 1,  0, 1, 0, 1,  1, 0, 1, 0,  1, 0, 1, 0
	0x9696, // 0, 1, 1, 0,  1, 0, 0, 1,  0, 1, 1, 0,  1, 0, 0, 1
	0xa55a, // 0, 1, 0, 1,  1, 0, 1, 0,  1, 0, 1, 0,  0, 1, 0, 1
	0x73ce, // 0, 1, 1, 1,  0, 0, 1, 1,  1, 1, 0, 0,  1, 1, 1, 0
	0x13c8, // 0, 0, 0, 1,  0, 0, 1, 1,  1, 1, 0, 0,  1, 0, 0, 0
	0x324c, // 0, 0, 1, 1,  0, 0, 1, 0,  0, 1, 0, 0,  1, 1, 0, 0
	0x3bdc, // 0, 0, 1, 1,  1, 0, 1, 1,  1, 1, 0, 1,  1, 1, 0, 0
	0x6996, // 0, 1, 1, 0,  1, 0, 0, 1,  1, 0, 0, 1,  0, 1, 1, 0
	0xc33c, // 0, 0, 1, 1,  1, 1, 0, 0,  1, 1, 0, 0,  0, 0, 1, 1
	0x9966, // 0, 1, 1, 0,  0, 1, 1, 0,  1, 0, 0, 1,  1, 0, 0, 1
	0x0660, // 0, 0, 0, 0,  0, 1, 1, 0,  0, 1, 1, 0,  0, 0, 0, 0
	0x0272, // 0, 1, 0, 0,  1, 1, 1, 0,  0, 1, 0, 0,  0, 0, 0, 0
	0x04e4, // 0, 0, 1, 0,  0, 1, 1, 1,  0, 0, 1, 0,  0, 0, 0, 0
	0x4e40, // 0, 0, 0, 0,  0, 0, 1, 0,  0, 1, 1, 1,  0, 0, 1, 0
	0x2720, // 0, 0, 0, 0,  0, 1, 0, 0,  1, 1, 1, 0,  0, 1, 0, 0
	0xc936, // 0, 1, 1, 0,  1, 1, 0, 0,  1, 0, 0, 1,  0, 0, 1, 1
	0x936c, // 0, 0, 1, 1,  0, 1, 1, 0,  1, 1, 0, 0,  1, 0, 0, 1
	0x39c6, // 0, 1, 1, 0,  0, 0, 1, 1,  1, 0, 0, 1,  1, 1, 0, 0
	0x639c, // 0, 0, 1, 1,  1, 0, 0, 1,  1, 1, 0, 0,  0, 1, 1, 0
	0x9336, // 0, 1, 1, 0,  1, 1, 0, 0,  1, 1, 0, 0,  1, 0, 0, 1
	0x9cc6, // 0, 1, 1, 0,  0, 0, 1, 1,  0, 0, 1, 1,  1, 0, 0, 1
	0x817e, // 0, 1, 1, 1,  1, 1, 1, 0,  1, 0, 0, 0,  0, 0, 0, 1
	0xe718, // 0, 0, 0, 1,  1, 0, 0, 0,  1, 1, 1, 0,  0, 1, 1, 1
	0xccf0, // 0, 0, 0, 0,  1, 1, 1, 1,  0, 0, 1, 1,  0, 0, 1, 1
	0x0fcc, // 0, 0, 1, 1,  0, 0, 1, 1,  1, 1, 1, 1,  0, 0, 0, 0
	0x7744, // 0, 0, 1, 0,  0, 0, 1, 0,  1, 1, 1, 0,  1, 1, 1, 0
	0xee22, // 0, 1, 0, 0,  0, 1, 0, 0,  0, 1, 1, 1,  0, 1, 1, 1
};

static const uint8_t s_bptcA2[] =
{
	15, 15, 15, 15, 15, 15, 15, 15,
	15, 15, 15, 15, 15, 15, 15, 15,
	15,  2,  8,  2,  2,  8,  8, 15,
	 2,  8,  2,  2,  8,  8,  2,  2,
	15, 15,  6,  8,  2,  8, 15, 15,
	 2,  8,  2,  2,  2, 15, 15,  6,
	 6,  2,  6,  8, 15, 15,  2,  2,
	15, 15, 15, 15, 15,  2,  2, 15,
};

static const uint8_t s_bptcFactors[3][16] =
{
	{  0, 21, 43, 64,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0 },
	{  0,  9, 18, 27, 37, 46, 55, 64,  0,  0,  0,  0,  0,  0,  0,  0 },
	{  0,  4,  9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 },
};

struct Bc6hModeInfo
{
	uint8_t transformed;
	uint8_t partitionBits;
	uint8_t endpointBits;
	uint8_t deltaBits[3];
};

static const Bc6hModeInfo s_bc6hModeInfo[] =
{
	{ 1, 5, 10, {  5,  5,  5 } },
	{ 1, 5,  7, {  6,  6,  6 } },
	{ 1, 5, 11, {  5,  4,  4 } },
	{ 0, 0, 10, { 10, 10, 10 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5, 11, {  4,  5,  4 } },
	{ 1, 0, 11, {  9,  9,  9 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5, 11, {  4,  4,  5 } },
	{ 1, 0, 12, {  8,  8,  8 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5,  9, {  5,  5,  5 } },
	{ 1, 0, 16, {  4,  4,  4 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5,  8, {  6,  5,  5 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5,  8, {  5,  6,  5 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 1, 5,  8, {  5,  5,  6 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 0,  0, {  0,  0,  0 } },
	{ 0, 5,  6, {  6,  6,  6 } },
	{ 0, 0,  0, {  0,  0,  0 } },
};

static uint16_t unquantize(uint16_t _value, bool _signed, uint8_t _endpointBits) noexcept
{
	const uint16_t maxValue = 1 << (_endpointBits - 1);

	if (_signed)
	{
		if (_endpointBits >= 16)
		{
			return _value;
		}

		const bool sign = !!(_value & 0x8000);
		_value &= 0x7fff;

		uint16_t unq;

		if (0 == _value)
		{
			unq = 0;
		}
		else if (_value >= maxValue - 1)
		{
			unq = 0x7fff;
		}
		else
		{
			unq = ((_value << 15) + 0x4000) >> (_endpointBits - 1);
		}

		return sign ? -unq : unq;
	}

	if (_endpointBits >= 15)
	{
		return _value;
	}

	if (0 == _value)
	{
		return 0;
	}

	if (_value == maxValue)
	{
		return UINT16_MAX;
	}

	return ((_value << 15) + 0x4000) >> (_endpointBits - 1);
}

static uint16_t finish_unquantize(uint16_t _value, bool _signed) noexcept
{
	if (_signed)
	{
		const uint16_t sign = _value & 0x8000;
		_value &= 0x7fff;

		return ((_value * 31) >> 5) | sign;
	}

	return (_value * 31) >> 6;
}

static uint16_t sign_extend(uint16_t _value, uint8_t _numBits) noexcept
{
	const uint16_t mask = 1 << (_numBits - 1);
	const uint16_t result = (_value ^ mask) - mask;

	return result;
}

static inline uint8_t f32_to_u8(const float f) noexcept {
	float c = roundf(f * 255);
	if (c < 0)
		return 0;
	else if (c > 255)
		return 255;
	else
		return static_cast<uint8_t>(c);
}

struct HalfToU8Lut {
	uint8_t table[0x2800];
	HalfToU8Lut() noexcept {
		for (uint16_t h = 0x1400; h < 0x3C00; ++h) {
			table[h - 0x1400] = f32_to_u8(fp16_ieee_to_fp32_value(h));
		}
	}
};

static inline uint8_t half_to_u8(uint16_t h) noexcept {
	static const HalfToU8Lut s_lut;
	if (h < 0x1400)
		return 0;
	if (h < 0x3C00)
		return s_lut.table[h - 0x1400];
	if (h <= 0x7C00)
		return 255;
	if (h >= 0x8000 && h <= 0xFC00)
		return 0;
	return f32_to_u8(fp16_ieee_to_fp32_value(h));
}

static void decode_bc6_block(const uint8_t* _src, uint32_t* _dst, bool _signed) noexcept
{
	BitReader bit(_src);

	uint8_t mode = uint8_t(bit.read(2));

	uint16_t epR[4] = {};
	uint16_t epG[4] = {};
	uint16_t epB[4] = {};

	if (mode & 2)
	{
		mode |= bit.read(3) << 2;

		if (0 == s_bc6hModeInfo[mode].endpointBits)
		{
			memset(_dst, 0, 16 * 4);
			return;
		}

		switch (mode)
		{
		case 2:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(5) << 0;
			epR[0] |= bit.read(1) << 10;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(4) << 0;
			epG[0] |= bit.read(1) << 10;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(4) << 0;
			epB[0] |= bit.read(1) << 10;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 3;
			break;

		case 3:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(10) << 0;
			epG[1] |= bit.read(10) << 0;
			epB[1] |= bit.read(10) << 0;
			break;

		case 6:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(4) << 0;
			epR[0] |= bit.read(1) << 10;
			epG[3] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(5) << 0;
			epG[0] |= bit.read(1) << 10;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(4) << 0;
			epB[0] |= bit.read(1) << 10;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(4) << 0;
			epB[3] |= bit.read(1) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(4) << 0;
			epG[2] |= bit.read(1) << 4;
			epB[3] |= bit.read(1) << 3;
			break;

		case 7:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(9) << 0;
			epR[0] |= bit.read(1) << 10;
			epG[1] |= bit.read(9) << 0;
			epG[0] |= bit.read(1) << 10;
			epB[1] |= bit.read(9) << 0;
			epB[0] |= bit.read(1) << 10;
			break;

		case 10:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(4) << 0;
			epR[0] |= bit.read(1) << 10;
			epB[2] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(4) << 0;
			epG[0] |= bit.read(1) << 10;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(5) << 0;
			epB[0] |= bit.read(1) << 10;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(4) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(4) << 0;
			epB[3] |= bit.read(1) << 4;
			epB[3] |= bit.read(1) << 3;
			break;

		case 11:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(8) << 0;
			epR[0] |= bit.read(1) << 11;
			epR[0] |= bit.read(1) << 10;
			epG[1] |= bit.read(8) << 0;
			epG[0] |= bit.read(1) << 11;
			epG[0] |= bit.read(1) << 10;
			epB[1] |= bit.read(8) << 0;
			epB[0] |= bit.read(1) << 11;
			epB[0] |= bit.read(1) << 10;
			break;

		case 14:
			epR[0] |= bit.read(9) << 0;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(9) << 0;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(9) << 0;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(5) << 0;
			epG[3] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 3;
			break;

		case 15:
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(4) << 0;
			epR[0] |= bit.read(1) << 15;
			epR[0] |= bit.read(1) << 14;
			epR[0] |= bit.read(1) << 13;
			epR[0] |= bit.read(1) << 12;
			epR[0] |= bit.read(1) << 11;
			epR[0] |= bit.read(1) << 10;
			epG[1] |= bit.read(4) << 0;
			epG[0] |= bit.read(1) << 15;
			epG[0] |= bit.read(1) << 14;
			epG[0] |= bit.read(1) << 13;
			epG[0] |= bit.read(1) << 12;
			epG[0] |= bit.read(1) << 11;
			epG[0] |= bit.read(1) << 10;
			epB[1] |= bit.read(4) << 0;
			epB[0] |= bit.read(1) << 15;
			epB[0] |= bit.read(1) << 14;
			epB[0] |= bit.read(1) << 13;
			epB[0] |= bit.read(1) << 12;
			epB[0] |= bit.read(1) << 11;
			epB[0] |= bit.read(1) << 10;
			break;

		case 18:
			epR[0] |= bit.read(8) << 0;
			epG[3] |= bit.read(1) << 4;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(8) << 0;
			epB[3] |= bit.read(1) << 2;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(8) << 0;
			epB[3] |= bit.read(1) << 3;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(6) << 0;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(6) << 0;
			epR[3] |= bit.read(6) << 0;
			break;

		case 22:
			epR[0] |= bit.read(8) << 0;
			epB[3] |= bit.read(1) << 0;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(8) << 0;
			epG[2] |= bit.read(1) << 5;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(8) << 0;
			epG[3] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(5) << 0;
			epG[3] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(6) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 3;
			break;

		case 26:
			epR[0] |= bit.read(8) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(8) << 0;
			epB[2] |= bit.read(1) << 5;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(8) << 0;
			epB[3] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(5) << 0;
			epG[3] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(6) << 0;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 3;
			break;

		case 30:
			epR[0] |= bit.read(6) << 0;
			epG[3] |= bit.read(1) << 4;
			epB[3] |= bit.read(1) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(6) << 0;
			epG[2] |= bit.read(1) << 5;
			epB[2] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 2;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(6) << 0;
			epG[3] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 3;
			epB[3] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(6) << 0;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(6) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(6) << 0;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(6) << 0;
			epR[3] |= bit.read(6) << 0;
			break;

		default:
			break;
		}
	}
	else
	{
		switch (mode)
		{
		case 0:
			epG[2] |= bit.read(1) << 4;
			epB[2] |= bit.read(1) << 4;
			epB[3] |= bit.read(1) << 4;
			epR[0] |= bit.read(10) << 0;
			epG[0] |= bit.read(10) << 0;
			epB[0] |= bit.read(10) << 0;
			epR[1] |= bit.read(5) << 0;
			epG[3] |= bit.read(1) << 4;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 2;
			epR[3] |= bit.read(5) << 0;
			epB[3] |= bit.read(1) << 3;
			break;

		case 1:
			epG[2] |= bit.read(1) << 5;
			epG[3] |= bit.read(1) << 4;
			epG[3] |= bit.read(1) << 5;
			epR[0] |= bit.read(7) << 0;
			epB[3] |= bit.read(1) << 0;
			epB[3] |= bit.read(1) << 1;
			epB[2] |= bit.read(1) << 4;
			epG[0] |= bit.read(7) << 0;
			epB[2] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 2;
			epG[2] |= bit.read(1) << 4;
			epB[0] |= bit.read(7) << 0;
			epB[3] |= bit.read(1) << 3;
			epB[3] |= bit.read(1) << 5;
			epB[3] |= bit.read(1) << 4;
			epR[1] |= bit.read(6) << 0;
			epG[2] |= bit.read(4) << 0;
			epG[1] |= bit.read(6) << 0;
			epG[3] |= bit.read(4) << 0;
			epB[1] |= bit.read(6) << 0;
			epB[2] |= bit.read(4) << 0;
			epR[2] |= bit.read(6) << 0;
			epR[3] |= bit.read(6) << 0;
			break;

		default:
			break;
		}
	}

	const Bc6hModeInfo mi = s_bc6hModeInfo[mode];

	if (_signed)
	{
		epR[0] = sign_extend(epR[0], mi.endpointBits);
		epG[0] = sign_extend(epG[0], mi.endpointBits);
		epB[0] = sign_extend(epB[0], mi.endpointBits);
	}

	const uint8_t numSubsets = !!mi.partitionBits + 1;

	for (uint8_t ii = 1, num = numSubsets * 2; ii < num; ++ii)
	{
		if (_signed || mi.transformed)
		{
			epR[ii] = sign_extend(epR[ii], mi.deltaBits[0]);
			epG[ii] = sign_extend(epG[ii], mi.deltaBits[1]);
			epB[ii] = sign_extend(epB[ii], mi.deltaBits[2]);
		}

		if (mi.transformed)
		{
			const uint16_t mask = (1 << mi.endpointBits) - 1;

			epR[ii] = (epR[ii] + epR[0]) & mask;
			epG[ii] = (epG[ii] + epG[0]) & mask;
			epB[ii] = (epB[ii] + epB[0]) & mask;

			if (_signed)
			{
				epR[ii] = sign_extend(epR[ii], mi.endpointBits);
				epG[ii] = sign_extend(epG[ii], mi.endpointBits);
				epB[ii] = sign_extend(epB[ii], mi.endpointBits);
			}
		}
	}

	for (uint8_t ii = 0, num = numSubsets * 2; ii < num; ++ii)
	{
		epR[ii] = unquantize(epR[ii], _signed, mi.endpointBits);
		epG[ii] = unquantize(epG[ii], _signed, mi.endpointBits);
		epB[ii] = unquantize(epB[ii], _signed, mi.endpointBits);
	}

	const uint8_t partitionSetIdx = uint8_t(mi.partitionBits ? bit.read(5) : 0);
	const uint8_t indexBits = mi.partitionBits ? 3 : 4;
	const uint8_t* factors = s_bptcFactors[indexBits - 2];

	if (0 == mi.partitionBits && !_signed)
	{
		const uint32_t r0 = epR[0], r1 = epR[1];
		const uint32_t g0 = epG[0], g1 = epG[1];
		const uint32_t b0 = epB[0], b1 = epB[1];
		for (uint8_t idx = 0; idx < 16; ++idx)
		{
			const uint8_t num = indexBits - (idx == 0);
			const uint8_t index = static_cast<uint8_t>(bit.read(num));
			const uint32_t fcb = factors[index];
			const uint32_t fca = 64 - fcb;
			const uint16_t rr = static_cast<uint16_t>((((r0 * fca + r1 * fcb + 32) >> 6) * 31) >> 6);
			const uint16_t gg = static_cast<uint16_t>((((g0 * fca + g1 * fcb + 32) >> 6) * 31) >> 6);
			const uint16_t bb = static_cast<uint16_t>((((b0 * fca + b1 * fcb + 32) >> 6) * 31) >> 6);
			_dst[idx] = static_cast<uint32_t>(color(half_to_u8(rr), half_to_u8(gg), half_to_u8(bb), 255));
		}
		return;
	}

	const uint16_t p2Mask = s_bptcP2[partitionSetIdx];
	const uint8_t a2Idx = s_bptcA2[partitionSetIdx];

	for (uint8_t idx = 0; idx < 16; ++idx)
	{
		uint8_t subsetIndex = 0;
		uint8_t indexAnchor = 0;

		if (0 != mi.partitionBits)
		{
			subsetIndex = (p2Mask >> idx) & 1;
			indexAnchor = subsetIndex ? a2Idx : 0;
		}

		const uint8_t anchor = idx == indexAnchor;
		const uint8_t num = indexBits - anchor;
		const uint8_t index = static_cast<uint8_t>(bit.read(num));

		const uint8_t fc = factors[index];
		const uint8_t fca = 64 - fc;
		const uint8_t fcb = fc;

		subsetIndex *= 2;
		uint16_t rr = finish_unquantize((epR[subsetIndex] * fca + epR[subsetIndex + 1] * fcb + 32) >> 6, _signed);
		uint16_t gg = finish_unquantize((epG[subsetIndex] * fca + epG[subsetIndex + 1] * fcb + 32) >> 6, _signed);
		uint16_t bb = finish_unquantize((epB[subsetIndex] * fca + epB[subsetIndex + 1] * fcb + 32) >> 6, _signed);

		_dst[idx] = static_cast<uint32_t>(color(half_to_u8(rr), half_to_u8(gg), half_to_u8(bb), 255));
	}
}

int decode_bc6(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) {
	if (data == nullptr || image == nullptr || m_width == 0 || m_height == 0)
		return 0;
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	uint32_t buffer[16];
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 16) {
			decode_bc6_block(data, buffer, false);
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
	return 1;
}

static const uint32_t s_bptcP3[] =
{
	0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050, 0x5555a0a0, 0x5a5a5050,
	0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090, 0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250,
	0xa5945040, 0x0a425054, 0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
	0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414, 0x50a4a450, 0x6a5a0200,
	0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424, 0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50,
	0x500aa550, 0xaaaa4444, 0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
	0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580, 0xaa141414, 0x96960000,
	0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000, 0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254,
};

static const uint8_t s_bptcA3[2][64] =
{
	{
		 3,  3, 15, 15,  8,  3, 15, 15,
		 8,  8,  6,  6,  6,  5,  3,  3,
		 3,  3,  8, 15,  3,  3,  6, 10,
		 5,  8,  8,  6,  8,  5, 15, 15,
		 8, 15,  3,  5,  6, 10,  8, 15,
		15,  3, 15,  5, 15, 15, 15, 15,
		 3, 15,  5,  5,  5,  8,  5, 10,
		 5, 10,  8, 13, 15, 12,  3,  3,
	},
	{
		15,  8,  8,  3, 15, 15,  3,  8,
		15, 15, 15, 15, 15, 15, 15,  8,
		15,  8, 15,  3, 15,  8, 15,  8,
		 3, 15,  6, 10, 15, 15, 10,  8,
		15,  3, 15, 10, 10,  8,  9, 10,
		 6, 15,  8, 15,  3,  6,  6,  8,
		15,  3, 15, 15, 15, 15, 15, 15,
		15, 15, 15, 15,  3, 15, 15,  8,
	},
};

struct Bc7ModeInfo
{
	uint8_t numSubsets;
	uint8_t partitionBits;
	uint8_t rotationBits;
	uint8_t indexSelectionBits;
	uint8_t colorBits;
	uint8_t alphaBits;
	uint8_t endpointPBits;
	uint8_t sharedPBits;
	uint8_t indexBits[2];
};

static const Bc7ModeInfo s_bp7ModeInfo[] =
{
	{ 3, 4, 0, 0, 4, 0, 1, 0, { 3, 0 } }, // 0
	{ 2, 6, 0, 0, 6, 0, 0, 1, { 3, 0 } }, // 1
	{ 3, 6, 0, 0, 5, 0, 0, 0, { 2, 0 } }, // 2
	{ 2, 6, 0, 0, 7, 0, 1, 0, { 2, 0 } }, // 3
	{ 1, 0, 2, 1, 5, 6, 0, 0, { 2, 3 } }, // 4
	{ 1, 0, 2, 0, 7, 8, 0, 0, { 2, 2 } }, // 5
	{ 1, 0, 0, 0, 7, 7, 1, 0, { 4, 0 } }, // 6
	{ 2, 6, 0, 0, 5, 5, 1, 0, { 2, 0 } }, // 7
};

static inline uint8_t expand_quantized(uint8_t v, int bits) noexcept {
	v = static_cast<uint8_t>(v << (8 - bits));
	return v | static_cast<uint8_t>(v >> bits);
}

static void decode_bc7_block(const uint8_t* _src, uint32_t* _dst) noexcept
{
	BitReader bit(_src);

	uint8_t mode = 0;
	for (; mode < 8 && 0 == bit.read(1); ++mode)
	{
	}

	if (mode == 8)
	{
		memset(_dst, 0, 16 * 4);
		return;
	}

	const Bc7ModeInfo& mi = s_bp7ModeInfo[mode];
	const uint8_t modePBits = 0 != mi.endpointPBits
		? mi.endpointPBits
		: mi.sharedPBits
		;

	const uint8_t partitionSetIdx = uint8_t(bit.read(mi.partitionBits));
	const uint8_t rotationMode = uint8_t(bit.read(mi.rotationBits));
	const uint8_t indexSelectionMode = uint8_t(bit.read(mi.indexSelectionBits));

	uint8_t epR[6];
	uint8_t epG[6];
	uint8_t epB[6];
	uint8_t epA[6];

	for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
	{
		epR[ii * 2 + 0] = uint8_t(bit.read(mi.colorBits) << modePBits);
		epR[ii * 2 + 1] = uint8_t(bit.read(mi.colorBits) << modePBits);
	}

	for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
	{
		epG[ii * 2 + 0] = uint8_t(bit.read(mi.colorBits) << modePBits);
		epG[ii * 2 + 1] = uint8_t(bit.read(mi.colorBits) << modePBits);
	}

	for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
	{
		epB[ii * 2 + 0] = uint8_t(bit.read(mi.colorBits) << modePBits);
		epB[ii * 2 + 1] = uint8_t(bit.read(mi.colorBits) << modePBits);
	}

	if (mi.alphaBits)
	{
		for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
		{
			epA[ii * 2 + 0] = uint8_t(bit.read(mi.alphaBits) << modePBits);
			epA[ii * 2 + 1] = uint8_t(bit.read(mi.alphaBits) << modePBits);
		}
	}
	else
	{
		memset(epA, 0xff, 6);
	}

	if (0 != modePBits)
	{
		for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
		{
			const uint8_t pda = uint8_t(bit.read(modePBits));
			const uint8_t pdb = uint8_t(0 == mi.sharedPBits ? bit.read(modePBits) : pda);

			epR[ii * 2 + 0] |= pda;
			epR[ii * 2 + 1] |= pdb;
			epG[ii * 2 + 0] |= pda;
			epG[ii * 2 + 1] |= pdb;
			epB[ii * 2 + 0] |= pda;
			epB[ii * 2 + 1] |= pdb;
			epA[ii * 2 + 0] |= pda;
			epA[ii * 2 + 1] |= pdb;
		}
	}

	const uint8_t colorBits = mi.colorBits + modePBits;

	for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
	{
		epR[ii * 2 + 0] = expand_quantized(epR[ii * 2 + 0], colorBits);
		epR[ii * 2 + 1] = expand_quantized(epR[ii * 2 + 1], colorBits);
		epG[ii * 2 + 0] = expand_quantized(epG[ii * 2 + 0], colorBits);
		epG[ii * 2 + 1] = expand_quantized(epG[ii * 2 + 1], colorBits);
		epB[ii * 2 + 0] = expand_quantized(epB[ii * 2 + 0], colorBits);
		epB[ii * 2 + 1] = expand_quantized(epB[ii * 2 + 1], colorBits);
	}

	if (mi.alphaBits)
	{
		const uint8_t alphaBits = mi.alphaBits + modePBits;

		for (uint8_t ii = 0; ii < mi.numSubsets; ++ii)
		{
			epA[ii * 2 + 0] = expand_quantized(epA[ii * 2 + 0], alphaBits);
			epA[ii * 2 + 1] = expand_quantized(epA[ii * 2 + 1], alphaBits);
		}
	}

	const bool hasIndexBits1 = 0 != mi.indexBits[1];

	const uint8_t* factors[2] =
	{
		s_bptcFactors[mi.indexBits[0] - 2],
		hasIndexBits1 ? s_bptcFactors[mi.indexBits[1] - 2] : s_bptcFactors[mi.indexBits[0] - 2],
	};

	uint16_t offset[2] =
	{
		0,
		uint16_t(mi.numSubsets * (16 * mi.indexBits[0] - 1)),
	};

	// Fast path for Mode 6 (1 subset, no rotation, no secondary index bits, 4-bit indices)
	if (mi.numSubsets == 1 && !hasIndexBits1 && rotationMode == 0)
	{
		const uint16_t r0 = epR[0], r1 = epR[1];
		const uint16_t g0 = epG[0], g1 = epG[1];
		const uint16_t b0 = epB[0], b1 = epB[1];
		const uint16_t a0 = epA[0], a1 = epA[1];
		const uint8_t ib0 = mi.indexBits[0];
		for (uint8_t idx = 0; idx < 16; ++idx)
		{
			const uint8_t num0 = ib0 - (idx == 0);
			const uint8_t index0 = static_cast<uint8_t>(bit.peek(offset[0], num0));
			offset[0] += num0;
			const uint16_t fcb = factors[0][index0];
			const uint16_t fca = 64 - fcb;
			const uint8_t rr = uint8_t((r0 * fca + r1 * fcb + 32) >> 6);
			const uint8_t gg = uint8_t((g0 * fca + g1 * fcb + 32) >> 6);
			const uint8_t bb = uint8_t((b0 * fca + b1 * fcb + 32) >> 6);
			const uint8_t aa = uint8_t((a0 * fca + a1 * fcb + 32) >> 6);
			_dst[idx] = static_cast<uint32_t>(color(rr, gg, bb, aa));
		}
		return;
	}

	const uint16_t p2Mask = s_bptcP2[partitionSetIdx];
	const uint8_t a2Idx = s_bptcA2[partitionSetIdx];
	const uint32_t p3Mask = s_bptcP3[partitionSetIdx];

	for (uint8_t idx = 0; idx < 16; ++idx)
	{
		uint8_t subsetIndex = 0;
		uint8_t indexAnchor = 0;
		switch (mi.numSubsets)
		{
		case 2:
			subsetIndex = (p2Mask >> idx) & 1;
			indexAnchor = 0 != subsetIndex ? a2Idx : 0;
			break;

		case 3:
			subsetIndex = (p3Mask >> (2 * idx)) & 3;
			indexAnchor = 0 != subsetIndex ? s_bptcA3[subsetIndex - 1][partitionSetIdx] : 0;
			break;

		default:
			break;
		}

		const uint8_t anchor = idx == indexAnchor;
		const uint8_t num0 = uint8_t(mi.indexBits[0] - anchor);
		const uint8_t num1 = uint8_t(hasIndexBits1 ? mi.indexBits[1] - anchor : 0);

		const uint8_t idx0 = static_cast<uint8_t>(bit.peek(offset[0], num0));
		const uint8_t idx1 = hasIndexBits1 ? static_cast<uint8_t>(bit.peek(offset[1], num1)) : idx0;
		const uint8_t index[2] = { idx0, idx1 };

		offset[0] += num0;
		offset[1] += num1;

		const uint8_t fc = factors[indexSelectionMode][index[indexSelectionMode]];
		const uint8_t fa = factors[!indexSelectionMode][index[!indexSelectionMode]];

		const uint8_t fca = 64 - fc;
		const uint8_t fcb = fc;
		const uint8_t faa = 64 - fa;
		const uint8_t fab = fa;

		subsetIndex *= 2;
		uint8_t rr = uint8_t(uint16_t(epR[subsetIndex] * fca + epR[subsetIndex + 1] * fcb + 32) >> 6);
		uint8_t gg = uint8_t(uint16_t(epG[subsetIndex] * fca + epG[subsetIndex + 1] * fcb + 32) >> 6);
		uint8_t bb = uint8_t(uint16_t(epB[subsetIndex] * fca + epB[subsetIndex + 1] * fcb + 32) >> 6);
		uint8_t aa = uint8_t(uint16_t(epA[subsetIndex] * faa + epA[subsetIndex + 1] * fab + 32) >> 6);

		switch (rotationMode)
		{
		case 1: std::swap(aa, rr); break;
		case 2: std::swap(aa, gg); break;
		case 3: std::swap(aa, bb); break;
		default:                  break;
		}

		_dst[idx] = static_cast<uint32_t>(color(rr, gg, bb, aa));
	}
}

int decode_bc7(const uint8_t* data, uint32_t m_width, uint32_t m_height, uint32_t* image) {
	if (data == nullptr || image == nullptr || m_width == 0 || m_height == 0)
		return 0;
	const uint32_t m_blocks_x = (m_width + 3) / 4;
	const uint32_t m_blocks_y = (m_height + 3) / 4;
	uint32_t buffer[16];
	for (uint32_t by = 0; by < m_blocks_y; by++) {
		for (uint32_t bx = 0; bx < m_blocks_x; bx++, data += 16) {
			decode_bc7_block(data, buffer);
			t2d_copy_block_4x4(static_cast<long>(bx), static_cast<long>(by), static_cast<long>(m_width), static_cast<long>(m_height), buffer, image);
		}
	}
	return 1;
}
