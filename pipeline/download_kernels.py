"""Download the SPICE kernels Skyfield needs for lunar surface geometry.

These are the four files used in Skyfield's "Planetary reference frames"
documentation (Moon example), all from NAIF ``generic_kernels``:

* ``de421.bsp``                     – planetary ephemeris (Sun, Earth, Moon)
* ``moon_080317.tf``                – frame kernel defining MOON_ME / MOON_PA
* ``pck00008.tpc``                  – planetary constants (radii, orientation)
* ``moon_pa_de421_1900-2050.bpc``   – high-precision lunar orientation

NAIF moved ``de421.bsp`` and ``pck00008.tpc`` into ``a_old_versions/``
folders; the URLs below are the verified official locations. There is
deliberately no fallback mirror: if a download fails, the script reports
which file failed and exits with a non-zero status.

Usage::

    python download_kernels.py            # into ./kernels
    python download_kernels.py --dir PATH
"""

from __future__ import annotations

import argparse
import sys
import urllib.error
import urllib.request
from pathlib import Path

NAIF = "https://naif.jpl.nasa.gov/pub/naif/generic_kernels"

KERNELS: dict[str, str] = {
    "de421.bsp": f"{NAIF}/spk/planets/a_old_versions/de421.bsp",
    "moon_080317.tf": f"{NAIF}/fk/satellites/moon_080317.tf",
    "pck00008.tpc": f"{NAIF}/pck/a_old_versions/pck00008.tpc",
    "moon_pa_de421_1900-2050.bpc": f"{NAIF}/pck/moon_pa_de421_1900-2050.bpc",
}

DEFAULT_DIR = Path(__file__).resolve().parent / "kernels"


def _mark(ok: bool, stream=None) -> str:
    """'✓'/'✗', or 'OK'/'FAIL' if the console encoding (e.g. Windows cp1250) cannot show them."""
    symbol = "✓" if ok else "✗"
    try:
        symbol.encode((stream or sys.stdout).encoding or "ascii")
    except (UnicodeEncodeError, LookupError):
        return "OK" if ok else "FAIL"
    return symbol


def _make_console_safe() -> None:
    """Never crash on characters the console encoding lacks (e.g. dashes in --help on cp852)."""
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")


def download(name: str, url: str, target_dir: Path) -> bool:
    """Download one kernel unless it already exists. Returns True on success."""
    target = target_dir / name
    if target.exists() and target.stat().st_size > 0:
        print(f"  {_mark(True)} {name} (already present)")
        return True

    tmp = target.with_suffix(target.suffix + ".part")
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "selene-pipeline/1.0"})
        with urllib.request.urlopen(req, timeout=120) as resp, open(tmp, "wb") as out:
            while chunk := resp.read(1 << 20):
                out.write(chunk)
        tmp.replace(target)
        print(f"  {_mark(True)} {name} ({target.stat().st_size / 1e6:.1f} MB)")
        return True
    except (urllib.error.URLError, OSError) as exc:
        tmp.unlink(missing_ok=True)
        reason = getattr(exc, "code", None) or getattr(exc, "reason", None) or exc
        print(f"  {_mark(False, sys.stderr)} {name}: download failed ({reason})\n      URL: {url}", file=sys.stderr)
        return False


def main() -> int:
    _make_console_safe()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir", type=Path, default=DEFAULT_DIR, help="target directory (default: ./kernels)")
    args = parser.parse_args()

    args.dir.mkdir(parents=True, exist_ok=True)
    print(f"Downloading SPICE kernels into {args.dir}")
    failed = [name for name, url in KERNELS.items() if not download(name, url, args.dir)]

    if failed:
        print(f"\nERROR: could not download: {', '.join(failed)}", file=sys.stderr)
        return 1
    print("All kernels ready.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
