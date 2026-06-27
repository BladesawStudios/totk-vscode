"""Load per-game MSBT tag definitions from Nintendo .gcf YAML configs."""

from __future__ import annotations

import os
from functools import lru_cache
from pathlib import Path

import yaml

_SCRIPT_DIR = Path(__file__).resolve().parent
_DEFAULT_GCF = _SCRIPT_DIR.parent / "vendor" / "TotK.gcf"


def default_msbt_config_path() -> str:
    return str(_DEFAULT_GCF)


def _resolve_config_path() -> str:
    override = os.environ.get("TKVSC_MSBT_CONFIG", "").strip()
    if override and os.path.isfile(override):
        return override
    if _DEFAULT_GCF.is_file():
        return str(_DEFAULT_GCF)
    return override or str(_DEFAULT_GCF)


@lru_cache(maxsize=8)
def _load_tag_tables(config_path: str) -> tuple[dict, dict]:
    path = Path(config_path)
    if not path.is_file():
        raise FileNotFoundError(f"MSBT game config not found: {config_path}")

    with path.open(encoding="utf-8") as handle:
        data = yaml.safe_load(handle) or {}

    tags = (data.get("msbt") or {}).get("tags") or []
    by_group_type: dict[str, dict] = {}
    by_name: dict[str, dict] = {}

    for tag in tags:
        if not isinstance(tag, dict):
            continue
        group = tag.get("group")
        type_ = tag.get("type")
        name = tag.get("name")
        if group is None or type_ is None or not name:
            continue
        key = f"{group}_{type_}"
        by_group_type[key] = tag
        by_name[name] = tag

    return by_group_type, by_name


def get_msbt_tag_tables() -> tuple[dict, dict]:
    """Return (MSBT_TAGS_BY_ID, MSBT_TAGS_BY_NAME) for the active game config."""
    config_path = _resolve_config_path()
    return _load_tag_tables(config_path)


def clear_msbt_tag_cache() -> None:
    _load_tag_tables.cache_clear()
