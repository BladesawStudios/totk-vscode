import sys
from pathlib import Path

from vendor_sys import add_vendor_to_path
add_vendor_to_path("ptcl")

from ptcl import ptcl_apply_edits_lib, ptcl_binary_to_text_lib


def ptclbin_to_json(binary_data: bytes) -> str:
    """Convert raw PtclBin bytes to JSON-like string"""
    try:
        # ptcl_binary_to_text_lib returns YAML text natively
        result = ptcl_binary_to_text_lib(binary_data)
        if isinstance(result, bytes):
            return result.decode("utf-8")
        return result
    except Exception as e:
        return f"Error converting PtclBin: {e}"


def json_to_ptclbin(original_binary: bytes, json_string: str) -> bytes:
    """Apply json_string (which is actually YAML) edits back to the original PtclBin"""
    try:
        result = ptcl_apply_edits_lib(original_binary, json_string.encode("utf-8"))
        if isinstance(result, str) and result.startswith("Error:"):
            raise ValueError(result)
        return result
    except Exception as e:
        raise ValueError(f"Error restoring PtclBin: {e}") from e
