"""Read-only BFRES (.bfres / .bfres.mc) archive support for TKVSC.

Lists internal resources similar to Switch Toolbox's BFRES tree:
Models, animation groups, embedded files, and nested BNTX textures.
"""

from __future__ import annotations

import struct

from bntx_reader import is_bntx, list_textures, read_texture_data
from mc_io import decompress_mc_bytes, is_mcpk

_FRES_MAGIC = b"FRES"


def is_bfres(data: bytes) -> bool:
    return len(data) >= 4 and data[:4] == _FRES_MAGIC


def _read_i64(data: bytes, offset: int, le: bool) -> int:
    fmt = "<q" if le else ">q"
    return struct.unpack_from(fmt, data, offset)[0]


def _read_i32(data: bytes, offset: int, le: bool) -> int:
    fmt = "<i" if le else ">i"
    return struct.unpack_from(fmt, data, offset)[0]


def _read_u32(data: bytes, offset: int, le: bool) -> int:
    fmt = "<I" if le else ">I"
    return struct.unpack_from(fmt, data, offset)[0]


def _read_u16(data: bytes, offset: int, le: bool) -> int:
    fmt = "<H" if le else ">H"
    return struct.unpack_from(fmt, data, offset)[0]


def _read_string(data: bytes, offset: int, le: bool) -> str:
    if offset < 0 or offset >= len(data):
        return ""
    if offset + 2 <= len(data):
        str_len = _read_u16(data, offset, le)
        if 0 <= str_len <= 512 and offset + 2 + str_len < len(data):
            if data[offset + 2 + str_len] == 0:
                return data[offset + 2 : offset + 2 + str_len].decode("utf-8", errors="replace")
    end = data.find(b"\x00", offset)
    if end < 0:
        end = min(offset + 256, len(data))
    return data[offset:end].decode("utf-8", errors="replace")


def load_bfres_bytes(data: bytes) -> bytes:
    if is_mcpk(data):
        data = decompress_mc_bytes(data)
    if not is_bfres(data):
        raise ValueError("Not a BFRES file")
    return data


def _bfres_major_version(data: bytes) -> int:
    """Major version byte at 0x0A (nn::util::BinaryFileHeader), matching MeshCodec."""
    if len(data) <= 0x0A:
        return 0
    return data[0x0A]


def _parse_header_v9(data: bytes) -> dict:
    le = data[0x0C:0x0E] == b"\xff\xfe"
    major = _bfres_major_version(data)
    if major < 9:
        version_raw = _read_u32(data, 0x08, True)
        raise ValueError(
            f"Unsupported BFRES version 0x{version_raw:08X} (major {major})"
        )

    return {
        "le": le,
        "model_array": _read_i64(data, 0x28, le),
        "skel_anim_array": _read_i64(data, 0x58, le),
        "mat_anim_array": _read_i64(data, 0x68, le),
        "bone_vis_array": _read_i64(data, 0x78, le),
        "shape_anim_array": _read_i64(data, 0x88, le),
        "scene_anim_array": _read_i64(data, 0x98, le),
        "embedded_array": _read_i64(data, 0xB8, le),
        "embedded_dict": _read_i64(data, 0xC0, le),
        "num_models": _read_u16(data, 0xDC, le),
        "num_skel": _read_u16(data, 0xE2, le),
        "num_mat": _read_u16(data, 0xE4, le),
        "num_bone_vis": _read_u16(data, 0xE6, le),
        "num_shape": _read_u16(data, 0xE8, le),
        "num_scene": _read_u16(data, 0xEA, le),
        "num_embedded": _read_u16(data, 0xEC, le),
    }


def _dict_keys(data: bytes, dict_offset: int, le: bool) -> list[str]:
    if dict_offset <= 0 or dict_offset + 8 > len(data):
        return []
    count = _read_i32(data, dict_offset + 4, le)
    if count <= 0:
        return []
    entries_start = dict_offset + 8
    keys: list[str] = []
    stack = [0]
    visited: set[int] = set()
    while stack:
        index = stack.pop()
        if index in visited:
            continue
        visited.add(index)
        entry_offset = entries_start + index * 16
        if entry_offset + 16 > len(data):
            continue
        ref_bit = _read_i32(data, entry_offset, le)
        left = _read_u16(data, entry_offset + 4, le)
        right = _read_u16(data, entry_offset + 6, le)
        key_offset = _read_i64(data, entry_offset + 8, le)
        if ref_bit < 0 and 0 < key_offset < len(data):
            name = _read_string(data, key_offset, le)
            if name:
                keys.append(name)
        limit = count + 1
        if 0 <= left < limit:
            stack.append(left)
        if 0 <= right < limit:
            stack.append(right)
    return keys


def _array_entry_names(
    data: bytes,
    array_offset: int,
    count: int,
    name_field: int,
    le: bool,
) -> list[str]:
    if array_offset <= 0 or count <= 0:
        return []
    names: list[str] = []
    for index in range(count):
        entry_ptr = _read_i64(data, array_offset + index * 8, le)
        if entry_ptr <= 0 or entry_ptr + name_field + 8 > len(data):
            names.append(f"entry_{index}")
            continue
        name_ptr = _read_i64(data, entry_ptr + name_field, le)
        name = _read_string(data, name_ptr, le) if 0 < name_ptr < len(data) else ""
        names.append(name or f"entry_{index}")
    return names


def _embedded_files(data: bytes, header: dict) -> dict[str, bytes]:
    count = header["num_embedded"]
    if count <= 0:
        return {}
    names = _dict_keys(data, header["embedded_dict"], header["le"])
    files: dict[str, bytes] = {}
    array_offset = header["embedded_array"]
    for index in range(count):
        entry_ptr = _read_i64(data, array_offset + index * 8, header["le"])
        if entry_ptr <= 0 or entry_ptr + 12 > len(data):
            continue
        blob_offset = _read_i64(data, entry_ptr, header["le"])
        blob_size = _read_u32(data, entry_ptr + 8, header["le"])
        if blob_offset <= 0 or blob_size <= 0:
            continue
        end = blob_offset + blob_size
        if end > len(data):
            continue
        name = names[index] if index < len(names) else f"embedded_{index}"
        files[name] = data[blob_offset:end]
    return files


def _bntx_texture_paths(bntx_data: bytes, prefix: str) -> list[str]:
    names = list_textures(bntx_data)
    if prefix:
        return [f"{prefix}/{name}" for name in names]
    return names


def list_bfres_entries(data: bytes) -> list[str]:
    bfres = load_bfres_bytes(data)
    header = _parse_header_v9(bfres)
    paths: list[str] = []

    for name in _array_entry_names(bfres, header["model_array"], header["num_models"], 0x08, header["le"]):
        paths.append(f"Models/{name}")

    for name in _array_entry_names(
        bfres, header["skel_anim_array"], header["num_skel"], 0x08, header["le"]
    ):
        paths.append(f"Animations/Skeletal/{name}")

    for name in _array_entry_names(
        bfres, header["mat_anim_array"], header["num_mat"], 0x08, header["le"]
    ):
        paths.append(f"Animations/Material/{name}")

    for name in _array_entry_names(
        bfres, header["bone_vis_array"], header["num_bone_vis"], 0x08, header["le"]
    ):
        paths.append(f"Animations/BoneVisibility/{name}")

    for name in _array_entry_names(
        bfres, header["shape_anim_array"], header["num_shape"], 0x08, header["le"]
    ):
        paths.append(f"Animations/Shape/{name}")

    for name in _array_entry_names(
        bfres, header["scene_anim_array"], header["num_scene"], 0x08, header["le"]
    ):
        paths.append(f"Animations/Scene/{name}")

    embedded = _embedded_files(bfres, header)
    bntx_listed = False
    for name, blob in embedded.items():
        paths.append(f"Embedded/{name}")
        if not bntx_listed and is_bntx(blob):
            paths.extend(_bntx_texture_paths(blob, "Textures"))
            bntx_listed = True

    return paths


def read_bfres_file_bytes(data: bytes, internal_path: str) -> bytes:
    internal_path = internal_path.replace("\\", "/").strip("/")
    if not internal_path:
        raise IsADirectoryError(internal_path)

    bfres = load_bfres_bytes(data)

    if internal_path.startswith("Textures/"):
        texture_name = internal_path[len("Textures/") :]
        embedded = _embedded_files(bfres, _parse_header_v9(bfres))
        for blob in embedded.values():
            if is_bntx(blob):
                return read_texture_data(blob, texture_name)
        raise FileNotFoundError(internal_path)

    if internal_path.startswith("Embedded/"):
        name = internal_path[len("Embedded/") :]
        embedded = _embedded_files(bfres, _parse_header_v9(bfres))
        if name not in embedded:
            raise FileNotFoundError(internal_path)
        return embedded[name]

    raise IsADirectoryError(internal_path)
