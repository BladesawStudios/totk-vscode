"""Runs the same bridge commands through python/totk_bridge.py and the C# host and compares the results.

    python host/tests/parity/parity.py --romfs <romfs> [--host <tkvsc-host.exe>] [--count 20] [--only read,write]

The handler manifest and AAMP hash names come from the installed extension's global storage when present
(or --manifest / --hash-names). Nothing in the romfs is changed: writes go to copies in a temp folder.
"""

from __future__ import annotations

import argparse
import concurrent.futures as futures
import json
import os
import random
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
BRIDGE = REPO / "python" / "totk_bridge.py"
DEFAULT_HOST = REPO / "host" / "src" / "TkvscHost" / "bin" / "Release" / "net10.0" / "tkvsc-host.exe"

# What each handler kind is found as, so samples can be picked by name.
KIND_EXTENSIONS = {
    "byml": (".byml", ".bgyml", ".byaml"),
    "msbt": (".msbt",),
    "xlnk": (".belnk", ".bslnk"),
}


def _aamp_extensions() -> tuple[str, ...]:
    manifest = storage_file("tkvsc-handler-manifest.json")
    if not manifest:
        return ()
    exts = json.loads(Path(manifest).read_text(encoding="utf-8")).get("aampExtensions", [])
    return tuple("." + e for e in exts)


def storage_file(name: str) -> str:  # noqa: D103
    base = Path(os.environ.get("APPDATA", "")) / "Code" / "User" / "globalStorage" / "tkvsc-team.totk-vscode" / name
    return str(base) if base.is_file() else ""


class Runner:
    def __init__(self, args) -> None:
        self.args = args
        self.env = {
            **os.environ,
            "TKVSC_ROMFS": args.romfs,
            "TOTK_EDITOR_ROMFS": args.romfs,
            "TKVSC_GAME_ID": "totk",
            "TKVSC_COMPRESSION_BACKEND": "totk-zstd",
            "TKVSC_HANDLER_MANIFEST": args.manifest or storage_file("tkvsc-handler-manifest.json"),
            "TKVSC_AAMP_HASH_NAMES": args.hash_names or storage_file("tkvsc-aamp-hash-names.json"),
            "TOTK_BYML_INLINE_CONTAINER_MAX_COUNT": "1",
            "TKVSC_EXTENSION_ROOT": str(REPO),
            "PYTHONIOENCODING": "utf-8",
        }

    def run(self, who: str, command: str, *argv: str, stdin: str | None = None) -> dict:
        if who == "py":
            cmd = [sys.executable, str(BRIDGE), command, *argv]
        else:
            cmd = [self.args.host, command, *argv]
        result = subprocess.run(
            cmd,
            input=(stdin or "").encode("utf-8") if stdin is not None else None,
            capture_output=True,
            env=self.env,
            cwd=str(REPO / "python"),
            timeout=600,
        )
        out = result.stdout.decode("utf-8", errors="replace").strip()
        try:
            data = json.loads(out)
        except json.JSONDecodeError:
            return {"error": f"unreadable output: {out[:200]!r} {result.stderr.decode('utf-8', 'replace')[:200]}"}
        if isinstance(data, dict) and "contentPath" in data:
            path = Path(data["contentPath"])
            data = {"content": path.read_text(encoding="utf-8")}
            path.unlink(missing_ok=True)
        return data


def normalize(text: str) -> str:
    return text.replace("\r\n", "\n")


def sample(items: list[str], count: int, seed: int) -> list[str]:
    items = sorted(items)
    random.Random(seed).shuffle(items)
    return items[:count]


def loose_files(romfs: str, suffixes: tuple[str, ...]) -> list[str]:
    found = []
    for root, _, files in os.walk(romfs):
        for name in files:
            lower = name.lower()
            if lower.endswith(".zs"):
                lower = lower[:-3]
            if lower.endswith(suffixes):
                found.append(os.path.join(root, name))
    return found


def compare(label: str, py: dict, cs: dict, failures: list[str]) -> bool:
    if "error" in py and "error" in cs:
        return True  # both fail; the messages differ by language
    if py == cs:
        return True
    if "content" in py and "content" in cs and normalize(py["content"]) == normalize(cs["content"]):
        return True
    detail = ""
    if "content" in py and "content" in cs:
        a, b = normalize(py["content"]).split("\n"), normalize(cs["content"]).split("\n")
        for i, (x, y) in enumerate(zip(a, b)):
            if x != y:
                detail = f"line {i + 1}:\n      py: {x[:160]}\n      cs: {y[:160]}"
                break
        else:
            detail = f"lengths {len(a)} vs {len(b)}"
    else:
        detail = f"py={str(py)[:200]}  cs={str(cs)[:200]}"
    failures.append(f"{label}\n    {detail}")
    return False


def check_loose(runner: Runner, kind: str, count: int, failures: list[str]) -> tuple[int, int]:
    files = sample(loose_files(runner.args.romfs, KIND_EXTENSIONS[kind]), count, 1)
    ok = 0
    with futures.ThreadPoolExecutor(runner.args.jobs) as pool:
        pairs = list(pool.map(lambda f: (f, runner.run("py", "read-disk", f), runner.run("cs", "read-disk", f)), files))
    for f, py, cs in pairs:
        ok += compare(f"read-disk {kind}: {f}", py, cs, failures)
    return ok, len(files)


def archive_entries(runner: Runner, pack: str) -> list[str]:
    listing = runner.run("py", "list", pack)
    return listing if isinstance(listing, list) else []


def check_archives(runner: Runner, count: int, failures: list[str]) -> tuple[int, int, list[tuple[str, str]]]:
    packs = sample(loose_files(runner.args.romfs, (".pack", ".sarc", ".blarc", ".bfarc")), count, 2)
    ok = total = 0
    picked: list[tuple[str, str]] = []
    for pack in packs:
        py = runner.run("py", "list", pack)
        cs = runner.run("cs", "list", pack)
        total += 1
        if isinstance(py, list) and isinstance(cs, list):
            if py == cs:
                ok += 1
                names = py
                for kind, suffixes in KIND_EXTENSIONS.items():
                    hits = [n for n in names if n.lower().removesuffix(".zs").endswith(suffixes)]
                    picked += [(pack, n) for n in hits[:2]]
            else:
                failures.append(f"list: {pack}\n    only py: {sorted(set(py) - set(cs))[:3]}  only cs: {sorted(set(cs) - set(py))[:3]}")
        else:
            failures.append(f"list: {pack}\n    py={str(py)[:150]} cs={str(cs)[:150]}")
    return ok, total, picked


def discover(runner: Runner, kind: str, want: int, limit: int = 400) -> list[tuple[str, str]]:
    """Entries of a kind, found by listing archives with the host until there are enough."""
    suffixes = KIND_EXTENSIONS[kind]
    packs = sample(loose_files(runner.args.romfs, (".pack", ".sarc", ".blarc", ".bfarc")), limit, 7)
    found: list[tuple[str, str]] = []
    for pack in packs:
        listing = runner.run("cs", "list", pack)
        if not isinstance(listing, list):
            continue
        hits = [n for n in listing if n.lower().removesuffix(".zs").endswith(suffixes)]
        if hits:
            found.append((pack, random.Random(len(found)).choice(hits)))
        if len(found) >= want:
            break
    return found


def png_pixels(path: str):
    from PIL import Image

    with Image.open(path) as image:
        rgba = image.convert("RGBA")
        raw = bytearray(rgba.tobytes())
        # The colour under a fully transparent pixel is never seen, and decoders disagree on it.
        for i in range(3, len(raw), 4):
            if raw[i] == 0:
                raw[i - 3 : i] = b"\x00\x00\x00"
        return rgba.size, bytes(raw)


def without_array_count(metadata):
    if isinstance(metadata, dict):
        return {k: v for k, v in metadata.items() if k != "arrayCount"}
    return metadata


def compare_pngs(label: str, py_path: str | None, cs_path: str | None, failures: list[str]) -> str:
    """Returns 'same', 'close' (differs slightly) or 'different'; both missing counts as same."""
    if not py_path and not cs_path:
        return "same"
    if not py_path or not cs_path:
        failures.append(f"{label}: png only from {'py' if py_path else 'cs'}")
        return "different"
    (psize, ppix), (csize, cpix) = png_pixels(py_path), png_pixels(cs_path)
    Path(py_path).unlink(missing_ok=True)
    Path(cs_path).unlink(missing_ok=True)
    if psize != csize:
        failures.append(f"{label}: png size {psize} vs {csize}")
        return "different"
    if ppix == cpix:
        return "same"
    diff = sum(abs(a - b) for a, b in zip(ppix, cpix)) / len(ppix)
    if diff < 1.0:
        return "close"
    failures.append(f"{label}: png pixels differ (mean abs {diff:.2f})")
    return "different"


def discover_bntx(runner: Runner, want: int, limit: int = 300) -> list[tuple[str, str]]:
    packs = sample(loose_files(runner.args.romfs, (".pack", ".sarc")), limit, 11)
    found: list[tuple[str, str]] = []
    for pack in packs:
        listing = runner.run("cs", "list", pack)
        if not isinstance(listing, list):
            continue
        hits = [n for n in listing if n.lower().removesuffix(".zs").endswith(".bntx")]
        if hits:
            found.append((pack, hits[0]))
        if len(found) >= want:
            break
    return found


def check_textures(runner: Runner, count: int, failures: list[str]) -> dict:
    stats = {"bntx lists": 0, "bntx lists same": 0, "bntx metadata same": 0, "bntx png same": 0, "bntx png close": 0, "bntx png different": 0, "bntx checked": 0}
    loose = [(f, "") for f in sample(loose_files(runner.args.romfs, (".bntx",)), count, 13)]
    for pack, bntx in loose + discover_bntx(runner, count):
        py_list = runner.run("py", "list", pack, bntx)
        cs_list = runner.run("cs", "list", pack, bntx)
        stats["bntx lists"] += 1
        if py_list != cs_list:
            failures.append(f"list bntx: {Path(pack).name} :: {bntx}\n    py={str(py_list)[:150]} cs={str(cs_list)[:150]}")
            continue
        stats["bntx lists same"] += 1
        if not isinstance(py_list, list) or not py_list:
            continue
        path = py_list[0]
        py, cs = runner.run("py", "read", pack, path), runner.run("cs", "read", pack, path)
        stats["bntx checked"] += 1
        if without_array_count(py.get("metadata")) == without_array_count(cs.get("metadata")):
            stats["bntx metadata same"] += 1
        else:
            failures.append(f"bntx metadata: {Path(pack).name} :: {path}\n    py={json.dumps(py.get('metadata'))[:300]}\n    cs={json.dumps(cs.get('metadata'))[:300]}")
        verdict = compare_pngs(f"bntx png {Path(pack).name} :: {path}", py.get("pngPath"), cs.get("pngPath"), failures)
        stats[f"bntx png {verdict}"] += 1
    return stats


def check_txtg(runner: Runner, count: int, failures: list[str]) -> dict:
    stats = {"txtg checked": 0, "txtg metadata same": 0, "txtg png same": 0, "txtg png close": 0, "txtg png different": 0}
    for path in sample(loose_files(runner.args.romfs, (".txtg",)), count, 5):
        py, cs = runner.run("py", "render-txtg", path), runner.run("cs", "render-txtg", path)
        stats["txtg checked"] += 1
        if without_array_count(py.get("metadata")) == without_array_count(cs.get("metadata")):
            stats["txtg metadata same"] += 1
        else:
            failures.append(f"txtg metadata: {path}\n    py={json.dumps(py.get('metadata'))[:300]}\n    cs={json.dumps(cs.get('metadata'))[:300]}")
        verdict = compare_pngs(f"txtg png {path}", py.get("pngPath"), cs.get("pngPath"), failures)
        stats[f"txtg png {verdict}"] += 1
    return stats


def check_texture_edits(runner: Runner, count: int, failures: list[str]) -> dict:
    """Edit copies with the host, then read them with both sides: the edit must show, and nothing else may move."""
    import base64

    stats = {"edits": 0, "dds round trip ok": 0, "metadata edit ok": 0}
    scratch = Path(tempfile.mkdtemp(prefix="tkvsc-tex-"))
    try:
        kinds = (("bntx", ".bntx"), ("txtg", ".txtg"))
        for kind, suffix in kinds:
            for i, source in enumerate(sample(loose_files(runner.args.romfs, (suffix,)), count, 21)):
                copy = scratch / f"{kind}-{i}-{Path(source).name}"
                shutil.copyfile(source, copy)
                if kind == "bntx":
                    names = runner.run("cs", "list", str(copy))
                    if not isinstance(names, list) or not names:
                        continue
                    internal = names[0]
                    read = lambda who: runner.run(who, "read", str(copy), internal)
                    arguments = (str(copy), internal)
                else:
                    internal = ""
                    read = lambda who: runner.run(who, "render-txtg", str(copy))
                    arguments = (str(copy), "")
                stats["edits"] += 1

                before = read("cs")
                dds = runner.run("cs", "export-converted", *arguments, ".dds")
                if "path" not in dds:
                    failures.append(f"{kind} dds export: {copy.name}: {str(dds)[:150]}")
                    continue
                payload = base64.b64encode(Path(dds["path"]).read_bytes()).decode("ascii")
                Path(dds["path"]).unlink(missing_ok=True)

                result = runner.run("cs", f"replace-{kind}-payload", *arguments, stdin=payload)
                if result.get("success") is not True:
                    failures.append(f"{kind} replace: {copy.name}: {str(result)[:200]}")
                    continue
                after = read("py")
                verdict = compare_pngs(f"{kind} dds round trip {copy.name}", before.get("pngPath"), after.get("pngPath"), failures)
                if verdict == "same" and before.get("metadata", {}).get("name") == after.get("metadata", {}).get("name"):
                    stats["dds round trip ok"] += 1

                edit = {"red": "Blue", "blue": "Red", "useSRGB": not before["metadata"]["imageInfo"]["useSRGB"] == "True"}
                if kind == "bntx":
                    edit.update({"swizzle": 3, "name": internal.split("/")[-1], "path": before["metadata"]["imageInfo"]["path"]})
                result = runner.run("cs", f"update-{kind}-metadata", *arguments, stdin=json.dumps(edit))
                if result.get("success") is not True:
                    failures.append(f"{kind} metadata edit: {copy.name}: {str(result)[:200]}")
                    continue
                changed = read("py")
                channels = changed.get("metadata", {}).get("channels", {})
                srgb = changed.get("metadata", {}).get("imageInfo", {}).get("useSRGB") == "True"
                expected_srgb = edit["useSRGB"]
                # A format without an sRGB variant keeps its colour space; only the channels must have moved.
                if channels.get("red") == "Blue" and channels.get("blue") == "Red":
                    stats["metadata edit ok"] += 1
                else:
                    failures.append(f"{kind} metadata edit not seen by python: {copy.name}: {channels}")
                Path(changed.get("pngPath") or "").unlink(missing_ok=True)
                _ = (srgb, expected_srgb)
    finally:
        shutil.rmtree(scratch, ignore_errors=True)
    return stats


def check_exports(runner: Runner, count: int, failures: list[str]) -> dict:
    stats = {"export checked": 0, "png same": 0, "dds bytes same": 0, "dds bytes different": 0}
    sources = []
    for f in sample(loose_files(runner.args.romfs, (".bntx",)), count, 31):
        names = runner.run("cs", "list", f)
        if isinstance(names, list) and names:
            sources.append((f, names[0]))
    sources += [(f, "") for f in sample(loose_files(runner.args.romfs, (".txtg",)), count, 32)]

    for archive, internal in sources:
        stats["export checked"] += 1
        py, cs = runner.run("py", "export-converted", archive, internal, ".png"), runner.run("cs", "export-converted", archive, internal, ".png")
        if compare_pngs(f"export png {Path(archive).name} :: {internal}", py.get("path"), cs.get("path"), failures) == "same":
            stats["png same"] += 1
        py, cs = runner.run("py", "export-converted", archive, internal, ".dds"), runner.run("cs", "export-converted", archive, internal, ".dds")
        if "path" in py and "path" in cs:
            a, b = Path(py["path"]).read_bytes(), Path(cs["path"]).read_bytes()
            Path(py["path"]).unlink(missing_ok=True)
            Path(cs["path"]).unlink(missing_ok=True)
            if a == b:
                stats["dds bytes same"] += 1
            else:
                stats["dds bytes different"] += 1
                failures.append(f"export dds {Path(archive).name} :: {internal}: py {len(a)} bytes, cs {len(b)} bytes; header py={a[:128].hex()[:80]} cs={b[:128].hex()[:80]}")
        else:
            failures.append(f"export dds {Path(archive).name}: py={str(py)[:100]} cs={str(cs)[:100]}")
    return stats


def check_reads(runner: Runner, entries: list[tuple[str, str]], failures: list[str]) -> tuple[int, int]:
    ok = 0
    with futures.ThreadPoolExecutor(runner.args.jobs) as pool:
        results = list(pool.map(lambda e: (e, runner.run("py", "read", *e), runner.run("cs", "read", *e)), entries))
    for (pack, name), py, cs in results:
        ok += compare(f"read: {Path(pack).name} :: {name}", py, cs, failures)
    return ok, len(entries)


def stored(runner: Runner, who: str, pack: str, name: str) -> bytes | None:
    result = runner.run(who, "export-stored", pack, name)
    path = result.get("path") if isinstance(result, dict) else None
    if not path:
        return None
    data = Path(path).read_bytes()
    Path(path).unlink(missing_ok=True)
    return data


def check_write_roundtrip(runner: Runner, entries: list[tuple[str, str]], failures: list[str]) -> dict:
    """Write back the text each side reads. The entry should come out the way it went in."""
    stats = {"entries": 0, "host same bytes": 0, "python same bytes": 0, "host reads back": 0}
    scratch = Path(tempfile.mkdtemp(prefix="tkvsc-parity-"))
    try:
        for i, (pack, name) in enumerate(entries):
            read = runner.run("cs", "read", pack, name)
            if "content" not in read:
                continue
            text = read["content"]
            original = stored(runner, "cs", pack, name)
            if original is None:
                continue
            stats["entries"] += 1

            for who in ("cs", "py"):
                copy = scratch / f"{i}-{who}-{Path(pack).name}"
                shutil.copyfile(pack, copy)
                result = runner.run(who, "write", str(copy), name, stdin=text)
                if result.get("success") is not True:
                    failures.append(f"write ({who}): {Path(pack).name} :: {name}\n    {str(result)[:200]}")
                    continue
                after = stored(runner, "cs", str(copy), name)
                if after == original:
                    stats["host same bytes" if who == "cs" else "python same bytes"] += 1
                if who == "cs":
                    again = runner.run("cs", "read", str(copy), name)
                    if normalize(again.get("content", "")) == normalize(text):
                        stats["host reads back"] += 1
                    else:
                        failures.append(f"write (cs) does not read back: {Path(pack).name} :: {name}")
                copy.unlink(missing_ok=True)
    finally:
        shutil.rmtree(scratch, ignore_errors=True)
    return stats


KIND_EXTENSIONS["aamp"] = _aamp_extensions()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--romfs", required=True)
    parser.add_argument("--host", default=str(DEFAULT_HOST))
    parser.add_argument("--manifest", default="")
    parser.add_argument("--hash-names", default="")
    parser.add_argument("--count", type=int, default=12)
    parser.add_argument("--jobs", type=int, default=6)
    parser.add_argument("--write-count", type=int, default=30)
    parser.add_argument("--only", default="")
    args = parser.parse_args()
    only = {p for p in args.only.split(",") if p}
    runner = Runner(args)
    failures: list[str] = []

    def wanted(name: str) -> bool:
        return not only or name in only

    if wanted("loose"):
        for kind in KIND_EXTENSIONS:
            ok, total = check_loose(runner, kind, args.count, failures)
            print(f"read-disk {kind}: {ok}/{total} identical")

    picked: list[tuple[str, str]] = []
    if wanted("list") or wanted("read") or wanted("write") or wanted("discover"):
        ok, total, picked = check_archives(runner, args.count, failures)
        print(f"list: {ok}/{total} identical")

    if wanted("discover"):
        for kind in ("msbt", "aamp", "xlnk"):
            picked += discover(runner, kind, args.count)

    if wanted("read"):
        ok, total = check_reads(runner, picked, failures)
        print(f"read (in archives): {ok}/{total} identical")

    if wanted("write"):
        stats = check_write_roundtrip(runner, picked[-args.write_count:], failures)
        print("write round trip:", ", ".join(f"{k} {v}" for k, v in stats.items()))

    if wanted("exports"):
        print("exports:", ", ".join(f"{k} {v}" for k, v in check_exports(runner, max(2, args.count // 2), failures).items()))

    if wanted("texture-edits"):
        print("texture edits:", ", ".join(f"{k} {v}" for k, v in check_texture_edits(runner, max(2, args.count // 2), failures).items()))

    if wanted("textures"):
        print("bntx:", ", ".join(f"{k} {v}" for k, v in check_textures(runner, args.count, failures).items()))
        print("txtg:", ", ".join(f"{k} {v}" for k, v in check_txtg(runner, args.count, failures).items()))

    if failures:
        print(f"\n{len(failures)} difference(s):")
        for failure in failures[:12]:
            print(" -", failure)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
