"""Read/write Nintendo XLNK (.belnk / .bslnk) via dt-12345/xlink2 `xlink`."""

import os
import subprocess
import sys
import tempfile
from pathlib import Path

from zstd_totk import compress_container, decompress_container

_ZSTD_MAGIC = b"\x28\xb5\x2f\xfd"
_YAZ0_MAGIC = b"Yaz0"

# `xlink` needs the game/platform to pick the right database version. TotK is
# EXKing on NX; other games can be selected with the env overrides in _game_args.
_GAME_BY_ID = {
    "totk": "EXKing",
}
_PLATFORM_BY_ID = {
    "totk": "NX",
}


def is_xlnk_extension(logical_path: str) -> bool:
    lower = logical_path.lower().replace("\\", "/")
    if lower.endswith(".zs"):
        lower = lower[:-3]
    return lower.endswith(".belnk") or lower.endswith(".bslnk")


def is_xlnk_binary(file_data: bytes) -> bool:
    if len(file_data) >= 4 and file_data[:4] == b"XLNK":
        return True
    try:
        data, _, _ = decompress_container(file_data, "", "")
    except ValueError:
        data = file_data
    return len(data) >= 4 and data[:4] == b"XLNK"


def _platform_tool_names() -> list[str]:
    """Return candidate binary names for the current platform, most specific first."""
    if os.name == "nt":
        return ["xlink.exe"]
    if sys.platform == "darwin":
        return ["xlink_osx", "xlink"]  # No macOS release build; must be built from source
    return ["xlink_linux", "xlink"]  # Assume linux if Windows and macOS not detected


def find_xlink_tool() -> str:
    override = os.environ.get("TOTK_XLINK_TOOL", "").strip()
    if override:
        if os.path.isfile(override):
            return override
        raise FileNotFoundError(f"TOTK_XLINK_TOOL is not a file: {override}")

    candidates = _platform_tool_names()
    from vendor_sys import get_vendor_path

    vendor_dir = get_vendor_path("xlink2")
    if vendor_dir:
        for name in candidates:
            p = vendor_dir / name
            if p.is_file():
                p.chmod(p.stat().st_mode | 0o111)
                return str(p)

    hint = ""
    if sys.platform == "darwin":
        hint = (
            " dt-12345/xlink2 does not publish a macOS build; build it from source and place "
            "the binary at vendor/xlink2/xlink_osx."
        )
    raise FileNotFoundError(
        "xlink not found. Install dt-12345/xlink2 and set TOTK_XLINK_TOOL, "
        f"or place one of {candidates} in vendor/xlink2/.{hint}"
    )


def _game_args() -> list[str]:
    game_id = (os.environ.get("TKVSC_GAME_ID", "") or "totk").strip().lower()
    game = os.environ.get("TOTK_XLINK_GAME", "").strip() or _GAME_BY_ID.get(game_id, "EXKing")
    platform = os.environ.get("TOTK_XLINK_PLATFORM", "").strip() or _PLATFORM_BY_ID.get(
        game_id, "NX"
    )
    return ["-g", game, "-p", platform]


def _run_xlink(tool: str, args: list[str], action: str) -> None:
    result = subprocess.run(
        [tool, *args], capture_output=True, text=True, encoding="utf-8", errors="replace"
    )
    if result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip() or f"exit {result.returncode}"
        raise RuntimeError(f"xlink {action} failed: {detail}")


def _run_xlink_export(tool: str, input_path: str, output_text: str) -> None:
    _run_xlink(
        tool,
        ["-i", input_path, "-o", output_text, "-it", "binary", "-ot", "text", *_game_args()],
        "export",
    )


def _run_xlink_import(tool: str, input_text: str, output_path: str) -> None:
    _run_xlink(
        tool,
        ["-i", input_text, "-o", output_path, "-it", "text", "-ot", "binary", *_game_args()],
        "import",
    )


def read_xlnk_content(file_data: bytes, logical_path: str = "", romfs_path: str = "") -> str:
    tool = find_xlink_tool()
    data, _, _ = decompress_container(file_data, logical_path, romfs_path)

    with tempfile.TemporaryDirectory(prefix="totk-xlnk-") as tmp:
        tmp_path = Path(tmp)
        inp = tmp_path / "input.bin"
        out_text = tmp_path / "output.txt"
        inp.write_bytes(data)
        _run_xlink_export(tool, str(inp), str(out_text))
        return out_text.read_text(encoding="utf-8")


def write_xlnk_bytes(
    orig_file_data: bytes,
    editor_text: str,
    logical_path: str = "",
    romfs_path: str = "",
) -> bytes:
    tool = find_xlink_tool()
    # Detect the original container from the magic alone -- decompressing just to
    # learn how to recompress would need the romfs dictionaries for no reason.
    was_yaz0 = orig_file_data.startswith(_YAZ0_MAGIC)
    was_zstd = not was_yaz0 and (
        orig_file_data.startswith(_ZSTD_MAGIC) or logical_path.lower().endswith(".zs")
    )

    with tempfile.TemporaryDirectory(prefix="totk-xlnk-") as tmp:
        tmp_path = Path(tmp)
        text_path = tmp_path / "input.txt"
        out_path = tmp_path / "output.bin"
        # newline="" so CRLF editor text is not translated into CRCRLF on Windows
        text_path.write_text(editor_text, encoding="utf-8", newline="")
        _run_xlink_import(tool, str(text_path), str(out_path))
        new_bytes = out_path.read_bytes()

    return compress_container(new_bytes, logical_path, romfs_path, was_zstd, was_yaz0)
