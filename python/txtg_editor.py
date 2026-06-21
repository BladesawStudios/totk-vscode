import struct

import zstandard as zstd
from txtg_reader import is_txtg


class TxtgImportError(Exception):
    """Raised when a DDS cannot be imported into a TXTG texture."""


class TxtgEditor:
    def __init__(self, data: bytes):
        if not is_txtg(data):
            raise ValueError("Not a valid TXTG file.")
        self._data = bytearray(data)

    @property
    def header_size(self) -> int:
        val = struct.unpack_from("<H", self._data, 0x00)[0]
        return val if val else 0x50

    @property
    def width(self) -> int:
        return struct.unpack_from("<H", self._data, 0x08)[0]

    @width.setter
    def width(self, value: int):
        struct.pack_into("<H", self._data, 0x08, value)

    @property
    def height(self) -> int:
        return struct.unpack_from("<H", self._data, 0x0A)[0]

    @height.setter
    def height(self, value: int):
        struct.pack_into("<H", self._data, 0x0A, value)

    @property
    def array_count(self) -> int:
        return max(struct.unpack_from("<H", self._data, 0x0C)[0], 1)

    @array_count.setter
    def array_count(self, value: int):
        struct.pack_into("<H", self._data, 0x0C, value)

    @property
    def mip_count(self) -> int:
        return max(self._data[0x0E], 1)

    @mip_count.setter
    def mip_count(self, value: int):
        self._data[0x0E] = value

    @property
    def comp_r(self) -> int:
        return self._data[0x18]

    @comp_r.setter
    def comp_r(self, value: int):
        self._data[0x18] = value

    @property
    def comp_g(self) -> int:
        return self._data[0x19]

    @comp_g.setter
    def comp_g(self, value: int):
        self._data[0x19] = value

    @property
    def comp_b(self) -> int:
        return self._data[0x1A]

    @comp_b.setter
    def comp_b(self, value: int):
        self._data[0x1A] = value

    @property
    def comp_a(self) -> int:
        return self._data[0x1B]

    @comp_a.setter
    def comp_a(self, value: int):
        self._data[0x1B] = value

    @property
    def format_id(self) -> int:
        return struct.unpack_from("<H", self._data, 0x3C)[0]

    @format_id.setter
    def format_id(self, value: int):
        struct.pack_into("<H", self._data, 0x3C, value)

    @property
    def texture_setting2(self) -> int:
        return struct.unpack_from("<I", self._data, 0x44)[0]

    @texture_setting2.setter
    def texture_setting2(self, value: int):
        struct.pack_into("<I", self._data, 0x44, value)

    @property
    def texture_setting4(self) -> int:
        return struct.unpack_from("<I", self._data, 0x4C)[0]

    @texture_setting4.setter
    def texture_setting4(self, value: int):
        struct.pack_into("<I", self._data, 0x4C, value)

    # -- surface table access -----------------------------------------------

    def _read_surface_tables(self, surface_count: int):
        """Return (index_values, extra_values) from the existing surface table."""
        cur = self.header_size
        index = [struct.unpack_from("<I", self._data, cur + i * 4)[0] for i in range(surface_count)]
        cur += surface_count * 4
        extra = [
            struct.unpack_from("<I", self._data, cur + i * 8 + 4)[0] for i in range(surface_count)
        ]
        return index, extra

    def replace_surfaces(
        self,
        raw_surfaces: list[bytes],
        index_values: list[int] | None = None,
        extra_values: list[int] | None = None,
    ):
        """Compress and write a new set of surfaces, preserving the table format.

        Each size-table slot is 8 bytes: a u32 compressed size followed by a u32
        flag field (constant ``6`` in retail files). The index table and that
        flag field are preserved from the original where available so the TXTG
        keeps loading in game.
        """
        header = bytes(self._data[: self.header_size])
        cctx = zstd.ZstdCompressor()

        surface_count = len(raw_surfaces)
        compressed_list = [cctx.compress(s) for s in raw_surfaces]

        if index_values is None:
            index_values = [(1 << 24) | (i << 16) for i in range(surface_count)]
        if extra_values is None:
            extra_values = [6] * surface_count

        index_bytes = bytearray()
        for i in range(surface_count):
            iv = index_values[i] if i < len(index_values) else ((1 << 24) | (i << 16))
            index_bytes.extend(struct.pack("<I", iv))

        size_bytes = bytearray()
        payload = bytearray()
        for i, comp in enumerate(compressed_list):
            ev = extra_values[i] if i < len(extra_values) else 6
            size_bytes.extend(struct.pack("<II", len(comp), ev))
            payload.extend(comp)

        self._data = bytearray(header) + index_bytes + size_bytes + payload

    def replace_image_data(self, raw_surfaces: list[bytes]):
        # Backwards-compatible entry point: preserve existing tables when the
        # surface count is unchanged.
        old_count = self.mip_count * self.array_count
        if old_count == len(raw_surfaces):
            index, extra = self._read_surface_tables(old_count)
            self.replace_surfaces(raw_surfaces, index, extra)
        else:
            self.replace_surfaces(raw_surfaces)

    # -- DDS import ----------------------------------------------------------

    def import_dds(self, dds_bytes: bytes):
        """Replace the TXTG's image data from a DDS file.

        For a same-format, same-size DDS the header is left untouched and only
        the (re-swizzled, re-compressed) surface payload is replaced. Importing a
        different compression format (for example BC7 in place of BC1) is allowed
        and updates the stored format id; the pixels are not converted, the DDS is
        assumed to already be in the format the user wants.
        """
        import dds_io
        import texture_swizzle as tsw

        cur_format = self.format_id
        cur_key = dds_io.txtg_format_to_key(cur_format)
        if cur_key is None:
            raise TxtgImportError(
                f"TXTG format 0x{cur_format:04X} is not supported for replacement."
            )

        dds = dds_io.parse_dds(dds_bytes)

        if dds.key == cur_key:
            new_format_id = cur_format
        else:
            new_format_id = dds_io.key_to_txtg_format(dds.key, dds.is_srgb)
            if new_format_id is None:
                raise TxtgImportError(
                    f"DDS format {dds.key.upper()} cannot be imported into a TXTG."
                )

        bpb, blk_w, blk_h = dds.block_info
        same_geometry = (
            dds.width == self.width
            and dds.height == self.height
            and dds.mip_count == self.mip_count
            and self.array_count == 1
        )

        surfaces, _bh_log2 = tsw.swizzle_surface_per_mip(
            dds.width, dds.height, blk_w, blk_h, bpb, dds.mip_count, dds.mips
        )

        if same_geometry:
            self.format_id = new_format_id
            index, extra = self._read_surface_tables(self.mip_count)
            self.replace_surfaces(surfaces, index, extra)
        else:
            self.width = dds.width
            self.height = dds.height
            self.mip_count = dds.mip_count
            self.array_count = 1
            self.format_id = new_format_id
            self.replace_surfaces(surfaces)

        return {
            "width": dds.width,
            "height": dds.height,
            "mipCount": dds.mip_count,
            "format": dds.key,
        }

    def to_bytes(self) -> bytes:
        return bytes(self._data)
