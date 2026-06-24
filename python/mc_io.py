"""Mesh Codec (.mc / MCPK) decompression for TOTK .bfres.mc files.

Uses the vendored mc_decompress binary when available, with a Python ZSTD
magicless fallback matching Switch Toolbox's MeshCodec implementation.
"""

from __future__ import annotations

import os
import platform
import struct
import subprocess
import tempfile
from pathlib import Path

import zstandard as zstd

_MCPK_MAGIC = 0x4B50434D  # "MCPK"
_SCRIPT_DIR = Path(__file__).resolve().parent


def is_mcpk(data: bytes) -> bool:
    return len(data) >= 4 and struct.unpack_from("<I", data, 0)[0] == _MCPK_MAGIC


def _mc_decompressed_size(flags: int) -> int:
    return (flags >> 5) << (flags & 0xF)


def _mesh_codec_flags(decompressed_size: int) -> int:
    aligned = (-decompressed_size % 0x1000 + 0x1000) % 0x1000
    padded = decompressed_size + aligned
    return ((padded >> 0xC) << 5) + 0xC


def _decompress_mcpk_python(data: bytes) -> bytes:
    if not is_mcpk(data):
        raise ValueError("Not an MCPK mesh codec package")
    flags = struct.unpack_from("<I", data, 8)[0]
    out_size = _mc_decompressed_size(flags)
    payload = data[0xC:]
    dctx = zstd.ZstdDecompressor(format=zstd.FORMAT_ZSTD1_MAGICLESS)
    return dctx.decompress(payload, max_output_size=out_size)


def _compress_mcpk_python(data: bytes) -> bytes:
    compressed = zstd.ZstdCompressor(level=20).compress(data)
    if compressed.startswith(b"\x28\xb5\x2f\xfd"):
        compressed = compressed[4:]
    flags = _mesh_codec_flags(len(data))
    return struct.pack("<IBBBBI", _MCPK_MAGIC, 1, 1, 0, 0, flags) + compressed


def _mc_tool_candidates() -> list[str]:
    system = platform.system()
    if system == "Windows":
        return ["mc_decompress.exe"]
    if system == "Darwin":
        return ["mc_decompress_osx", "mc_decompress"]
    return ["mc_decompress_linux", "mc_decompress"]


def find_mc_decompress() -> str | None:
    env = os.environ.get("TOTK_MC_DECOMPRESS", "").strip()
    if env and Path(env).is_file():
        return env

    from vendor_sys import get_vendor_path

    vendor_dir = get_vendor_path("mcdecompress")
    if vendor_dir:
        for name in _mc_tool_candidates():
            candidate = vendor_dir / name
            if candidate.is_file():
                return str(candidate)

    mesh_codec_build = _SCRIPT_DIR.parent.parent / "MeshCodec" / "build" / "tests" / "mc_decompress.exe"
    if mesh_codec_build.is_file():
        return str(mesh_codec_build)
    return None


def _decompress_mcpk_tool(data: bytes) -> bytes:
    tool = find_mc_decompress()
    if not tool:
        raise RuntimeError("mc_decompress tool not found")

    with tempfile.TemporaryDirectory(prefix="tkvsc-mc-") as tmp:
        tmp_path = Path(tmp)
        input_path = tmp_path / "input.mc"
        output_dir = tmp_path / "out"
        input_path.write_bytes(data)
        result = subprocess.run(
            [tool, str(input_path), str(output_dir)],
            capture_output=True,
            text=True,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "").strip()
            raise RuntimeError(f"mc_decompress failed: {detail or result.returncode}")

        outputs = list(output_dir.iterdir())
        if not outputs:
            raise RuntimeError("mc_decompress produced no output")
        return outputs[0].read_bytes()


def decompress_mc_bytes(data: bytes) -> bytes:
    if not is_mcpk(data):
        raise ValueError("Not an MCPK mesh codec package")
    try:
        return _decompress_mcpk_tool(data)
    except Exception:
        return _decompress_mcpk_python(data)


def compress_mc_bytes(data: bytes) -> bytes:
    return _compress_mcpk_python(data)
