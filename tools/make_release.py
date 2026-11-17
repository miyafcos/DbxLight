#!/usr/bin/env python3
"""Build a deterministic, framework-dependent Windows x64 release (stdlib only)."""
import argparse
import getpass
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
FILES = sorted([
    "DbxLight.exe", "DbxLight.dll", "DbxLight.runtimeconfig.json", "DbxLight.deps.json",
    "dbxl.exe", "dbxl.dll", "dbxl.runtimeconfig.json", "dbxl.deps.json", "DbxLight.Core.dll",
])


def scan(files):
    """Return (basename, byte offset) hits without printing identifying text."""
    home = str(Path.home())
    terms = {getpass.getuser(), home.replace("\\", "/"), home.replace("/", "\\")}
    patterns = set()
    for term in terms:
        if term:
            for variant in {term, term.lower(), term.upper(), term.casefold()}:
                for encoding in ("utf-8", "utf-16le"):
                    patterns.add(variant.encode(encoding))
    hits, total = [], 0
    for path in sorted(files, key=lambda p: p.name):
        data = path.read_bytes()
        total += len(data)
        offsets = set()
        for pattern in patterns:
            offsets.update(m.start() for m in re.finditer(re.escape(pattern), data, re.IGNORECASE))
        hits.extend((path.name, offset) for offset in sorted(offsets))
    return hits, total


class ReleaseParser(argparse.ArgumentParser):
    def error(self, message):
        self.exit(3, self.format_usage() + "error: " + message + "\n")


def main(argv=None):
    parser = ReleaseParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--license", type=Path)
    parser.add_argument("--version")
    args = parser.parse_args(argv)
    source, out = args.source.resolve(), args.out.resolve()
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        return 2
    props = source / "Directory.Build.props"
    if not props.is_file():
        parser.error("Source project not found.")
    try:
        version = args.version or ET.parse(props).findtext(".//Version")
    except (OSError, ET.ParseError):
        parser.error("Source version could not be read.")
    if version is None or not VERSION.fullmatch(version):
        parser.error("Version must be major.minor.patch.")
    if args.license is not None and not args.license.is_file():
        parser.error("License file not found.")
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="dbxl-release-", dir=out) as temporary:
        work = Path(temporary)
        publish, empty_feed = work / "publish", work / "empty-feed"
        empty_feed.mkdir()
        for project in ("DbxLight.Tray", "DbxLight.Cli"):
            command = [
                "dotnet", "publish", str(source / "src" / project / (project + ".csproj")),
                "-c", "Release", "-r", "win-x64", "--self-contained", "false",
                "-m:1", "--disable-build-servers", "-o", str(publish),
                "-p:Version=" + version, "-p:DebugType=None", "-p:DebugSymbols=false",
                "-p:PathMap=" + str(source) + "=/_/",
                "-p:RestoreSources=" + str(empty_feed), "-p:NuGetAudit=false",
            ]
            built = subprocess.run(command, capture_output=True, text=True, errors="replace")
            if built.returncode:
                print("dotnet publish failed.", file=sys.stderr)
                # Avoid publishing machine paths from build diagnostics.
                return 1
        if list(publish.rglob("*.pdb")):
            print("PDB present in publish output.", file=sys.stderr)
            return 1
        actual = sorted(p.name for p in publish.iterdir())
        if actual != FILES:
            print("Unexpected publish file list.", file=sys.stderr)
            return 1
        paths = [publish / name for name in FILES]
        if args.license is not None:
            license_path = publish / "LICENSE"
            license_path.write_bytes(args.license.read_bytes())
            paths.append(license_path)
        entries = [
            {"name": p.name, "size": p.stat().st_size, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
            for p in sorted(paths, key=lambda p: p.name)
        ]
        manifest = publish / "release.json"
        manifest.write_text(json.dumps({"schema": 1, "version": version, "files": entries},
                                      ensure_ascii=True, separators=(",", ":")) + "\n",
                            encoding="utf-8", newline="\n")
        paths.append(manifest)
        hits, size = scan(paths)
        if hits:
            for name, offset in hits:
                print(f"{name}:{offset}")
            return 1
        print(f"scan: files={len(paths)} bytes={size} hits=0")
        filename = f"DbxLight-{version}-win-x64.zip"
        archive = out / filename
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for path in sorted(paths, key=lambda p: p.name):
                info = zipfile.ZipInfo(path.name, date_time=(2000, 1, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = 3
                info.external_attr = 0o100644 << 16
                z.writestr(info, path.read_bytes(), compresslevel=9)
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        (out / (filename + ".sha256")).write_text(digest + "  " + filename + "\n",
                                                 encoding="ascii", newline="\n")
        print(filename + " sha256=" + digest)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, ET.ParseError):
        print("Release build failed.", file=sys.stderr)
        sys.exit(1)
