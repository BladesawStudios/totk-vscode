"""DDS reader/writer for the texture import/export path.

Mirrors how Switch Toolbox (the community-standard tool) writes DDS so files
interoperate in both directions:

* BC1/BC2/BC3/BC4/BC5 use legacy FourCC (DXT1/DXT3/DXT5, BC4U/S, BC5U/S).
* BC6H, BC7 and every ASTC block size use the DX10 extended header with the
  appropriate DXGI format. The ASTC DXGI values are the unofficial set
  (ASTC_4x4_UNORM = 134 ... ASTC_12x12_UNORM_SRGB = 187) that Switch Toolbox,
  the Nvidia Texture Tools exporter, and AMD Compressonator all use.
* Plain colour formats use uncompressed pixel masks.

The payload is raw block/pixel data, deswizzled to a linear layout, split per
mip. Nothing is decoded or re-encoded, so the round-trip is lossless for every
format. Only 2D, single-array surfaces are handled here.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass

# ---------------------------------------------------------------------------
#  ASTC block sizes, in the order Switch Toolbox's DXGI enum lays them out.
#  Each block size occupies 4 DXGI slots (TYPELESS, UNORM, UNORM_SRGB, gap)
#  starting at 133, so UNORM = 134 + 4*index and SRGB = UNORM + 1.
# ---------------------------------------------------------------------------
_ASTC_BLOCKS = [
    (4, 4),
    (5, 4),
    (5, 5),
    (6, 5),
    (6, 6),
    (8, 5),
    (8, 6),
    (8, 8),
    (10, 5),
    (10, 6),
    (10, 8),
    (10, 10),
    (12, 10),
    (12, 12),
]


def _astc_key(bw: int, bh: int) -> str:
    return f"astc{bw}x{bh}"


# Canonical format keys -> (block_bytes, block_w, block_h)
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
    "r8": (1, 1, 1),
    "rg8": (2, 1, 1),
    "rgb565": (2, 1, 1),
    "rgba4": (2, 1, 1),
    "rgb10a2": (4, 1, 1),
}
for _bw, _bh in _ASTC_BLOCKS:
    _BLOCK_INFO[_astc_key(_bw, _bh)] = (16, _bw, _bh)

# Block-based formats (payload is raw blocks; DDS LINEARSIZE flag applies).
_COMPRESSED_KEYS = {k for k in _BLOCK_INFO if k.startswith(("bc", "astc"))}

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
_DXGI_TO_KEY: dict[int, tuple[str, bool, bool]] = {
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

# key -> (unorm_dxgi, srgb_dxgi or None, snorm_dxgi or None) for the DX10 path.
_KEY_TO_DXGI: dict[str, tuple[int, int | None, int | None]] = {
    "bc6": (95, None, 96),
    "bc7": (98, 99, None),
    "rgba8": (28, 29, None),
    "bgra8": (87, 91, None),
}

# Fill in the ASTC DXGI tables both directions.
for _i, (_bw, _bh) in enumerate(_ASTC_BLOCKS):
    _unorm = 134 + 4 * _i
    _srgb = _unorm + 1
    _k = _astc_key(_bw, _bh)
    _DXGI_TO_KEY[_unorm] = (_k, False, False)
    _DXGI_TO_KEY[_srgb] = (_k, True, False)
    _KEY_TO_DXGI[_k] = (_unorm, _srgb, None)

# key -> legacy FourCC (BC1..BC5). A tuple is (snorm, unorm).
_KEY_TO_FOURCC: dict[str, bytes | tuple[bytes, bytes]] = {
    "bc1": b"DXT1",
    "bc2": b"DXT3",
    "bc3": b"DXT5",
    "bc4": (b"BC4S", b"BC4U"),
    "bc5": (b"BC5S", b"BC5U"),
}

# Uncompressed pixel layouts: key -> (rgb_bits, Rmask, Gmask, Bmask, Amask).
# Amask 0 means no alpha channel. These match Switch Toolbox's masks where it
# defines them; the small formats store native bytes so our round-trip is exact.
_UNCOMPRESSED: dict[str, tuple[int, int, int, int, int]] = {
    "rgba8": (32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000),
    "bgra8": (32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
    "r8": (8, 0x000000FF, 0, 0, 0),
    "rg8": (16, 0x000000FF, 0x0000FF00, 0, 0),
    "rgb565": (16, 0xF800, 0x07E0, 0x001F, 0),
    "rgba4": (16, 0x0F00, 0x00F0, 0x000F, 0xF000),
    "rgb10a2": (32, 0x000003FF, 0x000FFC00, 0x3FF00000, 0xC0000000),
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


def _match_uncompressed(rgb_bits: int, r: int, g: int, b: int, a: int) -> str | None:
    for key, (bits, rm, gm, bm, am) in _UNCOMPRESSED.items():
        if bits != rgb_bits:
            continue
        # Match on the masks that distinguish the format. A zero stored alpha
        # mask means "don't care" so files that set it are still accepted.
        if rm == r and gm == g and bm == b and (am == a or am == 0):
            return key
    return None


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
    elif pf_flags & (0x40 | 0x1):  # uncompressed RGB / RGBA
        r = struct.unpack_from("<I", data, 92)[0]
        g = struct.unpack_from("<I", data, 96)[0]
        b = struct.unpack_from("<I", data, 100)[0]
        a = struct.unpack_from("<I", data, 104)[0]
        matched = _match_uncompressed(rgb_bits, r, g, b, a)
        if matched is None:
            raise ValueError(f"Unsupported uncompressed DDS ({rgb_bits}bpp, R=0x{r:X} A=0x{a:X}).")
        key = matched
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
    block_based = key in _COMPRESSED_KEYS

    flags = 0x1 | 0x2 | 0x4 | 0x1000  # CAPS | HEIGHT | WIDTH | PIXELFORMAT
    caps = 0x1000  # TEXTURE
    if mip_count > 1:
        flags |= 0x20000  # MIPMAPCOUNT
        caps |= 0x8 | 0x400000  # COMPLEX | MIPMAP
    if block_based:
        flags |= 0x80000  # LINEARSIZE
    else:
        flags |= 0x8  # PITCH

    legacy_fourcc = _KEY_TO_FOURCC.get(key)
    if isinstance(legacy_fourcc, tuple):
        legacy_fourcc = legacy_fourcc[0] if is_snorm else legacy_fourcc[1]
    use_dx10 = key in _KEY_TO_DXGI and legacy_fourcc is None

    bpb = _BLOCK_INFO[key][0]
    pitch_or_linear = linear0 if block_based else (width * bpb)

    header = bytearray(128)
    header[0:4] = b"DDS "
    struct.pack_into("<I", header, 4, 124)
    struct.pack_into("<I", header, 8, flags)
    struct.pack_into("<I", header, 12, height)
    struct.pack_into("<I", header, 16, width)
    struct.pack_into("<I", header, 20, pitch_or_linear)
    struct.pack_into("<I", header, 28, mip_count)
    struct.pack_into("<I", header, 76, 32)  # ddspf size

    if legacy_fourcc is not None or use_dx10:
        struct.pack_into("<I", header, 80, 0x4)  # FOURCC
        header[84:88] = b"DX10" if use_dx10 else legacy_fourcc
    else:
        bits, rm, gm, bm, am = _UNCOMPRESSED[key]
        pf = 0x40  # RGB
        if am:
            pf |= 0x1  # ALPHAPIXELS
        struct.pack_into("<I", header, 80, pf)
        struct.pack_into("<I", header, 88, bits)
        for i, m in enumerate((rm, gm, bm, am)):
            struct.pack_into("<I", header, 92 + i * 4, m)

    struct.pack_into("<I", header, 108, caps)

    if use_dx10:
        unorm, srgb, snorm = _KEY_TO_DXGI[key]
        dxgi = srgb if (is_srgb and srgb) else (snorm if (is_snorm and snorm) else unorm)
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
    0x02: "r8",
    0x09: "rg8",
    0x07: "rgb565",
    0x08: "rgb565",
    0x05: "rgb565",
    0x06: "rgb565",
    0x3B: "rgb565",
    0x03: "rgba4",
    0x04: "rgba4",
    0x0E: "rgb10a2",
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
for _i, (_bw, _bh) in enumerate(_ASTC_BLOCKS):
    _BNTX_FMT_TO_KEY[0x2D + _i] = _astc_key(_bw, _bh)

# Preferred BNTX format key to write per dds key (first definition wins).
_KEY_TO_BNTX_FMT: dict[str, int] = {}
for _bntx_key, _dds_key in _BNTX_FMT_TO_KEY.items():
    _KEY_TO_BNTX_FMT.setdefault(_dds_key, _bntx_key)

# TXTG format id -> dds key (from txtg_reader._FORMAT_MAP)
_TXTG_FMT_TO_KEY = {
    0x101: "astc4x4",
    0x109: "astc4x4",
    0x102: "astc8x8",
    0x105: "astc8x8",
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
    "astc4x4": (0x101, 0x109),
    "astc8x8": (0x102, 0x105),
    "bc1": (0x302, 0x303),
    "bc3": (0x505, 0x505),
    "bc4": (0x607, 0x607),
    "bc5": (0x707, 0x707),
    "bc7": (0x901, 0x901),
    "rgba8": (0x0B0B, 0x0B0B),
    "bgra8": (0x0C0C, 0x0C0C),
}


def key_to_txtg_format(key: str, is_srgb: bool = False) -> int | None:
    pair = _KEY_TO_TXTG_FMT.get(key)
    if pair is None:
        return None
    return pair[1] if is_srgb else pair[0]


# ---------------------------------------------------------------------------
#  Editable export for the small uncompressed formats
#
#  These store one or two channels, or a packed 16-bit pixel, which most image
#  editors will not open from a DDS. So they are exported expanded to a plain
#  RGBA8 DDS and collapsed back to their native layout on import. Every channel
#  is 8 bits or fewer, so the expand/collapse pair is an exact inverse for an
#  unedited round-trip. R10G10B10A2 is excluded because its 10-bit channels do
#  not fit in 8-bit RGBA; it stays native.
# ---------------------------------------------------------------------------
_SMALL_RGBA8_EXPAND = {"r8", "rg8", "rgb565", "rgba4"}


def expand_to_rgba8(key: str, data: bytes, pixel_count: int) -> bytes:
    import numpy as np

    out = np.empty((pixel_count, 4), np.uint8)
    out[:, 3] = 255
    if key == "r8":
        r = np.frombuffer(data[:pixel_count], dtype=np.uint8)
        out[:, 0] = r
        out[:, 1] = r
        out[:, 2] = r
    elif key == "rg8":
        px = np.frombuffer(data[: pixel_count * 2], dtype=np.uint8).reshape(-1, 2)
        out[:, 0] = px[:, 0]
        out[:, 1] = px[:, 1]
        out[:, 2] = 0
    elif key == "rgb565":
        v = np.frombuffer(data[: pixel_count * 2], dtype="<u2").astype(np.uint16)
        r5 = (v >> 11) & 0x1F
        g6 = (v >> 5) & 0x3F
        b5 = v & 0x1F
        out[:, 0] = ((r5 << 3) | (r5 >> 2)).astype(np.uint8)
        out[:, 1] = ((g6 << 2) | (g6 >> 4)).astype(np.uint8)
        out[:, 2] = ((b5 << 3) | (b5 >> 2)).astype(np.uint8)
    elif key == "rgba4":
        v = np.frombuffer(data[: pixel_count * 2], dtype="<u2").astype(np.uint16)
        a4 = (v >> 12) & 0xF
        r4 = (v >> 8) & 0xF
        g4 = (v >> 4) & 0xF
        b4 = v & 0xF
        out[:, 0] = ((r4 << 4) | r4).astype(np.uint8)
        out[:, 1] = ((g4 << 4) | g4).astype(np.uint8)
        out[:, 2] = ((b4 << 4) | b4).astype(np.uint8)
        out[:, 3] = ((a4 << 4) | a4).astype(np.uint8)
    else:
        raise ValueError(f"No RGBA8 expansion for {key!r}.")
    return out.tobytes()


def collapse_rgba8(key: str, rgba: bytes, pixel_count: int) -> bytes:
    import numpy as np

    px = np.frombuffer(rgba[: pixel_count * 4], dtype=np.uint8).reshape(-1, 4)
    if key == "r8":
        return px[:, 0].tobytes()
    if key == "rg8":
        out = np.empty((pixel_count, 2), np.uint8)
        out[:, 0] = px[:, 0]
        out[:, 1] = px[:, 1]
        return out.tobytes()
    if key == "rgb565":
        r5 = px[:, 0].astype(np.uint16) >> 3
        g6 = px[:, 1].astype(np.uint16) >> 2
        b5 = px[:, 2].astype(np.uint16) >> 3
        return ((r5 << 11) | (g6 << 5) | b5).astype("<u2").tobytes()
    if key == "rgba4":
        r4 = px[:, 0].astype(np.uint16) >> 4
        g4 = px[:, 1].astype(np.uint16) >> 4
        b4 = px[:, 2].astype(np.uint16) >> 4
        a4 = px[:, 3].astype(np.uint16) >> 4
        return ((a4 << 12) | (r4 << 8) | (g4 << 4) | b4).astype("<u2").tobytes()
    raise ValueError(f"No RGBA8 collapse for {key!r}.")
