"""Load format handler manifest written by TKVSC TypeScript (Phase 2 addon registry)."""

from __future__ import annotations

import importlib.util
import json
import os
from functools import lru_cache
from pathlib import Path
from types import ModuleType

_BUILTIN_KINDS = frozenset({"byml", "msbt", "aamp", "asb", "baev", "xlnk"})

_EMPTY_MANIFEST: dict = {
    "version": 1,
    "extensionToHandler": {},
    "aampExtensions": [],
    "handlers": {},
}


def _manifest_path() -> str:
    return os.environ.get("TKVSC_HANDLER_MANIFEST", "").strip()


@lru_cache(maxsize=1)
def load_handler_manifest() -> dict:
    path = _manifest_path()
    if not path or not os.path.isfile(path):
        return dict(_EMPTY_MANIFEST)
    try:
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
        if isinstance(data, dict):
            return data
    except (OSError, json.JSONDecodeError):
        pass
    return dict(_EMPTY_MANIFEST)


def clear_handler_manifest_cache() -> None:
    load_handler_manifest.cache_clear()
    _load_addon_module.cache_clear()


def file_extension(logical_path: str) -> str:
    lower = logical_path.lower().replace("\\", "/")
    if lower.endswith(".zs"):
        lower = lower[:-3]
    if "." not in lower:
        return ""
    return lower.rsplit(".", 1)[-1]


def extension_to_handler_kind(logical_path: str) -> str | None:
    manifest = load_handler_manifest()
    ext = file_extension(logical_path)
    if not ext:
        return None
    aamp_exts = manifest.get("aampExtensions") or []
    if ext in aamp_exts:
        return "aamp"
    mapping = manifest.get("extensionToHandler") or {}
    return mapping.get(ext)


def is_addon_handler_kind(kind: str | None) -> bool:
    if not kind or kind in _BUILTIN_KINDS:
        return False
    handlers = load_handler_manifest().get("handlers") or {}
    return kind in handlers


@lru_cache(maxsize=32)
def _load_addon_module(module_path: str) -> ModuleType:
    path = Path(module_path)
    if not path.is_file():
        raise FileNotFoundError(f"Addon handler module not found: {module_path}")
    spec = importlib.util.spec_from_file_location(f"tkvsc_addon_{path.stem}", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Cannot load addon handler module: {module_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _handler_entry(kind: str) -> dict:
    handlers = load_handler_manifest().get("handlers") or {}
    entry = handlers.get(kind)
    if not isinstance(entry, dict):
        raise ValueError(f"No addon handler registered for kind: {kind}")
    return entry


def read_addon_content(
    kind: str,
    file_data: bytes,
    logical_path: str,
    romfs_path: str = "",
) -> str:
    entry = _handler_entry(kind)
    module = _load_addon_module(entry["modulePath"])
    fn_name = entry.get("readFunction") or "read_content"
    fn = getattr(module, fn_name, None)
    if not callable(fn):
        raise ValueError(f"Addon handler {kind} missing {fn_name}()")
    return fn(file_data, logical_path, romfs_path)


def write_addon_bytes(
    kind: str,
    original: bytes,
    editor_text: str,
    logical_path: str,
    romfs_path: str = "",
) -> bytes:
    entry = _handler_entry(kind)
    module = _load_addon_module(entry["modulePath"])
    fn_name = entry.get("writeFunction") or "write_content"
    fn = getattr(module, fn_name, None)
    if not callable(fn):
        raise ValueError(f"Addon handler {kind} missing {fn_name}()")
    result = fn(original, editor_text, logical_path, romfs_path)
    if isinstance(result, bytes):
        return result
    raise TypeError(f"Addon handler {kind}.{fn_name}() must return bytes")
