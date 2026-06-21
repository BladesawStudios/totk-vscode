"""Tegra X1 block-linear (re)swizzle helpers shared by BNTX and TXTG editors.

Deswizzle already lives in ``bntx_renderer`` for the read path. This module adds
the *write* path: turning linear (DDS-style) mip data back into the swizzled
block-linear layout the Switch GPU expects, matching Switch Toolbox's
``SwizzleSurfaceMipMaps``.

The heavy lifting is done by ``py_tegra_swizzle`` (a binding around
ScanMountGoat's ``tegra_swizzle`` Rust crate, the same code Switch Toolbox calls).
A pure-Python fallback is provided so the editor still works if the native
module is unavailable, ported from AboodXD's BNTX-Extractor swizzle.py.
"""

from __future__ import annotations


def div_round_up(n: int, d: int) -> int:
    return (n + d - 1) // d


def round_up(x: int, y: int) -> int:
    return ((x - 1) | (y - 1)) + 1


def pow2_round_up(x: int) -> int:
    x -= 1
    x |= x >> 1
    x |= x >> 2
    x |= x >> 4
    x |= x >> 8
    x |= x >> 16
    return x + 1


# ---------------------------------------------------------------------------
#  Native engine (preferred)
# ---------------------------------------------------------------------------

try:
    import py_tegra_swizzle as _pts
    from py_tegra_swizzle import PyBlockDim as _PyBlockDim

    _HAVE_PTS = True
except Exception:  # pragma: no cover - exercised only without the native dep
    _pts = None
    _PyBlockDim = None
    _HAVE_PTS = False


def block_height_mip0(height_in_blocks: int) -> int:
    """Block height (in GOBs) for mip 0, as a power of two (1..16)."""
    if _HAVE_PTS:
        return int(_pts.block_height_mip0(max(1, height_in_blocks)))
    # tegra_swizzle reference: thresholds on (height + height/2), not a plain
    # power-of-two round of height/8.
    h = height_in_blocks + height_in_blocks // 2
    if h >= 128:
        return 16
    if h >= 64:
        return 8
    if h >= 32:
        return 4
    if h >= 16:
        return 2
    return 1


def mip_block_height(mip_height_in_blocks: int, block_height_mip0_value: int) -> int:
    """Per-mip block height, shrinking as the mip gets smaller."""
    if _HAVE_PTS:
        return int(_pts.mip_block_height(max(1, mip_height_in_blocks), block_height_mip0_value))
    bh = block_height_mip0_value
    while bh > 1 and pow2_round_up(mip_height_in_blocks) < bh * 8:
        bh //= 2
    return max(1, bh)


def swizzled_mip_size(
    width: int, height: int, blk_w: int, blk_h: int, bpp: int, block_height: int
) -> int:
    """Canonical swizzled byte size of a single mip for the given block height."""
    if _HAVE_PTS:
        return int(
            _pts.get_swizzled_surface_size(
                width, height, 1, _PyBlockDim(blk_w, blk_h, 1), block_height, bpp, 1, 1
            )
        )
    wb = div_round_up(width, blk_w)
    hb = div_round_up(height, blk_h)
    pitch = round_up(wb * bpp, 64)
    return pitch * round_up(hb, block_height * 8)


# ---------------------------------------------------------------------------
#  Single-mip swizzle / deswizzle
# ---------------------------------------------------------------------------


def swizzle_mip(
    width: int, height: int, blk_w: int, blk_h: int, bpp: int, block_height: int, data: bytes
) -> bytes:
    """Swizzle one linear mip into block-linear. ``block_height`` is in GOBs."""
    wb = div_round_up(width, blk_w)
    hb = div_round_up(height, blk_h)
    need = wb * hb * bpp
    if len(data) < need:
        data = bytes(data) + b"\x00" * (need - len(data))
    else:
        data = bytes(data[:need])

    if _HAVE_PTS:
        return bytes(_pts.swizzle_block_linear(wb, hb, 1, data, block_height, bpp))
    return _swizzle_block_linear_py(wb, hb, bpp, block_height, data, to_swizzle=True)


def deswizzle_mip(
    width: int, height: int, blk_w: int, blk_h: int, bpp: int, block_height: int, data: bytes
) -> bytes:
    """Deswizzle one block-linear mip into linear. Lenient about short input."""
    wb = div_round_up(width, blk_w)
    hb = div_round_up(height, blk_h)
    if _HAVE_PTS:
        need = swizzled_mip_size(width, height, blk_w, blk_h, bpp, block_height)
        if len(data) < need:
            data = bytes(data) + b"\x00" * (need - len(data))
        return bytes(_pts.deswizzle_block_linear(wb, hb, 1, data[:need], block_height, bpp))
    return _swizzle_block_linear_py(wb, hb, bpp, block_height, data, to_swizzle=False)


def _swizzle_block_linear_py(
    width_in_blocks: int,
    height_in_blocks: int,
    bpp: int,
    block_height: int,
    data: bytes,
    to_swizzle: bool,
) -> bytes:
    """Pure-Python block-linear (de)swizzle (AboodXD getAddrBlockLinear)."""
    bh8 = 8 * block_height
    bh512 = 512 * block_height
    pitch = round_up(width_in_blocks * bpp, 64)
    surf_size = pitch * round_up(height_in_blocks, bh8)
    image_width_in_gobs = div_round_up(pitch, 64)
    gob_y_stride = bh512 * image_width_in_gobs

    linear_size = width_in_blocks * height_in_blocks * bpp
    if to_swizzle:
        src = data if len(data) >= linear_size else data + b"\x00" * (linear_size - len(data))
        dst = bytearray(surf_size)
    else:
        src = data
        dst = bytearray(linear_size)

    x_parts = []
    for x in range(width_in_blocks):
        xb = x * bpp
        x_parts.append(
            (
                xb,
                (xb // 64) * bh512 + ((xb % 64) // 32) * 256 + ((xb % 32) // 16) * 32 + (xb % 16),
            )
        )

    for y in range(height_in_blocks):
        y_part = (
            (y // bh8) * gob_y_stride + ((y % bh8) // 8) * 512 + ((y % 8) // 2) * 64 + (y % 2) * 16
        )
        lin_row = y * width_in_blocks * bpp
        for xb, x_part in x_parts:
            sw = y_part + x_part
            lin = lin_row + xb
            if to_swizzle:
                if sw + bpp <= surf_size and lin + bpp <= len(src):
                    dst[sw : sw + bpp] = src[lin : lin + bpp]
            else:
                if sw + bpp <= len(src) and lin + bpp <= linear_size:
                    dst[lin : lin + bpp] = src[sw : sw + bpp]

    return bytes(dst)


# ---------------------------------------------------------------------------
#  Whole-surface helpers (mip chains)
# ---------------------------------------------------------------------------


def swizzle_surface_mipmaps(
    width: int,
    height: int,
    blk_w: int,
    blk_h: int,
    bpp: int,
    mip_count: int,
    linear_mips: list[bytes],
    alignment: int = 512,
) -> tuple[bytes, list[int], int, int]:
    """Swizzle a full mip chain into one combined, mip-aligned BNTX blob.

    Mirrors Switch Toolbox's ``SwizzleSurfaceMipMaps`` with ``CombineMipLevel``:
    each mip is swizzled, then concatenated with the surface padded up to
    ``alignment`` before each mip.

    Returns ``(combined_bytes, mip_offsets, block_height_log2, image_size)``.
    """
    bh0 = block_height_mip0(div_round_up(height, blk_h))
    block_height_log2 = bh0.bit_length() - 1

    combined = bytearray()
    mip_offsets: list[int] = []
    surface_size = 0

    for mip in range(mip_count):
        pad = round_up(surface_size, alignment) - surface_size if alignment > 1 else 0
        combined.extend(b"\x00" * pad)
        surface_size += pad
        mip_offsets.append(surface_size)

        mw = max(1, width >> mip)
        mh = max(1, height >> mip)
        mhb = div_round_up(mh, blk_h)
        bh = mip_block_height(mhb, bh0)

        data = linear_mips[mip] if mip < len(linear_mips) else b""
        sw = swizzle_mip(mw, mh, blk_w, blk_h, bpp, bh, data)
        combined.extend(sw)
        surface_size += len(sw)

    return bytes(combined), mip_offsets, block_height_log2, surface_size


def swizzle_surface_per_mip(
    width: int,
    height: int,
    blk_w: int,
    blk_h: int,
    bpp: int,
    mip_count: int,
    linear_mips: list[bytes],
) -> tuple[list[bytes], int]:
    """Swizzle a mip chain into a list of separate surfaces (TXTG layout).

    TXTG stores each mip as its own (later zstd-compressed) surface rather than
    one combined blob. Returns ``(swizzled_surfaces, block_height_log2)``.
    """
    bh0 = block_height_mip0(div_round_up(height, blk_h))
    block_height_log2 = bh0.bit_length() - 1

    surfaces: list[bytes] = []
    for mip in range(mip_count):
        mw = max(1, width >> mip)
        mh = max(1, height >> mip)
        mhb = div_round_up(mh, blk_h)
        bh = mip_block_height(mhb, bh0)
        data = linear_mips[mip] if mip < len(linear_mips) else b""
        surfaces.append(swizzle_mip(mw, mh, blk_w, blk_h, bpp, bh, data))

    return surfaces, block_height_log2
