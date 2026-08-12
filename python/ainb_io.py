"""Read/write AINB using the vendored TKVSC-Team/AINB toolkit.

The editor representation is the library's own dictionary form (``AINB.as_dict``),
serialized as JSON. That keeps the node graph editor and the plain-text JSON editor
working off exactly the same document, and lets the library own index remapping when
nodes are added or removed.
"""

import json
from pathlib import Path

_TOTK_VERSION = 0x407


def _ensure_ainb_toolkit_on_path() -> None:
    from vendor_sys import add_vendor_to_path

    add_vendor_to_path("ainb")


def _load_ainb_module():
    _ensure_ainb_toolkit_on_path()
    import ainb

    # Selects the TotK enum database used to resolve enum-valued parameters.
    ainb.set_tears_of_the_kingdom()
    return ainb


def _stem_from_path(logical_path: str) -> str:
    name = Path(logical_path.replace("\\", "/")).name
    if name.endswith(".zs"):
        name = name[:-3]
    if name.endswith(".ainb"):
        name = name[:-5]
    return name


def _decompress_bytes(data: bytes, logical_path: str, romfs_path: str) -> bytes:
    from zstd_totk import decompress_container

    payload, _, _ = decompress_container(data, logical_path, romfs_path)
    return payload


def _compress_bytes(data: bytes, logical_path: str, romfs_path: str, was_compressed: bool) -> bytes:
    if not was_compressed:
        return data

    from zstd_totk import compress_container

    logical = logical_path
    if not logical.endswith(".zs"):
        logical = logical_path + ".zs"
    return compress_container(data, logical, romfs_path, was_zstd=True, was_yaz0=False)


def _to_editor_text(file_data: bytes, logical_path: str, romfs_path: str) -> str:
    ainb = _load_ainb_module()

    data = _decompress_bytes(file_data, logical_path, romfs_path)
    ainb_file = ainb.AINB.from_binary(data)
    return json.dumps(ainb_file.as_dict(), indent=2, ensure_ascii=False) + "\n"


def _to_binary(editor_text: str, logical_path: str) -> bytes:
    ainb = _load_ainb_module()

    data = json.loads(editor_text)
    # The filename is baked into the file and must track the path it is saved to,
    # otherwise the game looks up a module that no longer resolves.
    ainb_file = ainb.AINB.from_dict(data, override_filename=_stem_from_path(logical_path))
    return ainb_file.to_binary()


def read_ainb_content(file_data: bytes, logical_path: str, romfs_path: str = "") -> str:
    return _to_editor_text(file_data, logical_path, romfs_path)


def read_ainb_content_disk(file_path: str, romfs_path: str = "") -> str:
    return _to_editor_text(Path(file_path).read_bytes(), file_path, romfs_path)


def write_ainb_bytes(
    original: bytes,
    editor_text: str,
    logical_path: str,
    romfs_path: str = "",
) -> bytes:
    was_zstd = original.startswith(b"\x28\xb5\x2f\xfd")
    new_bytes = _to_binary(editor_text, logical_path)
    return _compress_bytes(new_bytes, logical_path, romfs_path, was_zstd)


def write_ainb_disk(file_path: str, editor_text: str, romfs_path: str = "") -> None:
    original = Path(file_path).read_bytes() if Path(file_path).is_file() else b""
    Path(file_path).write_bytes(write_ainb_bytes(original, editor_text, file_path, romfs_path))


def get_supported_versions() -> list:
    ainb = _load_ainb_module()
    return list(ainb.get_supported_versions())


def new_ainb_text(filename: str, category: str = "Logic") -> str:
    """Editor text for a brand new, empty AINB file."""
    return (
        json.dumps(
            {
                "Version": _TOTK_VERSION,
                "Filename": filename,
                "Category": category,
                "Blackboard ID": 0,
                "Parent Blackboard ID": 0,
                "Commands": [],
                "Nodes": [],
                "Blackboard": {},
                "Expressions": {},
                "Replacement Table": [],
                "Modules": [],
                "Unknown Section 0x58": {},
                "Has Section 0x6C": False,
            },
            indent=2,
            ensure_ascii=False,
        )
        + "\n"
    )
