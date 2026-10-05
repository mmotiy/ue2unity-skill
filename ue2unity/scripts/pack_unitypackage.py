#!/usr/bin/env python3
"""Build a Unity .unitypackage from a folder tree.

Usage:
  python pack_unitypackage.py <src_folder> <out.unitypackage> [--assets-root Assets/CityPacks]

Each source file becomes a GUID folder in the package containing:
  asset       - the file bytes
  asset.meta  - source sidecar metadata and its GUID, or minimal deterministic
                metadata when the source has no sidecar
  pathname    - the target path inside the project, e.g. Assets/CityPacks/x.gltf
"""
import argparse
import gzip
import io
import tarfile
import time
import uuid
import re
from pathlib import Path

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--assets-root", default="Assets/CityPacks")
    args = ap.parse_args()

    src = Path(args.src).resolve()
    files = sorted(p for p in src.rglob("*") if p.is_file() and p.suffix != '.meta')
    if not files:
        raise SystemExit(f"no files under {src}")

    now = int(time.time())
    # Unity silently skips payloads when gzip FNAME ends in .unitypackage.
    # Suppress this optional header; keep the proven tar payload unchanged.
    with open(args.out, 'wb') as output, \
            gzip.GzipFile(filename='', fileobj=output, mode='wb', compresslevel=1, mtime=0) as gz, \
            tarfile.open(fileobj=gz, mode='w|') as tar:
        for index, f in enumerate(files, 1):
            rel = f.relative_to(src).as_posix()
            target = f"{args.assets_root}/{rel}"
            sidecar = f.with_name(f.name + '.meta')
            if sidecar.exists():
                meta = sidecar.read_bytes()
                match = re.search(rb'^guid: ([0-9a-f]{32})\s*$', meta, re.MULTILINE)
                if not match:
                    raise ValueError(f'Invalid sidecar GUID: {sidecar}')
                guid = match.group(1).decode()
            else:
                guid = uuid.uuid5(uuid.NAMESPACE_URL, target).hex
                meta = f'fileFormatVersion: 2\nguid: {guid}\n'.encode()

            def add_bytes(name: str, data: bytes) -> None:
                info = tarfile.TarInfo(name=f"{guid}/{name}")
                info.size = len(data)
                info.mtime = now
                tar.addfile(info, io.BytesIO(data))

            info = tarfile.TarInfo(name=f'{guid}/asset')
            info.size = f.stat().st_size
            info.mtime = now
            with f.open('rb') as handle:
                tar.addfile(info, handle)
            add_bytes("asset.meta", meta)
            add_bytes("pathname", target.encode())
            if index % 100 == 0:
                print(f'packed {index}/{len(files)}', flush=True)

    print(f"packed {len(files)} files -> {args.out}")


if __name__ == "__main__":
    main()
