"""Read-only BFRES (.bfres / .bfres.mc) archive support for TKVSC.

Lists internal resources similar to Switch Toolbox's BFRES tree:
Models, animation groups, embedded files, and nested BNTX textures.
"""

from __future__ import annotations

import os
import struct
from functools import lru_cache
from pathlib import Path

from bntx_reader import is_bntx, list_textures, read_texture_data
from mc_io import decompress_mc_bytes, is_mcpk

_FRES_MAGIC = b"FRES"
_HAS_EXTERNAL_STRING = 1 << 1
_EMBEDDED_FOLDER = "Embedded Files"
_EXTERNAL_STRING_REL = os.path.join("Shader", "ExternalBinaryString.bfres.mc")


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


def _read_nw_string(data: bytes, offset: int, le: bool) -> str:
    """Read an nn::util NW string (u16 length + bytes + optional null)."""
    if offset < 0 or offset + 2 > len(data):
        return ""
    str_len = _read_u16(data, offset, le)
    if not (0 < str_len <= 1024):
        return _read_c_string(data, offset)
    end = offset + 2 + str_len
    if end > len(data):
        return ""
    raw = data[offset + 2 : end]
    if raw and raw[-1] == 0:
        raw = raw[:-1]
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("utf-8", errors="replace")


def _read_c_string(data: bytes, offset: int) -> str:
    if offset < 0 or offset >= len(data):
        return ""
    end = data.find(b"\x00", offset)
    if end < 0:
        end = min(offset + 512, len(data))
    return data[offset:end].decode("utf-8", errors="replace")


def _resolve_string_ptr(
    ptr: int,
    local: bytes,
    external: bytes | None,
    le: bool,
) -> str:
    if ptr <= 0:
        return ""
    for blob in (local, external or b""):
        if not blob or ptr >= len(blob):
            continue
        name = _read_nw_string(blob, ptr, le)
        if name:
            return name
        name = _read_c_string(blob, ptr)
        if name:
            return name
    return ""


def load_bfres_bytes(data: bytes) -> bytes:
    if is_mcpk(data):
        data = decompress_mc_bytes(data)
    if not is_bfres(data):
        raise ValueError("Not a BFRES file")
    return data


def _bfres_major_version(data: bytes) -> int:
    if len(data) <= 0x0B:
        return 0
    return _read_u16(data, 0x0A, True)


def _external_flags(data: bytes) -> int:
    if _bfres_major_version(data) < 10 or len(data) <= 0xEE:
        return 0
    return data[0xEE]


@lru_cache(maxsize=2)
def _load_external_string_bfres(romfs_path: str) -> bytes | None:
    if not romfs_path:
        return None
    path = os.path.join(romfs_path, _EXTERNAL_STRING_REL)
    if not os.path.isfile(path):
        return None
    try:
        return load_bfres_bytes(Path(path).read_bytes())
    except (OSError, ValueError):
        return None


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
        "external_flags": _external_flags(data),
        "model_array": _read_i64(data, 0x28, le),
        "model_dict": _read_i64(data, 0x30, le),
        "skel_anim_array": _read_i64(data, 0x58, le),
        "skel_anim_dict": _read_i64(data, 0x60, le),
        "mat_anim_array": _read_i64(data, 0x68, le),
        "mat_anim_dict": _read_i64(data, 0x70, le),
        "bone_vis_array": _read_i64(data, 0x78, le),
        "bone_vis_dict": _read_i64(data, 0x80, le),
        "shape_anim_array": _read_i64(data, 0x88, le),
        "shape_anim_dict": _read_i64(data, 0x90, le),
        "scene_anim_array": _read_i64(data, 0x98, le),
        "scene_anim_dict": _read_i64(data, 0xA0, le),
        "embedded_array": _read_i64(data, 0xB8, le),
        "embedded_dict": _read_i64(data, 0xC0, le),
        "string_table": _read_i64(data, 0xD0, le),
        "num_models": _read_u16(data, 0xDC, le),
        "num_skel": _read_u16(data, 0xE2, le),
        "num_mat": _read_u16(data, 0xE4, le),
        "num_bone_vis": _read_u16(data, 0xE6, le),
        "num_shape": _read_u16(data, 0xE8, le),
        "num_scene": _read_u16(data, 0xEA, le),
        "num_embedded": _read_u16(data, 0xEC, le),
    }


def _dict_keys_ordered(
    data: bytes,
    dict_offset: int,
    le: bool,
    expected_count: int,
    local: bytes,
    external: bytes | None,
) -> list[str]:
    """Return ResDic keys in array-index order (Switch nn::util::ResDic / Syroot GetKey)."""
    if dict_offset <= 0 or expected_count <= 0:
        return []

    node_count = _read_i32(data, dict_offset + 4, le)
    if node_count <= 0:
        return []

    entries_start = dict_offset + 8
    names: list[str] = []
    for index in range(1, node_count + 1):
        entry_offset = entries_start + index * 16
        if entry_offset + 16 > len(data):
            break
        key_ptr = _read_i64(data, entry_offset + 8, le)
        name = _resolve_string_ptr(key_ptr, local, external, le)
        names.append(name or f"entry_{index - 1}")

    while len(names) < expected_count:
        names.append(f"entry_{len(names)}")
    return names[:expected_count]


def _array_entry_names(
    data: bytes,
    array_offset: int,
    count: int,
    name_field: int,
    le: bool,
    local: bytes,
    external: bytes | None,
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
        name = _resolve_string_ptr(name_ptr, local, external, le)
        names.append(name or f"entry_{index}")
    return names


def _resource_names(
    data: bytes,
    header: dict,
    dict_offset: int,
    array_offset: int,
    count: int,
    name_field: int,
    external: bytes | None,
) -> list[str]:
    le = header["le"]
    local = data
    names = _dict_keys_ordered(data, dict_offset, le, count, local, external)
    if any(name.startswith("entry_") for name in names):
        fallback = _array_entry_names(
            data, array_offset, count, name_field, le, local, external
        )
        names = [
            dict_name if not dict_name.startswith("entry_") else fallback[i]
            for i, dict_name in enumerate(names)
        ]
    return names


def _embedded_names(
    data: bytes,
    header: dict,
    external: bytes | None,
) -> list[str]:
    count = header["num_embedded"]
    if count <= 0:
        return []
    return _dict_keys_ordered(
        data,
        header["embedded_dict"],
        header["le"],
        count,
        data,
        external,
    )


def _read_switch_external_file(
    data: bytes,
    struct_offset: int,
    le: bool,
) -> bytes | None:
    if struct_offset <= 0 or struct_offset + 16 > len(data):
        return None
    if data[struct_offset : struct_offset + 4] == b"_RLT":
        return None
    blob_offset = _read_i64(data, struct_offset, le)
    blob_size = _read_i64(data, struct_offset + 8, le)
    if blob_offset <= 0 or blob_size <= 0:
        return None
    end = blob_offset + blob_size
    if end > len(data):
        return None
    return data[blob_offset:end]


def _resolve_switch_external_struct_offsets(
    data: bytes,
    header: dict,
) -> list[int]:
    count = header["num_embedded"]
    if count <= 0:
        return []
    le = header["le"]
    array_offset = header["embedded_array"]
    offsets: list[int] = []
    for index in range(count):
        entry_ptr = _read_i64(data, array_offset + index * 8, le)
        if entry_ptr > 0 and entry_ptr + 16 <= len(data):
            if data[entry_ptr : entry_ptr + 4] != b"_RLT":
                offsets.append(entry_ptr)
    if len(offsets) == count and all(
        _read_switch_external_file(data, off, le) is not None for off in offsets
    ):
        return offsets

    # Fallback: locate Switch ExternalFile headers (offset + u64 size) before _RLT.
    rlt = data.rfind(b"_RLT")
    if rlt < 0:
        return offsets[:count]
    candidates: list[tuple[int, int, int]] = []
    scan_start = max(8, rlt - 65_536)
    for pos in range(scan_start, rlt - 16, 8):
        blob_offset = _read_i64(data, pos, le)
        blob_size = _read_i64(data, pos + 8, le)
        if blob_offset <= 0 or blob_size <= 0 or blob_size > 8_000_000:
            continue
        end = blob_offset + blob_size
        if end > rlt or blob_offset < 256:
            continue
        blob = data[blob_offset:end]
        if not blob or blob == b"\x00" * len(blob):
            continue
        if b"FMDL" in blob[:16] or b"FSKL" in blob[:16] or b"_STR" in blob[:16]:
            continue
        # Skip vertex-ish float blobs (common false positive in model data).
        if len(blob) >= 64 and len(blob) % 4 == 0:
            floats = struct.unpack(f"<{min(len(blob)//4, 64)}f", blob[: 64 * 4])
            finite = [abs(v) for v in floats if v == v and abs(v) < 1e6]
            if len(finite) >= 48 and sum(1 for v in finite if v < 1000) / len(finite) > 0.85:
                continue
        candidates.append((pos, blob_offset, blob_size))
    if not candidates:
        return offsets[:count]
    # Prefer candidates nearest the relocation table (WriteBlocks order).
    candidates.sort(key=lambda item: item[0], reverse=True)
    seen: set[tuple[int, int]] = set()
    resolved: list[int] = []
    for pos, blob_offset, blob_size in candidates:
        key = (blob_offset, blob_size)
        if key in seen:
            continue
        seen.add(key)
        resolved.append(pos)
        if len(resolved) >= count:
            break
    return resolved


def _embedded_files(
    data: bytes,
    header: dict,
    external: bytes | None,
) -> dict[str, bytes]:
    count = header["num_embedded"]
    if count <= 0:
        return {}
    names = _embedded_names(data, header, external)
    files: dict[str, bytes] = {}
    le = header["le"]
    struct_offsets = _resolve_switch_external_struct_offsets(data, header)
    for index in range(count):
        name = names[index] if index < len(names) else f"embedded_{index}"
        blob = None
        if index < len(struct_offsets):
            blob = _read_switch_external_file(data, struct_offsets[index], le)
        if blob is None:
            # Wii U / legacy layout: u64 offset + u32 size on the resource header.
            array_offset = header["embedded_array"]
            entry_ptr = _read_i64(data, array_offset + index * 8, le)
            if entry_ptr > 0 and entry_ptr + 12 <= len(data):
                blob_offset = _read_i64(data, entry_ptr, le)
                blob_size = _read_u32(data, entry_ptr + 8, le)
                if blob_offset > 0 and blob_size > 0:
                    end = blob_offset + blob_size
                    if end <= len(data):
                        blob = data[blob_offset:end]
        if blob is not None:
            files[name] = blob
    return files


def _bntx_texture_paths(bntx_data: bytes, prefix: str) -> list[str]:
    names = list_textures(bntx_data)
    if prefix:
        return [f"{prefix}/{name}" for name in names]
    return names


def list_bfres_entries(data: bytes, romfs_path: str = "") -> list[str]:
    bfres = load_bfres_bytes(data)
    header = _parse_header_v9(bfres)
    use_external = bool(header["external_flags"] & _HAS_EXTERNAL_STRING)
    external = _load_external_string_bfres(romfs_path) if use_external else None

    paths: list[str] = []

    for name in _resource_names(
        bfres,
        header,
        header["model_dict"],
        header["model_array"],
        header["num_models"],
        0x08,
        external,
    ):
        paths.append(f"Models/{name}")

    for name in _resource_names(
        bfres,
        header,
        header["skel_anim_dict"],
        header["skel_anim_array"],
        header["num_skel"],
        0x08,
        external,
    ):
        paths.append(f"Animations/Skeletal/{name}")

    for name in _resource_names(
        bfres,
        header,
        header["mat_anim_dict"],
        header["mat_anim_array"],
        header["num_mat"],
        0x08,
        external,
    ):
        paths.append(f"Animations/Material/{name}")

    for name in _resource_names(
        bfres,
        header,
        header["bone_vis_dict"],
        header["bone_vis_array"],
        header["num_bone_vis"],
        0x08,
        external,
    ):
        paths.append(f"Animations/BoneVisibility/{name}")

    for name in _resource_names(
        bfres,
        header,
        header["shape_anim_dict"],
        header["shape_anim_array"],
        header["num_shape"],
        0x08,
        external,
    ):
        paths.append(f"Animations/Shape/{name}")

    for name in _resource_names(
        bfres,
        header,
        header["scene_anim_dict"],
        header["scene_anim_array"],
        header["num_scene"],
        0x08,
        external,
    ):
        paths.append(f"Animations/Scene/{name}")

    for name in _embedded_names(bfres, header, external):
        paths.append(f"{_EMBEDDED_FOLDER}/{name}")

    embedded = _embedded_files(bfres, header, external)
    bntx_listed = False
    for name, blob in embedded.items():
        if not bntx_listed and is_bntx(blob):
            paths.extend(_bntx_texture_paths(blob, "Textures"))
            bntx_listed = True

    return paths


def read_bfres_file_bytes(
    data: bytes,
    internal_path: str,
    romfs_path: str = "",
) -> bytes:
    internal_path = internal_path.replace("\\", "/").strip("/")
    if not internal_path:
        raise IsADirectoryError(internal_path)

    bfres = load_bfres_bytes(data)
    header = _parse_header_v9(bfres)
    use_external = bool(header["external_flags"] & _HAS_EXTERNAL_STRING)
    external = _load_external_string_bfres(romfs_path) if use_external else None

    if internal_path.startswith("Textures/"):
        texture_name = internal_path[len("Textures/") :]
        embedded = _embedded_files(bfres, header, external)
        for blob in embedded.values():
            if is_bntx(blob):
                return read_texture_data(blob, texture_name)
        raise FileNotFoundError(internal_path)

    if internal_path.startswith(f"{_EMBEDDED_FOLDER}/") or internal_path.startswith(
        "Embedded/"
    ):
        name = internal_path.split("/", 1)[1]
        embedded = _embedded_files(bfres, header, external)
        if name not in embedded:
            raise FileNotFoundError(internal_path)
        return embedded[name]

    raise IsADirectoryError(internal_path)
