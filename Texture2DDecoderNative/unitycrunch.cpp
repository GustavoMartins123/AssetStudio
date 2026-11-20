#include "unitycrunch.h"
#include <stdint.h>
#include <algorithm>
#include <new>

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable : 4018 4244)
#endif
#include "unitycrunch/crn_decomp.h"
#ifdef _MSC_VER
#pragma warning(pop)
#endif

bool unity_crunch_unpack_level(const uint8_t* data, uint32_t data_size, uint32_t level_index, void** ret, uint32_t* ret_size) {
	if (ret != nullptr)
	{
		*ret = nullptr;
	}
	if (ret_size != nullptr)
	{
		*ret_size = 0;
	}
	if (data == nullptr || data_size == 0 || ret == nullptr || ret_size == nullptr || level_index >= 32)
	{
		return false;
	}

	unitycrnd::crn_texture_info tex_info;
	if (!unitycrnd::crnd_get_texture_info(data, data_size, &tex_info))
	{
		return false;
	}

	if (level_index >= tex_info.m_levels)
	{
		return false;
	}

	unitycrnd::crnd_unpack_context pContext = unitycrnd::crnd_unpack_begin(data, data_size);
	if (!pContext)
	{
		return false;
	}

	const crn_uint32 width = std::max(1U, tex_info.m_width >> level_index);
	const crn_uint32 height = std::max(1U, tex_info.m_height >> level_index);
	const uint64_t blocks_x = std::max<uint64_t>(1U, (static_cast<uint64_t>(width) + 3ULL) >> 2);
	const uint64_t blocks_y = std::max<uint64_t>(1U, (static_cast<uint64_t>(height) + 3ULL) >> 2);
	const uint64_t bytes_per_block = unitycrnd::crnd_get_bytes_per_dxt_block(tex_info.m_format);
	if (bytes_per_block == 0)
	{
		unitycrnd::crnd_unpack_end(pContext);
		return false;
	}

	const uint64_t row_pitch64 = blocks_x * bytes_per_block;
	if (row_pitch64 == 0 || row_pitch64 > UINT32_MAX || blocks_y > static_cast<uint64_t>(INT32_MAX) / row_pitch64)
	{
		unitycrnd::crnd_unpack_end(pContext);
		return false;
	}

	const crn_uint32 row_pitch = static_cast<crn_uint32>(row_pitch64);
	const crn_uint32 total_face_size = static_cast<crn_uint32>(row_pitch64 * blocks_y);
	uint8_t* buffer = new (std::nothrow) uint8_t[total_face_size];
	if (buffer == nullptr)
	{
		unitycrnd::crnd_unpack_end(pContext);
		return false;
	}

	*ret = buffer;
	*ret_size = total_face_size;
	if (!unitycrnd::crnd_unpack_level(pContext, ret, total_face_size, row_pitch, level_index))
	{
		delete[] static_cast<uint8_t*>(*ret);
		*ret = nullptr;
		*ret_size = 0;
		unitycrnd::crnd_unpack_end(pContext);
		return false;
	}
	unitycrnd::crnd_unpack_end(pContext);
	return true;
}
