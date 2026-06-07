import os
import sys
from pathlib import Path

# Ensure vendor/ptcl is in sys.path so ptcl.py can import .utils properly
_script_dir = Path(__file__).resolve().parent
_ptcl_vendor = _script_dir.parent / "vendor" / "ptcl"
if str(_ptcl_vendor) not in sys.path:
    sys.path.insert(0, str(_ptcl_vendor))

from ptcl import ptcl_binary_to_text_lib, ptcl_apply_edits_lib

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
        result = ptcl_apply_edits_lib(original_binary, json_string.encode('utf-8'))
        if isinstance(result, str) and result.startswith("Error:"):
            raise ValueError(result)
        return result
    except Exception as e:
        raise ValueError(f"Error restoring PtclBin: {e}")
