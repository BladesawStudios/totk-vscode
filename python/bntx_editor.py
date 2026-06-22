import struct

from bntx_reader import _read_bntx_string, is_bntx


class BntxImportError(Exception):
    """Raised when a DDS cannot be imported into a BNTX texture."""


class BntxEditor:
    # BRTI field offsets, relative to (brti_abs + 0x10).
    _OFF_TILE_MODE = 0x02
    _OFF_SWIZZLE = 0x04
    _OFF_MIP_COUNT = 0x06
    _OFF_FORMAT = 0x0C
    _OFF_WIDTH = 0x14
    _OFF_HEIGHT = 0x18
    _OFF_LAYOUT = 0x24
    _OFF_IMAGE_SIZE = 0x40
    _OFF_ALIGNMENT = 0x44
    _OFF_CH_R = 0x48
    _OFF_NAME_PTR = 0x50
    _OFF_PATH_PTR = 0x58
    _OFF_PTRS = 0x60

    def __init__(self, data: bytes):
        if not is_bntx(data):
            raise ValueError("Not a valid BNTX file.")
        self._data = bytearray(data)

        self.bom = self._data[0x0C:0x0E]
        self.le = self.bom == b"\xff\xfe"
        self.endian_fmt = "<" if self.le else ">"

    def _read_fmt(self, fmt: str, offset: int):
        return struct.unpack_from(self.endian_fmt + fmt, self._data, offset)[0]

    def _write_fmt(self, fmt: str, offset: int, value):
        struct.pack_into(self.endian_fmt + fmt, self._data, offset, value)

    @property
    def tex_count(self) -> int:
        return self._read_fmt("i", 0x24)

    @tex_count.setter
    def tex_count(self, value: int):
        self._write_fmt("i", 0x24, value)

    @property
    def info_ptrs_addr(self) -> int:
        return self._read_fmt("q", 0x28)

    @property
    def file_size(self) -> int:
        return self._read_fmt("I", 0x1C)

    @file_size.setter
    def file_size(self, value: int):
        self._write_fmt("I", 0x1C, value)

    def get_texture_ptrs(self) -> list[int]:
        ptrs = []
        addr = self.info_ptrs_addr
        for _ in range(self.tex_count):
            ptrs.append(self._read_fmt("q", addr))
            addr += 8
        return ptrs

    def find_texture_brti(self, name: str) -> int:
        for ptr in self.get_texture_ptrs():
            if ptr <= 0 or ptr + 0x70 > len(self._data):
                continue
            name_addr = self._read_fmt("q", ptr + 0x10 + self._OFF_NAME_PTR)
            if 0 < name_addr < len(self._data):
                tex_name = _read_bntx_string(self._data, name_addr, self.le)
                if tex_name == name:
                    return ptr
        return -1

    # -- low level field access for a located BRTI ---------------------------

    def _data_base(self, d: int) -> int:
        """Absolute file offset of mip 0's image data."""
        ptrs_addr = self._read_fmt("q", d + self._OFF_PTRS)
        if not (0 < ptrs_addr < len(self._data)):
            raise BntxImportError("Texture has no image-data pointer.")
        return self._read_fmt("q", ptrs_addr)

    def _mip_ptr_array_addr(self, d: int) -> int:
        return self._read_fmt("q", d + self._OFF_PTRS)

    # -- metadata edit (unchanged behaviour) ---------------------------------

    def update_metadata(self, name: str, metadata: dict):
        ptr = self.find_texture_brti(name)
        if ptr < 0:
            raise ValueError(f"Texture {name} not found.")

        d = ptr + 0x10
        channels = ["Zero", "One", "Red", "Green", "Blue", "Alpha"]
        ch_map = {c: i for i, c in enumerate(channels)}

        if "red" in metadata and metadata["red"] in ch_map:
            self._data[d + self._OFF_CH_R + 0] = ch_map[metadata["red"]]
        if "green" in metadata and metadata["green"] in ch_map:
            self._data[d + self._OFF_CH_R + 1] = ch_map[metadata["green"]]
        if "blue" in metadata and metadata["blue"] in ch_map:
            self._data[d + self._OFF_CH_R + 2] = ch_map[metadata["blue"]]
        if "alpha" in metadata and metadata["alpha"] in ch_map:
            self._data[d + self._OFF_CH_R + 3] = ch_map[metadata["alpha"]]

        if metadata.get("swizzle") is not None:
            self._write_fmt("H", d + self._OFF_SWIZZLE, int(metadata["swizzle"]))

        new_name = metadata.get("name", name)
        if new_name is not None and new_name != name:
            self.rename_texture(name, new_name)
            name = new_name

        if "path" in metadata and metadata["path"] is not None:
            new_path = metadata["path"]
            ptr = self.find_texture_brti(name)
            new_path_bytes = (
                struct.pack(self.endian_fmt + "H", len(new_path))
                + new_path.encode("utf-8")
                + b"\x00"
            )
            new_addr = len(self._data)
            self._data.extend(new_path_bytes)
            self._write_fmt("q", ptr + 0x10 + self._OFF_PATH_PTR, new_addr)
            self.file_size = len(self._data)

        if "useSRGB" in metadata and metadata["useSRGB"] is not None:
            self._set_srgb(ptr + 0x10, bool(metadata["useSRGB"]))

    def _set_srgb(self, d: int, use_srgb: bool):
        format_id = self._read_fmt("I", d + self._OFF_FORMAT)
        variant = format_id & 0xFF
        if use_srgb and variant != 0x06:
            self._write_fmt("I", d + self._OFF_FORMAT, (format_id & 0xFFFFFF00) | 0x06)
        elif not use_srgb and variant == 0x06:
            self._write_fmt("I", d + self._OFF_FORMAT, (format_id & 0xFFFFFF00) | 0x01)

    def rename_texture(self, old_name: str, new_name: str):
        raise NotImplementedError("Texture renaming is temporarily disabled!")

    # -- DDS import ----------------------------------------------------------

    def import_dds(self, name: str, dds_bytes: bytes):
        """Replace a texture's image data from a DDS file.

        The DDS must match the target texture's compression format family.
        Dimensions and mip count may differ from the original; larger imports
        relocate image data to the end of the file.
        """
        import dds_io
        import texture_swizzle as tsw

        ptr = self.find_texture_brti(name)
        if ptr < 0:
            raise BntxImportError(f"Texture {name!r} not found in BNTX.")
        d = ptr + 0x10

        cur_format = self._read_fmt("I", d + self._OFF_FORMAT)
        cur_image_size = self._read_fmt("I", d + self._OFF_IMAGE_SIZE)
        cur_alignment = self._read_fmt("I", d + self._OFF_ALIGNMENT) or 512
        tile_mode = self._read_fmt("H", d + self._OFF_TILE_MODE)

        cur_key = dds_io.bntx_format_to_key(cur_format)
        if cur_key is None:
            raise BntxImportError(
                f"Texture format 0x{cur_format:04X} is not supported for replacement."
            )
        if tile_mode == 1:
            raise BntxImportError("Linear (pitch) textures cannot be replaced yet.")

        dds = dds_io.parse_dds(dds_bytes)

        # Small uncompressed formats are exported as editable RGBA8, so collapse
        # an RGBA8 DDS back to the texture's native layout before writing.
        if cur_key in dds_io._SMALL_RGBA8_EXPAND and dds.key == "rgba8":
            native = [
                dds_io.collapse_rgba8(
                    cur_key, m, max(1, dds.width >> i) * max(1, dds.height >> i)
                )
                for i, m in enumerate(dds.mips)
            ]
            dds = dds_io.DdsImage(
                dds.width, dds.height, len(native), cur_key, False, False, native
            )

        if dds.key != cur_key:
            raise BntxImportError(
                f"Format mismatch: texture is {cur_key.upper()} but DDS is {dds.key.upper()}. "
                f"Re-export the DDS as {cur_key.upper()}."
            )

        bpb, blk_w, blk_h = dds.block_info

        max_mips = dds.mip_count
        combined, offsets, bh_log2, image_size = tsw.swizzle_surface_mipmaps(
            dds.width, dds.height, blk_w, blk_h, bpb, max_mips, dds.mips[:max_mips], cur_alignment
        )

        data_base = self._data_base(d)
        if data_base <= 0 or data_base + cur_image_size > len(self._data):
            raise BntxImportError("Texture image data is outside the file bounds.")

        ptr_array = self._mip_ptr_array_addr(d)

        if len(combined) <= cur_image_size:
            # Overwrite in place and zero any trailing slack so stale bytes do not leak.
            self._data[data_base : data_base + len(combined)] = combined
            if len(combined) < cur_image_size:
                self._data[data_base + len(combined) : data_base + cur_image_size] = b"\x00" * (
                    cur_image_size - len(combined)
                )
            write_base = data_base
            stored_image_size = image_size
        else:
            # Larger mip chain: relocate image data to the end of the file.
            align = cur_alignment or 512
            write_base = len(self._data)
            pad = (align - (write_base % align)) % align
            if pad:
                self._data.extend(b"\x00" * pad)
                write_base = len(self._data)
            self._data.extend(combined)
            if image_size > len(combined):
                self._data.extend(b"\x00" * (image_size - len(combined)))
            stored_image_size = image_size
            self.file_size = len(self._data)

        for i in range(max_mips):
            self._write_fmt("q", ptr_array + i * 8, write_base + offsets[i])

        self._write_fmt("i", d + self._OFF_WIDTH, dds.width)
        self._write_fmt("i", d + self._OFF_HEIGHT, dds.height)
        self._write_fmt("H", d + self._OFF_MIP_COUNT, max_mips)
        self._write_fmt("I", d + self._OFF_IMAGE_SIZE, stored_image_size)

        # Preserve the existing block-height layout bits, refresh the low 3 bits.
        layout = self._read_fmt("I", d + self._OFF_LAYOUT)
        self._write_fmt("I", d + self._OFF_LAYOUT, (layout & ~0x7) | (bh_log2 & 0x7))

        # Keep the sRGB/SNORM variant in sync with the DDS.
        if dds.is_srgb:
            self._set_srgb(d, True)
        elif (cur_format & 0xFF) == 0x06 and not dds.is_srgb:
            self._set_srgb(d, False)

        return {"width": dds.width, "height": dds.height, "mipCount": max_mips, "format": cur_key}

    def to_bytes(self) -> bytes:
        return bytes(self._data)
