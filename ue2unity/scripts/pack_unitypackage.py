#!/usr/bin/env python3
"""Build a Unity .unitypackage from a folder tree.

Usage:
  python pack_unitypackage.py <src_folder> <out.unitypackage> [--assets-root Assets/CityPacks]

Each source file becomes a GUID folder in the package containing:
  asset       - the file bytes
  asset.meta  - minimal DefaultImporter meta (Unity regenerates proper importer
                settings on first import; GUIDs are not load-bearing here
                because glTFast resolves textures by relative path)
  pathname    - the target path inside the project, e.g. Assets/CityPacks/x.gltf
"""
import argparse
import io
import tarfile
import time
import uuid
from pathlib import Path

META_TMPL = """fileFormatVersion: 2
guid: {guid}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--assets-root", default="Assets/CityPacks")
    args = ap.parse_args()

    src = Path(args.src).resolve()
    files = sorted(p for p in src.rglob("*") if p.is_file())
    if not files:
        raise SystemExit(f"no files under {src}")

    now = int(time.time())
    with tarfile.open(args.out, "w:gz") as tar:
        for f in files:
            rel = f.relative_to(src).as_posix()
            target = f"{args.assets_root}/{rel}"
            guid = uuid.uuid4().hex

            def add_bytes(name: str, data: bytes) -> None:
                info = tarfile.TarInfo(name=f"{guid}/{name}")
                info.size = len(data)
                info.mtime = now
                tar.addfile(info, io.BytesIO(data))

            add_bytes("asset", f.read_bytes())
            add_bytes("asset.meta", META_TMPL.format(guid=guid).encode())
            add_bytes("pathname", target.encode())

    print(f"packed {len(files)} files -> {args.out}")


if __name__ == "__main__":
    main()
