"""Convert Starlight's Definitions.eainbdef into the compact catalog TKVSC ships.

Starlight builds its node definition database by decoding every AINB in the game's
Logic/AI/Sequence folders and merging the node signatures it finds. That database is
what powers its "add node" search, and the same data drives the AINB node editor here.

About 95% of the .eainbdef payload is the per-definition list of files each node
appears in, which the editor does not use - dropping it takes the catalog from ~19.7 MB
to ~1.7 MB, and ~0.2 MB once gzipped.

Usage:
    python scripts/convert_ainb_defs.py <path to Definitions.eainbdef> [-o config/ainbNodeDefs.json.gz]
"""

from __future__ import annotations

import argparse
import gzip
import json
import struct
import sys
from pathlib import Path

MAGIC = b"EAINBDEF"
SUPPORTED_VERSION = 2

# Starlight's AINBFile::ValueType order -> the AINB library's dict keys.
VALUE_TYPES = ["Int", "Bool", "Float", "String", "Vector3F", "Pointer"]

# Starlight's NodeDef::Category order.
CATEGORIES = ["Logic", "AI", "Sequence"]

# AINBFile::NodeTypes -> ainb.node.NodeType names (the two enums agree).
NODE_TYPES = {
    0: "UserDefined",
    1: "Element_S32Selector",
    2: "Element_Sequential",
    3: "Element_Simultaneous",
    4: "Element_F32Selector",
    5: "Element_StringSelector",
    6: "Element_RandomSelector",
    7: "Element_BoolSelector",
    8: "Element_Fork",
    9: "Element_Join",
    10: "Element_Alert",
    20: "Element_Expression",
    100: "Element_ModuleIF_Input_S32",
    101: "Element_ModuleIF_Input_F32",
    102: "Element_ModuleIF_Input_Vec3f",
    103: "Element_ModuleIF_Input_String",
    104: "Element_ModuleIF_Input_Bool",
    105: "Element_ModuleIF_Input_Ptr",
    200: "Element_ModuleIF_Output_S32",
    201: "Element_ModuleIF_Output_F32",
    202: "Element_ModuleIF_Output_Vec3f",
    203: "Element_ModuleIF_Output_String",
    204: "Element_ModuleIF_Output_Bool",
    205: "Element_ModuleIF_Output_Ptr",
    300: "Element_ModuleIF_Child",
    400: "Element_StateEnd",
    500: "Element_SplitTiming",
}


class Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.pos = 0

    def u8(self) -> int:
        value = self.data[self.pos]
        self.pos += 1
        return value

    def u16(self) -> int:
        (value,) = struct.unpack_from("<H", self.data, self.pos)
        self.pos += 2
        return value

    def u32(self) -> int:
        (value,) = struct.unpack_from("<I", self.data, self.pos)
        self.pos += 4
        return value

    def string(self) -> str:
        length = self.u16()
        value = self.data[self.pos : self.pos + length].decode("utf-8", "replace")
        self.pos += length
        return value

    def skip(self, count: int) -> None:
        self.pos += count


def _read_params(reader: Reader, has_flags: bool) -> list:
    out = []
    for _ in range(reader.u16()):
        name = reader.string()
        classname = reader.string()
        value_type = reader.u8()
        if has_flags:
            reader.skip(reader.u8())
        entry = {"n": name, "t": VALUE_TYPES[value_type] if value_type < len(VALUE_TYPES) else "Int"}
        if classname:
            entry["c"] = classname
        out.append(entry)
    return out


def parse_definitions(data: bytes) -> list:
    if data[:8] != MAGIC:
        raise ValueError(f"Not an eainbdef file (magic was {data[:8]!r})")

    reader = Reader(data)
    reader.skip(8)
    version = reader.u16()
    if version != SUPPORTED_VERSION:
        raise ValueError(f"Unsupported eainbdef version {version}, expected {SUPPORTED_VERSION}")

    definitions = []
    for _ in range(reader.u32()):
        name = reader.string()
        categories = [CATEGORIES[c] for c in (reader.u8() for _ in range(reader.u16())) if c < 3]
        node_type = reader.u16()
        flow = [reader.string() for _ in range(reader.u16())]
        inputs = _read_params(reader, True)
        outputs = _read_params(reader, False)
        properties = _read_params(reader, True)
        # Per-definition file usage lists: the bulk of the file, and unused here.
        for _ in range(reader.u16()):
            reader.string()
        reader.u32()  # name hash

        entry = {"name": name, "type": NODE_TYPES.get(node_type, "UserDefined")}
        if categories:
            entry["cats"] = categories
        if flow:
            entry["flow"] = flow
        if inputs:
            entry["in"] = inputs
        if outputs:
            entry["out"] = outputs
        if properties:
            entry["props"] = properties
        definitions.append(entry)

    if reader.pos != len(data):
        raise ValueError(f"Trailing data: consumed {reader.pos} of {len(data)} bytes")

    return definitions


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help="Path to Definitions.eainbdef")
    parser.add_argument(
        "-o",
        "--output",
        default=str(Path(__file__).resolve().parent.parent / "config" / "ainbNodeDefs.json.gz"),
        help="Output path (gzipped JSON)",
    )
    args = parser.parse_args()

    data = Path(args.input).read_bytes()
    definitions = parse_definitions(data)
    definitions.sort(key=lambda d: d["name"].lower())

    payload = json.dumps(
        {"version": 1, "source": "Starlight Definitions.eainbdef", "definitions": definitions},
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(gzip.compress(payload, 9))

    print(f"{len(definitions)} definitions")
    print(f"{len(data) / 1e6:.1f} MB -> {len(payload) / 1e6:.2f} MB JSON -> "
          f"{out_path.stat().st_size / 1e6:.2f} MB gzipped")
    print(f"wrote {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
