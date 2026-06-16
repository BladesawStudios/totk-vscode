"""Parse Nintendo BARS (.bars) archives and decode audio via bwav_io."""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from pathlib import Path

from bwav_io import read_bwav_as_base64_wav, read_bwav_to_temp_wav
from zstd_totk import decompress_container

_BARS_MAGIC = b"BARS"
_AMTA_MAGIC = b"AMTA"

# Romfs paths to search for full BWAVs, relative to romfs root
_BWAV_SEARCH_PATHS = [
    "Sound/Resource/Stream/{name}.bwav",
    "Sound/Resource/{name}.bwav",
]


# ---------------------------------------------------------------------------
# Data classes
# ---------------------------------------------------------------------------

@dataclass
class AmtaMarker:
    id: int
    name: str
    start: int
    length: int


@dataclass
class AmtaStreamTrack:
    channel_count: int
    volume: float


@dataclass
class AmtaMetadata:
    name: str
    audio_type: str             # "Wave" or "Stream"
    sample_rate: int
    channel_count: int
    num_samples: int
    loop_start: int
    is_looped: bool
    volume_db: float            # raw dB value from file
    volume_linear: float        # 10 ** (volume_db / 20)
    stream_tracks: list[AmtaStreamTrack]
    markers: list[AmtaMarker]
    amplitude_peak: float | None  # v4.0+ only


@dataclass
class BarsEntry:
    name: str
    name_hash: int
    amta_offset: int    # absolute offset of AMTA block in BARS data
    bwav_offset: int    # absolute offset of prefetch BWAV, or -1 if absent
    metadata: AmtaMetadata | None = field(default=None)


@dataclass
class BarsFile:
    entries: list[BarsEntry]
    data: bytes         # raw (decompressed) BARS bytes


# ---------------------------------------------------------------------------
# Primitive readers
# ---------------------------------------------------------------------------

def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def _i32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<i", data, offset)[0]


def _u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def _f32(data: bytes, offset: int) -> float:
    return struct.unpack_from("<f", data, offset)[0]


def _cstring(data: bytes, offset: int) -> str:
    end = data.index(b"\x00", offset)
    return data[offset:end].decode("utf-8", errors="replace")


# ---------------------------------------------------------------------------
# AMTA parsing
# ---------------------------------------------------------------------------

def _parse_amta(data: bytes, amta_offset: int) -> AmtaMetadata:
    """Parse a full AMTA block and return structured metadata."""
    if data[amta_offset:amta_offset + 4] != _AMTA_MAGIC:
        raise ValueError(f"Expected AMTA magic at offset {amta_offset:#x}")

    # AMTA header:
    #   0x0  4  "AMTA"
    #   0x4  2  BOM (0xFEFF)
    #   0x6  2  version (major << 8 | minor), e.g. 0x0500 = v5.0
    #   0x8  4  filesize
    #   0xC  4  offset to DATA section (relative to amta_offset)
    #   0x10 4  offset to MARK section
    #   0x14 4  offset to EXT section (v3+) OR STRG section (v1)
    #   0x18 4  offset to STRG section (v3+ only)

    version = _u16(data, amta_offset + 0x6)
    major = (version >> 8) & 0xFF

    if major >= 5:
        data_rel = _u32(data, amta_offset + 0x10)
        mark_rel = _u32(data, amta_offset + 0x14)
        strg_rel = _u32(data, amta_offset + 0x18)
        
        data_abs = amta_offset + data_rel if data_rel else None
        mark_abs = amta_offset + mark_rel if mark_rel else None

        markers: list[AmtaMarker] = []
        name = ""
        
        if mark_abs is not None:
            marker_count = _u32(data, mark_abs)
            
            for i in range(marker_count):
                m_off = mark_abs + 4 + i * 16
                m_name_off = _u32(data, m_off + 4)
                markers.append(AmtaMarker(
                    id=_u32(data, m_off + 0),
                    name=_cstring(data, m_off + 4 + m_name_off),
                    start=_u32(data, m_off + 8),
                    length=_u32(data, m_off + 12),
                ))

        # In V5, the asset name immediately follows the DATA blob that starts at amta_offset + 0x24
        # The size of this blob is the first 4 bytes at amta_offset + 0x24
        try:
            datasize = _u32(data, amta_offset + 0x24)
            name = _cstring(data, amta_offset + 0x24 + datasize)
        except Exception:
            name = ""

        volume_db = 0.0
        volume_linear = 1.0
        if data_abs is not None:
            volume_db = _f32(data, data_abs + 0xC)
            volume_linear = 10.0 ** (volume_db / 20.0) if volume_db > -100 else 0.0

        return AmtaMetadata(
            name=name,
            audio_type="Stream",
            sample_rate=0,
            channel_count=data[amta_offset + 0x31] if (amta_offset + 0x31) < len(data) else 0,
            num_samples=0,
            loop_start=0,
            is_looped=False,
            volume_db=volume_db,
            volume_linear=volume_linear,
            stream_tracks=[],
            markers=markers,
            amplitude_peak=None,
        )

    data_rel  = _u32(data, amta_offset + 0xC)
    mark_rel  = _u32(data, amta_offset + 0x10)

    if major >= 3:
        strg_rel = _u32(data, amta_offset + 0x18)
    else:
        strg_rel = _u32(data, amta_offset + 0x14)

    data_abs = amta_offset + data_rel
    mark_abs = amta_offset + mark_rel if mark_rel else None
    strg_abs = amta_offset + strg_rel

    if strg_rel == 0:
        raise ValueError(
            f"AMTA at {amta_offset:#x}: STRG section offset is zero — "
            "cannot read asset name"
        )

    # STRG: 4 magic + 4 size + body of null-terminated strings
    strg_body = strg_abs + 0x8

    # DATA section: 4 magic + 4 size, then body
    data_body = data_abs + 0x8

    # DATA body layout (from spec):
    #   0x0  4   asset name offset into STRG body
    #   0x4  4   number of output samples at 48000 Hz
    #   0x8  1   type (0=Wave, 1=Stream)
    #   0x9  1   total channel count
    #   0xA  1   number of stream tracks
    #   0xB  1   flags (bit2 = is_looped)
    #   0xC  4   unknown float
    #   0x10 4   sample rate
    #   0x14 4   loop start sample
    #   0x18 4   number of samples
    #   0x1C 4   volume in dB (float, always negative)
    #   0x20 8*8 stream tracks (up to 8, each: 4 channel_count + 4 volume float)
    #   0x60 4   amplitude peak (float, v4.0+ only)

    name_strg_off  = _u32(data, data_body + 0x0)
    audio_type_raw = data[data_body + 0x8]
    channel_count  = data[data_body + 0x9]
    track_count    = data[data_body + 0xA]
    flags          = data[data_body + 0xB]
    sample_rate    = _u32(data, data_body + 0x10)
    loop_start     = _u32(data, data_body + 0x14)
    num_samples    = _u32(data, data_body + 0x18)
    volume_db      = _f32(data, data_body + 0x1C)
    volume_linear  = 10.0 ** (volume_db / 20.0)
    is_looped      = bool(flags & 0x4)
    audio_type     = "Stream" if audio_type_raw == 1 else "Wave"

    name = _cstring(data, strg_body + name_strg_off)

    stream_tracks: list[AmtaStreamTrack] = []
    for i in range(min(track_count, 8)):
        t_off = data_body + 0x20 + i * 8
        stream_tracks.append(AmtaStreamTrack(
            channel_count=_u32(data, t_off + 0x0),
            volume=_f32(data, t_off + 0x4),
        ))

    amplitude_peak: float | None = None
    if major >= 4:
        amplitude_peak = _f32(data, data_body + 0x60)

    # MARK section: 4 magic + 4 size + body
    #   body 0x0: entry count
    #   body 0x4: entries (each: 4 id + 4 name_off + 4 start + 4 length)
    markers: list[AmtaMarker] = []
    if mark_abs is not None:
        mark_body = mark_abs + 0x8
        marker_count = _u32(data, mark_body + 0x0)
        for i in range(marker_count):
            m_off = mark_body + 0x4 + i * 16
            m_name_off = _u32(data, m_off + 0x4)
            markers.append(AmtaMarker(
                id=_u32(data, m_off + 0x0),
                name=_cstring(data, strg_body + m_name_off),
                start=_u32(data, m_off + 0x8),
                length=_u32(data, m_off + 0xC),
            ))

    return AmtaMetadata(
        name=name,
        audio_type=audio_type,
        sample_rate=sample_rate,
        channel_count=channel_count,
        num_samples=num_samples,
        loop_start=loop_start,
        is_looped=is_looped,
        volume_db=volume_db,
        volume_linear=volume_linear,
        stream_tracks=stream_tracks,
        markers=markers,
        amplitude_peak=amplitude_peak,
    )


# ---------------------------------------------------------------------------
# BARS parsing
# ---------------------------------------------------------------------------

def parse_bars(data: bytes) -> BarsFile:
    """Parse a decompressed BARS file and return all entries with metadata."""
    if data[:4] != _BARS_MAGIC:
        raise ValueError(f"Not a BARS file (got magic {data[:4]!r})")

    num_assets = _u32(data, 0xC)

    hashes_start  = 0x10
    offsets_start = hashes_start + num_assets * 4

    entries: list[BarsEntry] = []
    for i in range(num_assets):
        name_hash = _u32(data, hashes_start + i * 4)
        amta_off = _u32(data, offsets_start + i * 8 + 0)
        bwav_raw = _i32(data, offsets_start + i * 8 + 4)
        bwav_off = -1 if bwav_raw in (-1, 0) else bwav_raw
        if bwav_off != -1:
            if bwav_off + 14 > len(data):
                bwav_off = -1
            elif data[bwav_off:bwav_off+4] != b"BWAV":
                bwav_off = -1
            else:
                bom = data[bwav_off+4:bwav_off+6]
                import struct
                if bom == b'\xfe\xff':
                    blocks = struct.unpack('>H', data[bwav_off+12:bwav_off+14])[0]
                else:
                    blocks = struct.unpack('<H', data[bwav_off+12:bwav_off+14])[0]
                if blocks == 0:
                    bwav_off = -1

        metadata = _parse_amta(data, amta_off)
        entries.append(BarsEntry(
            name=metadata.name,
            name_hash=name_hash,
            amta_offset=amta_off,
            bwav_offset=bwav_off,
            metadata=metadata,
        ))

    return BarsFile(entries=entries, data=data)


# ---------------------------------------------------------------------------
# Romfs lookup
# ---------------------------------------------------------------------------

def _find_bwav_in_romfs(name: str, romfs_path: str) -> bytes | None:
    for pattern in _BWAV_SEARCH_PATHS:
        candidate = Path(romfs_path) / pattern.format(name=name)
        if candidate.is_file():
            return candidate.read_bytes()
    return None


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def is_bars_extension(logical_path: str) -> bool:
    lower = logical_path.lower().replace("\\", "/")
    if lower.endswith(".zs"):
        lower = lower[:-3]
    return lower.endswith(".bars")


def is_bars_binary(file_data: bytes) -> bool:
    if len(file_data) >= 4 and file_data[:4] == _BARS_MAGIC:
        return True
    try:
        data, _, _ = decompress_container(file_data, "", "")
    except ValueError:
        data = file_data
    return len(data) >= 4 and data[:4] == _BARS_MAGIC


@dataclass
class BarsAudioResult:
    name: str
    wav_path: str
    is_prefetch: bool
    metadata: AmtaMetadata | None


def read_bars_entry_audio(
    file_data: bytes,
    entry_index: int = 0,
    logical_path: str = "",
    romfs_path: str = "",
    force_prefetch: bool = False,
) -> BarsAudioResult:
    """
    Decode audio for one entry in a BARS file.

    Tries the full BWAV from romfs first; falls back to the embedded prefetch
    clip if romfs is unavailable or the file isn't found.
    """
    data, _, _ = decompress_container(file_data, logical_path, romfs_path)
    bars = parse_bars(data)

    if entry_index >= len(bars.entries):
        raise IndexError(
            f"Entry index {entry_index} out of range "
            f"(BARS has {len(bars.entries)} entries)"
        )

    entry = bars.entries[entry_index]

    if romfs_path and not force_prefetch:
        bwav_data = _find_bwav_in_romfs(entry.name, romfs_path)
        if bwav_data is not None:
            wav_path = read_bwav_to_temp_wav(bwav_data, entry.name + ".bwav", romfs_path)
            return BarsAudioResult(
                name=entry.name, wav_path=wav_path,
                is_prefetch=False, metadata=entry.metadata,
            )

    if entry.bwav_offset == -1:
        if romfs_path:
            raise FileNotFoundError(
                f"Full BWAV not found in romfs for '{entry.name}', "
                "and this entry has no embedded prefetch clip."
            )
        raise FileNotFoundError(
            f"No audio available for '{entry.name}': "
            "no prefetch clip and romfs is not configured."
        )

    bwav_data = data[entry.bwav_offset:]
    wav_path = read_bwav_to_temp_wav(bwav_data, entry.name + ".bwav", romfs_path)
    return BarsAudioResult(
        name=entry.name, wav_path=wav_path,
        is_prefetch=True, metadata=entry.metadata,
    )


def list_bars_entries(
    file_data: bytes,
    logical_path: str = "",
    romfs_path: str = "",
) -> list[dict]:
    """
    Return a list of entry descriptors for all assets in a BARS file.
    Each dict includes the full AMTA metadata alongside playback availability.
    """
    data, _, _ = decompress_container(file_data, logical_path, romfs_path)
    bars = parse_bars(data)

    result = []
    for entry in bars.entries:
        has_romfs = False
        if romfs_path:
            has_romfs = _find_bwav_in_romfs(entry.name, romfs_path) is not None

        m = entry.metadata
        result.append({
            "name": entry.name,
            "name_hash": entry.name_hash,
            "amta_offset": entry.amta_offset,
            "bwav_offset": entry.bwav_offset,
            "has_prefetch": entry.bwav_offset != -1,
            "has_romfs_bwav": has_romfs,
            "metadata": {
                "audio_type": m.audio_type,
                "sample_rate": m.sample_rate,
                "channel_count": m.channel_count,
                "num_samples": m.num_samples,
                "loop_start": m.loop_start,
                "is_looped": m.is_looped,
                "volume_db": m.volume_db,
                "volume_linear": m.volume_linear,
                "amplitude_peak": m.amplitude_peak,
                "stream_tracks": [
                    {"channel_count": t.channel_count, "volume": t.volume}
                    for t in m.stream_tracks
                ],
                "markers": [
                    {"id": mk.id, "name": mk.name, "start": mk.start, "length": mk.length}
                    for mk in m.markers
                ],
            } if m is not None else None,
        })
    return result