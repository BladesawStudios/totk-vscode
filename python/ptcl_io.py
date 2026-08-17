import os

import yaml
from vendor_sys import add_vendor_to_path

add_vendor_to_path("ptcl")

from ptcl import ptcl_apply_edits_lib, ptcl_binary_to_text_lib


class _PtclDumper(yaml.SafeDumper):
    """Emits float vectors inline; everything else stays in block style."""


def _represent_list(dumper, data):
    inline = bool(data) and all(
        isinstance(item, (int, float)) and not isinstance(item, bool) for item in data
    )
    return dumper.represent_sequence("tag:yaml.org,2002:seq", data, flow_style=inline)


_PtclDumper.add_representer(list, _represent_list)


def _untuple(node):
    if isinstance(node, (tuple, list)):
        return [_untuple(item) for item in node]
    if isinstance(node, dict):
        return {key: _untuple(value) for key, value in node.items()}
    return node


def ptclbin_to_json(binary_data: bytes) -> str:
    try:
        result = ptcl_binary_to_text_lib(binary_data)
        if isinstance(result, bytes):
            result = result.decode("utf-8")

        document = yaml.load(result, Loader=yaml.Loader)
        if not isinstance(document, dict):
            raise ValueError(result)

        compact = os.environ.get("TOTK_PTCL_VECTOR_FORMAT", "compact") != "expanded"
        return yaml.dump(
            _untuple(document),
            Dumper=_PtclDumper if compact else yaml.SafeDumper,
            sort_keys=False,
            allow_unicode=True,
            indent=4,
            width=4096,
        )
    except Exception as e:
        raise ValueError(f"Error converting PtclBin: {e}") from e


def json_to_ptclbin(original_binary: bytes, json_string: str) -> bytes:
    """Apply json_string (which is actually YAML) edits back to the original PtclBin"""
    try:
        result = ptcl_apply_edits_lib(original_binary, json_string.encode("utf-8"))
        if isinstance(result, str) and result.startswith("Error:"):
            raise ValueError(result)
        return result
    except Exception as e:
        raise ValueError(f"Error restoring PtclBin: {e}") from e
