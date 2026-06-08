"""Centralized path reference and vendor management module.

Importing this module ensures all vendored packages in the 'vendor' directory
are properly inserted into sys.path so they can be imported directly.
"""

import sys
from pathlib import Path

# Resolve base directories
SCRIPT_DIR = Path(__file__).resolve().parent
WORKSPACE_DIR = SCRIPT_DIR.parent
VENDOR_DIR = WORKSPACE_DIR / "vendor"

# List of vendor directories to add to sys.path
VENDOR_SUBDIRS = [
    VENDOR_DIR / "ainb",
    VENDOR_DIR / "asb",
    VENDOR_DIR / "bphcl",
    VENDOR_DIR / "ptcl",
    VENDOR_DIR / "py-xlink2",
    VENDOR_DIR / "pymsbt",
    VENDOR_DIR / "hexpyt" / "src",
]

def setup_vendor_paths() -> None:
    """Scan and insert existing vendor directories into sys.path."""
    # Add script_dir itself and workspace root just in case
    for path in [SCRIPT_DIR, WORKSPACE_DIR]:
        path_str = str(path)
        if path.is_dir() and path_str not in sys.path:
            sys.path.insert(0, path_str)

    # Add all vendor directories
    for path in VENDOR_SUBDIRS:
        path_str = str(path)
        if path.is_dir() and path_str not in sys.path:
            sys.path.insert(0, path_str)

# Automatically run the path setup when imported
setup_vendor_paths()
