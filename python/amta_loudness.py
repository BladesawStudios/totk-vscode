"""Loudness statistics stored in TotK (version 5) AMTA blocks.

Each v5 AMTA DATA section holds a channel map, then a flags word (usually
0x4F) followed by five float32 values describing the asset's audio:

  +0x04  sample peak (linear, 0..1, rounded to 4 decimals)
  +0x08  loudest 400 ms RMS window (linear, channel average; approximate,
         ~1 dB median error - Nintendo's exact definition is unknown)
  +0x0C  momentary-max loudness (EBU R128, LUFS)
  +0x10  integrated loudness (EBU R128, LUFS)
  +0x14  loudness range (EBU Tech 3342, LU; 0 for clips under 3 s; exact
         for long tracks, off by a few LU on some clips under ~20 s)

Silent assets store peak 0 and -96 for both loudness values. Matched against
the vanilla romfs, peak / momentary-max / integrated agree to ~0.1 dB for
audio >= 3 s; short
sound effects deviate by a few dB (Nintendo's tool preprocesses them
differently), which is close enough for a replacement.

Pure Python on purpose: numpy isn't a bridge dependency, and this costs a
small fraction of the DSP-ADPCM encode that always precedes it.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass

FLOOR_DB = -96.0


@dataclass
class LoudnessStats:
    peak: float
    rms_max: float
    momentary_max: float
    integrated: float
    loudness_range: float


def _k_weighting(sample_rate: int) -> tuple[tuple[float, ...], tuple[float, ...]]:
    """BS.1770 pre-filter (high shelf) and RLB high-pass biquads for any
    sample rate (coefficient derivation as in libebur128)."""
    f0 = 1681.974450955533
    gain = 3.999843853973347
    q = 0.7071752369554196
    k = math.tan(math.pi * f0 / sample_rate)
    vh = 10.0 ** (gain / 20.0)
    vb = vh**0.4996667741545416
    a0 = 1.0 + k / q + k * k
    shelf = (
        (vh + vb * k / q + k * k) / a0,
        2.0 * (k * k - vh) / a0,
        (vh - vb * k / q + k * k) / a0,
        2.0 * (k * k - 1.0) / a0,
        (1.0 - k / q + k * k) / a0,
    )
    f0 = 38.13547087602444
    q = 0.5003270373238773
    k = math.tan(math.pi * f0 / sample_rate)
    a0 = 1.0 + k / q + k * k
    highpass = (2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0)
    return shelf, highpass


def _lufs(energy: float) -> float:
    if energy <= 0.0:
        return FLOOR_DB
    return max(FLOOR_DB, -0.691 + 10.0 * math.log10(energy))


def _percentile(sorted_values: list[float], pct: float) -> float:
    """Linear-interpolated percentile (numpy's default method)."""
    pos = (len(sorted_values) - 1) * pct / 100.0
    lo = math.floor(pos)
    hi = min(lo + 1, len(sorted_values) - 1)
    return sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * (pos - lo)


def _window_means(blocks: list[float], width: int) -> list[float]:
    return [sum(blocks[i : i + width]) / width for i in range(len(blocks) - width + 1)]


def measure_loudness(channels: list[list[int]], sample_rate: int) -> LoudnessStats:
    """Measure per-channel PCM16 audio the way TotK's AMTA values are stored."""
    num_samples = len(channels[0]) if channels else 0
    if num_samples == 0 or sample_rate <= 0:
        return LoudnessStats(0.0, 0.0, FLOOR_DB, FLOOR_DB, 0.0)

    (b0, b1, b2, a1, a2), (c1, c2) = _k_weighting(sample_rate)
    block = max(1, sample_rate // 10)  # 100 ms gating step
    num_blocks = (num_samples + block - 1) // block
    k_energy = [0.0] * num_blocks  # K-weighted, channels summed
    raw_energy = [0.0] * num_blocks  # unweighted, channel average
    peak = 0
    scale = 1.0 / 32768.0

    for channel in channels:
        x1 = x2 = y1 = y2 = w1 = w2 = 0.0
        for bi in range(num_blocks):
            k_acc = 0.0
            raw_acc = 0.0
            for v in channel[bi * block : (bi + 1) * block]:
                if v > peak:
                    peak = v
                elif -v > peak:
                    peak = -v
                x = v * scale
                raw_acc += x * x
                y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
                w = y - 2.0 * y1 + y2 - c1 * w1 - c2 * w2
                x2, x1, y2, y1, w2, w1 = x1, x, y1, y, w1, w
                k_acc += w * w
            k_energy[bi] += k_acc / block
            raw_energy[bi] += raw_acc / block / len(channels)

    # Clips shorter than one 400 ms window are measured as if padded with silence.
    if num_blocks < 4:
        k_energy += [0.0] * (4 - num_blocks)
        raw_energy += [0.0] * (4 - num_blocks)

    momentary = _window_means(k_energy, 4)
    momentary_max = _lufs(max(momentary))

    integrated = FLOOR_DB
    gated = [e for e in momentary if _lufs(e) > -70.0]
    if gated:
        relative = _lufs(sum(gated) / len(gated)) - 10.0
        gated = [e for e in gated if _lufs(e) > relative]
        if gated:
            integrated = _lufs(sum(gated) / len(gated))

    loudness_range = 0.0
    if len(k_energy) >= 30:  # needs at least one 3 s short-term window
        short_term = [e for e in _window_means(k_energy, 30) if _lufs(e) > -70.0]
        if short_term:
            relative = _lufs(sum(short_term) / len(short_term)) - 20.0
            levels = sorted(_lufs(e) for e in short_term if _lufs(e) > relative)
            if levels:
                loudness_range = _percentile(levels, 95) - _percentile(levels, 10)

    rms_max = math.sqrt(max(_window_means(raw_energy, 4)))

    return LoudnessStats(
        peak=round(min(peak * scale, 1.0), 4),
        rms_max=round(min(rms_max, 1.0), 4),
        momentary_max=momentary_max,
        integrated=integrated,
        loudness_range=loudness_range,
    )


def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def _f32(data: bytes, offset: int) -> float:
    return struct.unpack_from("<f", data, offset)[0]


def locate_loudness_block(data: bytes, amta_offset: int) -> int | None:
    """Offset of the flags word that precedes the five loudness floats, or None.

    The channel map before it varies in length, so scan for the flags word
    (0x4F/0x5F/0x6F/0x7F in the vanilla game) followed by plausible values.
    Finds the block in every vanilla TotK AMTA.
    """
    if data[amta_offset : amta_offset + 4] != b"AMTA":
        return None
    if (_u32(data, amta_offset + 0x4) >> 24) & 0xFF < 5:  # BOM u16 + version major byte
        return None
    end = amta_offset + 0x24 + _u32(data, amta_offset + 0x24)
    for p in range(amta_offset + 0x34, end - 0x13, 4):
        flags = _u32(data, p)
        if (
            0x40 <= flags < 0x100
            and flags & 0xF == 0xF
            and 0.0 <= _f32(data, p + 0x4) <= 1.0001
            and _f32(data, p + 0xC) <= 0.0
            and _f32(data, p + 0x10) <= 0.0
        ):
            return p
    return None


def write_loudness(data: bytearray, amta_offset: int, stats: LoudnessStats) -> bool:
    """Overwrite an AMTA's loudness floats in place (sizes never change).
    Returns False when the AMTA has no recognisable loudness block."""
    p = locate_loudness_block(data, amta_offset)
    if p is None:
        return False
    struct.pack_into(
        "<5f",
        data,
        p + 0x4,
        stats.peak,
        stats.rms_max,
        stats.momentary_max,
        stats.integrated,
        stats.loudness_range,
    )
    return True
