"""Encrypt/decrypt Nintendo BFTTF/BFOTF fonts (Switch NX, Wii U CAFE, Windows)."""

from __future__ import annotations

import re

_OPEN_FONT_MAGICS = (
    0x4F54544F,  # OTTO
    0x00010000,  # TTF
    0x774F4646,  # wOFF
    0x774F4632,  # wOF2
    0x74727565,  # true
    0x74746366,  # ttcf
)

# BFTTFutil -enc_nx uses key 1231165446 (magic 36 f8 1a 1e on disk).
_PLATFORM_KEYS: dict[str, tuple[bytes, int]] = {
    "nx": (b"\x36\xf8\x1a\x1e", 1231165446),
    "cafe": (b"\xf3\x68\xde\xc1", 2364726489),
    "win": (b"\xd9\x9b\x87\x1a", 2785117442),
}

_MAGIC_TO_PLATFORM = {magic: name for name, (magic, _) in _PLATFORM_KEYS.items()}


def is_open_font(data: bytes) -> bool:
    if len(data) < 4:
        return False
    magic = int.from_bytes(data[:4], byteorder="big")
    return magic in _OPEN_FONT_MAGICS


def is_encrypted_totk_font(data: bytes) -> bool:
    return len(data) >= 4 and data[:4] in _MAGIC_TO_PLATFORM


def detect_platform_from_encrypted(data: bytes) -> str:
    if len(data) < 4:
        return "nx"
    return _MAGIC_TO_PLATFORM.get(data[:4], "nx")


def _select_bfttf_key(first_chunk: int, base_key: int, file_size_val: int) -> int:
    derived_key = base_key ^ file_size_val
    if (first_chunk ^ derived_key) in _OPEN_FONT_MAGICS:
        return derived_key
    if (first_chunk ^ base_key) in _OPEN_FONT_MAGICS:
        return base_key

    key_val = base_key
    for possible_magic in _OPEN_FONT_MAGICS:
        if (first_chunk ^ possible_magic) ^ base_key < 0x0FFFFFFF:
            key_val = first_chunk ^ possible_magic
            break
    return key_val


def decrypt_bfttf(data: bytes) -> bytes:
    if len(data) <= 8:
        return data

    magic = data[:4]
    base_key = None
    for platform_magic, key in _PLATFORM_KEYS.values():
        if magic == platform_magic:
            base_key = key
            break
    if base_key is None:
        return data

    first_chunk = int.from_bytes(data[8:12], byteorder="big")
    file_size_val = len(data) - 8
    key_val = _select_bfttf_key(first_chunk, base_key, file_size_val)

    out = bytearray(len(data) - 8)
    key_bytes = key_val.to_bytes(4, byteorder="big")
    for i in range(8, len(data)):
        out[i - 8] = data[i] ^ key_bytes[i % 4]
    return bytes(out)


def encrypt_bfttf(data: bytes, platform: str = "nx") -> bytes:
    if platform not in _PLATFORM_KEYS:
        raise ValueError(f"Unsupported BFTTF platform: {platform}")

    plain = decrypt_bfttf(data) if is_encrypted_totk_font(data) else data
    if not is_open_font(plain):
        raise ValueError("Input is not a valid TTF/OTF font")

    magic, base_key = _PLATFORM_KEYS[platform]
    file_size_val = len(plain)
    derived_key = base_key ^ file_size_val

    key_candidates = [derived_key, base_key]
    if len(plain) >= 4:
        plain_magic = int.from_bytes(plain[:4], byteorder="big")
        for possible_magic in _OPEN_FONT_MAGICS:
            key_candidates.append(plain_magic ^ possible_magic)

    tried: set[int] = set()
    for key_val in key_candidates:
        if key_val in tried:
            continue
        tried.add(key_val)

        key_bytes = key_val.to_bytes(4, byteorder="big")
        enc = bytes(plain[i] ^ key_bytes[i % 4] for i in range(len(plain)))
        if len(enc) < 4:
            continue

        first_chunk = int.from_bytes(enc[:4], byteorder="big")
        if _select_bfttf_key(first_chunk, base_key, file_size_val) != key_val:
            continue

        decrypted = decrypt_bfttf(magic + b"\x00\x00\x00\x00" + enc)
        if decrypted != plain:
            continue
        return magic + b"\x00\x00\x00\x00" + enc

    raise ValueError("Could not encrypt font")


def prepare_font_replacement(
    data: bytes,
    import_path: str,
    target_path: str,
    target_existing: bytes | None = None,
) -> bytes:
    """Normalize an imported font to the bytes that should be written to target_path."""
    if is_encrypted_totk_font(data):
        return data

    plain = decrypt_bfttf(data)
    if not is_open_font(plain):
        raise ValueError(f"Not a valid font file: {import_path}")

    target_lower = target_path.replace("\\", "/").lower()
    if re.search(r"\.(?:bfotf|bfttf)(?:\.zs)?$", target_lower):
        platform = "nx"
        if target_existing and is_encrypted_totk_font(target_existing):
            platform = detect_platform_from_encrypted(target_existing)
        return encrypt_bfttf(plain, platform)

    return plain
