"""Decode Nintendo BWAV (.bwav) audio files to WAV via vgmstream-cli."""

import base64
import os
import subprocess
import sys
import tempfile
from pathlib import Path

from zstd_totk import decompress_container

_BWAV_MAGIC = b"BWAV"
_ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"
_SCRIPT_DIR = Path(__file__).resolve().parent


def is_bwav_extension(logical_path: str) -> bool:
    lower = logical_path.lower().replace("\\", "/")
    if lower.endswith(".zs"):
        lower = lower[:-3]
    return lower.endswith(".bwav")


def is_bwav_binary(file_data: bytes) -> bool:
    if len(file_data) >= 4 and file_data[:4] == _BWAV_MAGIC:
        return True
    try:
        data, _, _ = decompress_container(file_data, "", "")
    except ValueError:
        data = file_data
    return len(data) >= 4 and data[:4] == _BWAV_MAGIC


def _platform_subdir_and_name() -> tuple[str, str]:
    if os.name == "nt":
        return ("windows", "vgmstream-cli.exe")
    if sys.platform == "darwin":
        return ("osx", "vgmstream-cli")
    return ("linux", "vgmstream-cli")


def find_vgmstream_cli() -> str:
    override = os.environ.get("TOTK_VGMSTREAM_CLI", "").strip()
    if override:
        if os.path.isfile(override):
            return override
        raise FileNotFoundError(f"TOTK_VGMSTREAM_CLI is not a file: {override}")

    subdir, name = _platform_subdir_and_name()
    search_dirs = [
        _SCRIPT_DIR / "vendor" / "vgmstream" / subdir,
        _SCRIPT_DIR.parent / "vendor" / "vgmstream" / subdir,
    ]
    for d in search_dirs:
        p = d / name
        if p.is_file():
            p.chmod(p.stat().st_mode | 0o111)
            return str(p)

    raise FileNotFoundError(
        f"vgmstream-cli not found. Download a release from github.com/vgmstream/vgmstream "
        f"and set TOTK_VGMSTREAM_CLI, or place {name} in vendor/vgmstream/{subdir}/."
    )


def _decode_bwav_to_wav(tool: str, bwav_data: bytes) -> bytes:
    """Run vgmstream-cli on raw BWAV bytes, return WAV bytes."""
    with tempfile.TemporaryDirectory(prefix="totk-bwav-") as tmp:
        tmp_path = Path(tmp)
        # vgmstream uses the extension to identify the format
        inp = tmp_path / "input.bwav"
        out = tmp_path / "output.wav"
        inp.write_bytes(bwav_data)
        result = subprocess.run(
            [tool, "-o", str(out), str(inp)],
            capture_output=True,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or b"").decode(errors="replace").strip()
            raise RuntimeError(f"vgmstream-cli failed: {detail or f'exit {result.returncode}'}")
        return out.read_bytes()


def read_bwav_as_base64_wav(
    file_data: bytes,
    logical_path: str = "",
    romfs_path: str = "",
) -> str:
    """Decompress if needed, decode BWAV to WAV, return base64-encoded WAV string."""
    data, _, _ = decompress_container(file_data, logical_path, romfs_path)
    if not data.startswith(_BWAV_MAGIC):
        raise ValueError(f"Not a BWAV file (got magic {data[:4]!r})")
    tool = find_vgmstream_cli()
    wav_bytes = _decode_bwav_to_wav(tool, data)
    return base64.b64encode(wav_bytes).decode("ascii")