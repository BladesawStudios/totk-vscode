"""Minimal DDS reader/writer for the texture import/export path.

Reads both legacy FourCC headers (DXT1/3/5, BC4U/S, BC5U/S, ATI1/ATI2,
uncompressed RGBA) and the DX10 extended header, and splits the payload into
per-mip linear surfaces. Writes legacy FourCC where one exists (BC1/2/3/4/5 and
uncompressed) and falls back to a DX10 header for BC6H/BC7.

Only 2D, single-array surfaces are handled. That covers every texture the BNTX
and TXTG editors deal with.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass

# Canonical format keys used across the texture pipeline.
# value: (block_bytes, block_w, block_h)
_BLOCK_INFO: dict[str, tuple[int, int, int]] = {
    "bc1": (8, 4, 4),
    "bc2": (16, 4, 4),
    "bc3": (16, 4, 4),
    "bc4": (8, 4, 4),
    "bc5": (16, 4, 4),
    "bc6": (16, 4, 4),
    "bc7": (16, 4, 4),
    "rgba8": (4, 1, 1),
    "bgra8": (4, 1, 1),
}

_FOURCC_TO_KEY = {
    b"DXT1": ("bc1", False, False),
    b"DXT3": ("bc2", False, False),
    b"DXT5": ("bc3", False, False),
    b"BC4U": ("bc4", False, False),
    b"BC4S": ("bc4", False, True),
    b"ATI1": ("bc4", False, False),
    b"BC5U": ("bc5", False, False),
    b"BC5S": ("bc5", False, True),
    b"ATI2": ("bc5", False, False),
}

# DXGI format -> (key, is_srgb, is_snorm)
_DXGI_TO_KEY = {
    28: ("rgba8", False, False),
    29: ("rgba8", True, False),
    87: ("bgra8", False, False),
    91: ("bgra8", True, False),
    71: ("bc1", False, False),
    72: ("bc1", True, False),
    74: ("bc2", False, False),
    75: ("bc2", True, False),
    77: ("bc3", False, False),
    78: ("bc3", True, False),
    80: ("bc4", False, False),
    81: ("bc4", False, True),
    83: ("bc5", False, False),
    84: ("bc5", False, True),
    95: ("bc6", False, False),
    96: ("bc6", False, True),
    98: ("bc7", False, False),
    99: ("bc7", True, False),
}

# key -> legacy FourCC (None means we must emit a DX10 header instead)
_KEY_TO_FOURCC = {
    "bc1": b"DXT1",
    "bc2": b"DXT3",
    "bc3": b"DXT5",
    "bc4": (b"BC4S", b"BC4U"),  # (snorm, unorm)
    "bc5": (b"BC5S", b"BC5U"),
}

# key -> DXGI format for the DX10 fallback path. (unorm, srgb, snorm)
_KEY_TO_DXGI = {
    "bc6": (95, None, 96),
    "bc7": (98, 99, None),
    "rgba8": (28, 29, None),
    "bgra8": (87, 91, None),
}


@dataclass
class DdsImage:
    width: int
    height: int
    mip_count: int
    key: str
    is_srgb: bool
    is_snorm: bool
    mips: list[bytes]

    @property
    def block_info(self) -> tuple[int, int, int]:
        return _BLOCK_INFO[self.key]


def div_round_up(n: int, d: int) -> int:
    return (n + d - 1) // d


def mip_linear_size(width: int, height: int, key: str) -> int:
    bpb, bw, bh = _BLOCK_INFO[key]
    return div_round_up(width, bw) * div_round_up(height, bh) * bpb


def is_dds(data: bytes) -> bool:
    return len(data) >= 4 and data[:4] == b"DDS "


def parse_dds(data: bytes) -> DdsImage:
    if not is_dds(data):
        raise ValueError("Not a DDS file.")
    if len(data) < 128:
        raise ValueError("DDS header truncated.")

    height = struct.unpack_from("<I", data, 12)[0]
    width = struct.unpack_from("<I", data, 16)[0]
    mip_count = struct.unpack_from("<I", data, 28)[0] or 1

    pf_flags = struct.unpack_from("<I", data, 80)[0]
    fourcc = data[84:88]
    rgb_bits = struct.unpack_from("<I", data, 88)[0]

    payload_off = 128
    key: str
    is_srgb = False
    is_snorm = False

    if pf_flags & 0x4 and fourcc == b"DX10":
        dxgi = struct.unpack_from("<I", data, 128)[0]
        payload_off = 148
        if dxgi not in _DXGI_TO_KEY:
            raise ValueError(f"Unsupported DXGI format {dxgi} in DDS.")
        key, is_srgb, is_snorm = _DXGI_TO_KEY[dxgi]
    elif pf_flags & 0x4 and fourcc in _FOURCC_TO_KEY:
        key, is_srgb, is_snorm = _FOURCC_TO_KEY[fourcc]
    elif pf_flags & 0x40:  # uncompressed RGB(A)
        if rgb_bits != 32:
            raise ValueError(f"Unsupported uncompressed DDS bit depth {rgb_bits}.")
        r = struct.unpack_from("<I", data, 92)[0]
        b = struct.unpack_from("<I", data, 100)[0]
        # BGRA if blue mask is in the low byte, otherwise RGBA.
        key = "bgra8" if (b == 0x000000FF and r == 0x00FF0000) else "rgba8"
    else:
        raise ValueError(f"Unrecognised DDS pixel format (fourcc={fourcc!r}).")

    mips: list[bytes] = []
    cursor = payload_off
    for mip in range(mip_count):
        mw = max(1, width >> mip)
        mh = max(1, height >> mip)
        size = mip_linear_size(mw, mh, key)
        chunk = data[cursor : cursor + size]
        if len(chunk) < size:
            # Some exporters omit trailing mips; stop cleanly at what we have.
            if mip == 0:
                raise ValueError("DDS payload too small for its declared dimensions.")
            break
        mips.append(chunk)
        cursor += size

    return DdsImage(width, height, len(mips), key, is_srgb, is_snorm, mips)


def build_dds(
    width: int,
    height: int,
    key: str,
    mips: list[bytes],
    is_srgb: bool = False,
    is_snorm: bool = False,
) -> bytes:
    if key not in _BLOCK_INFO:
        raise ValueError(f"Cannot build DDS for format key {key!r}.")

    mip_count = max(1, len(mips))
    linear0 = mip_linear_size(width, height, key)
    payload = b"".join(mips)

    flags = 0x1 | 0x2 | 0x4 | 0x1000  # CAPS | HEIGHT | WIDTH | PIXELFORMAT
    caps = 0x1000  # TEXTURE
    if mip_count > 1:
        flags |= 0x20000  # MIPMAPCOUNT
        caps |= 0x8 | 0x400000  # COMPLEX | MIPMAP

    compressed = key.startswith("bc")
    if compressed:
        flags |= 0x80000  # LINEARSIZE

    legacy_fourcc = _KEY_TO_FOURCC.get(key)
    if isinstance(legacy_fourcc, tuple):
        legacy_fourcc = legacy_fourcc[0] if is_snorm else legacy_fourcc[1]

    use_dx10 = compressed and legacy_fourcc is None

    header = bytearray(128)
    header[0:4] = b"DDS "
    struct.pack_into("<I", header, 4, 124)
    struct.pack_into("<I", header, 8, flags)
    struct.pack_into("<I", header, 12, height)
    struct.pack_into("<I", header, 16, width)
    struct.pack_into("<I", header, 20, linear0 if compressed else width * 4)
    struct.pack_into("<I", header, 28, mip_count)
    struct.pack_into("<I", header, 76, 32)  # ddspf size

    if compressed:
        struct.pack_into("<I", header, 80, 0x4)  # FOURCC
        header[84:88] = b"DX10" if use_dx10 else legacy_fourcc
    else:
        # Uncompressed BGRA8 / RGBA8.
        struct.pack_into("<I", header, 80, 0x1 | 0x40)  # ALPHAPIXELS | RGB
        struct.pack_into("<I", header, 88, 32)
        if key == "bgra8":
            masks = (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)
        else:
            masks = (0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000)
        for i, m in enumerate(masks):
            struct.pack_into("<I", header, 92 + i * 4, m)

    struct.pack_into("<I", header, 108, caps)

    if use_dx10 or (not compressed):
        # Uncompressed paths are unambiguous via masks; only emit DX10 when we
        # truly have no legacy FourCC (BC6H/BC7).
        if use_dx10:
            dxgi_tuple = _KEY_TO_DXGI[key]
            dxgi = (
                dxgi_tuple[1]
                if (is_srgb and dxgi_tuple[1])
                else (dxgi_tuple[2] if (is_snorm and dxgi_tuple[2]) else dxgi_tuple[0])
            )
            dx10 = bytearray(20)
            struct.pack_into("<I", dx10, 0, dxgi)
            struct.pack_into("<I", dx10, 4, 3)  # 2D
            struct.pack_into("<I", dx10, 12, 1)  # arraySize
            return bytes(header) + bytes(dx10) + payload

    return bytes(header) + payload


# ---------------------------------------------------------------------------
#  Format mapping between DDS keys and BNTX / TXTG format ids
# ---------------------------------------------------------------------------

# BNTX format key (format_id >> 8) -> dds key
_BNTX_FMT_TO_KEY = {
    0x1A: "bc1",
    0x1B: "bc2",
    0x1C: "bc3",
    0x1D: "bc4",
    0x1E: "bc5",
    0x1F: "bc6",
    0x20: "bc7",
    0x0B: "rgba8",
    0x0C: "bgra8",
}
_KEY_TO_BNTX_FMT = {v: k for k, v in _BNTX_FMT_TO_KEY.items()}

# TXTG format id -> dds key (from txtg_reader._FORMAT_MAP)
_TXTG_FMT_TO_KEY = {
    0x202: "bc1",
    0x203: "bc1",
    0x302: "bc1",
    0x303: "bc1",
    0x505: "bc3",
    0x602: "bc4",
    0x606: "bc4",
    0x607: "bc4",
    0x702: "bc5",
    0x703: "bc5",
    0x707: "bc5",
    0x901: "bc7",
    0x0B0B: "rgba8",
    0x0C0C: "bgra8",
}


def bntx_format_to_key(format_id: int) -> str | None:
    return _BNTX_FMT_TO_KEY.get(format_id >> 8)


def key_to_bntx_format(key: str, is_srgb: bool, is_snorm: bool) -> int | None:
    base = _KEY_TO_BNTX_FMT.get(key)
    if base is None:
        return None
    variant = 0x06 if is_srgb else (0x02 if is_snorm else 0x01)
    return (base << 8) | variant


def txtg_format_to_key(format_id: int) -> str | None:
    return _TXTG_FMT_TO_KEY.get(format_id)


# Canonical TXTG format id to write when an import changes the compression
# format. (unorm, srgb) where a distinct srgb id exists.
_KEY_TO_TXTG_FMT = {
    "bc1": (0x302, 0x303),
    "bc3": (0x505, 0x505),
    "bc4": (0x607, 0x607),
    "bc5": (0x707, 0x707),
    "bc7": (0x901, 0x901),
}


def key_to_txtg_format(key: str, is_srgb: bool = False) -> int | None:
    pair = _KEY_TO_TXTG_FMT.get(key)
    if pair is None:
        return None
    return pair[1] if is_srgb else pair[0]
