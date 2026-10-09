#!/usr/bin/env python3
"""Generates THIRD-PARTY-NOTICES.txt for a MyVideoEditor build.

Lists everything shipped with the app, with copyright and licence text: MyVideoEditor itself,
the bundled FFmpeg (GPL; its full licence text is in ffmpeg/FFMPEG-LICENSE.txt), the bundled
.NET runtime, the Inter font, and every NuGet package the build restored (for the given
runtime, so each platform lists what it actually ships).

Licence texts come from the packages' own licence/notice files where they have them, then
from tools/notices/<Package>.txt overrides, then from standard texts in tools/notices/licenses/.

Run after a restore/publish for the runtime, e.g.:
    dotnet publish src/MyVideoEditor.App -c Release -r linux-x64 --self-contained true -o out
    python3 tools/generate-third-party-notices.py --rid linux-x64 --output out/THIRD-PARTY-NOTICES.txt
"""
import argparse
import hashlib
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTICES = os.path.join(ROOT, "tools", "notices")
# Shipped, but copied by MyVideoEditor.App.csproj rather than listed as package assets.
COPIED_BY_PROJECT = {"microsoft.ai.directml"}
# Build-time only: not shipped with the app.
BUILD_ONLY = {"avalonia.buildservices"}
# Licences for packages whose metadata doesn't name one (texts in tools/notices/<Package>.txt).
KNOWN_LICENCES = {"avalonia.angle.windows.natives": "BSD-3-Clause"}
LICENCE_FILE = re.compile(r"^(licen[cs]e|copying|third[-_ ]?party[-_ ]?notices|notice)[^/]*$", re.IGNORECASE)
RULE = "=" * 78


def read(path):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        return f.read().replace("\r\n", "\n").strip()


def standard_text(expression):
    """Standard licence text for an SPDX expression, without template copyright lines."""
    path = os.path.join(NOTICES, "licenses", f"{expression}.txt")
    if not os.path.exists(path):
        return None
    lines = [l for l in read(path).splitlines() if "<year>" not in l and "<copyright holders>" not in l and "<owner>" not in l]
    return "\n".join(lines).strip()


def nuspec_info(package_dir):
    nuspec = next((f for f in os.listdir(package_dir) if f.endswith(".nuspec")), None)
    text = read(os.path.join(package_dir, nuspec)) if nuspec else ""

    def tag(name):
        m = re.search(rf"<{name}[^>]*>([^<]*)</{name}>", text)
        return m.group(1).strip().replace("&amp;", "&") if m else None

    expression = None
    m = re.search(r'<license type="expression">([^<]+)</license>', text)
    if m:
        expression = m.group(1).strip()
    repository = re.search(r'<repository [^>]*url="([^"]+)"', text)
    url = tag("projectUrl") or (repository.group(1) if repository else None)
    return {
        "copyright": tag("copyright"),
        "authors": tag("authors"),
        # Drop tracking parameters only (fwlink URLs need their query).
        "url": (url.split("?")[0] if url and "utm_" in url else url or "").removesuffix(".git") or None,
        "expression": expression,
    }


def licence_files(package_dir):
    files = []
    for name in sorted(os.listdir(package_dir)):
        if LICENCE_FILE.match(name) and os.path.isfile(os.path.join(package_dir, name)):
            files.append(os.path.join(package_dir, name))
    return files


def runtime_packages(assets, rid):
    targets = assets["targets"]
    key = f"net10.0/{rid}" if rid and f"net10.0/{rid}" in targets else next(k for k in targets if "/" not in k)
    if rid and "/" not in key:
        print(f"warning: no restore for runtime {rid}; listing packages for all platforms", file=sys.stderr)
    for name, info in sorted(targets[key].items(), key=lambda kv: kv[0].lower()):
        if info.get("type") != "package":
            continue
        package, version = name.split("/")
        if package.lower() in BUILD_ONLY:
            continue
        # DirectML.dll is copied by the app's project file, not through NuGet's asset lists.
        if "/" in key and not ships_files(info) and package.lower() not in COPIED_BY_PROJECT:
            continue  # e.g. macOS native assets in a Linux build: nothing of it is shipped
        yield package, version


def ships_files(info):
    """True if the package puts real files into the build ("_._" marks an empty placeholder)."""
    for kind in ("runtime", "native", "resource"):
        if any(not path.endswith("/_._") for path in info.get(kind, {})):
            return True
    return False


def runtime_pack_dir(assets, rid, packages_root):
    """The Microsoft.NETCore.App runtime pack bundled into a self-contained build."""
    for framework in assets.get("project", {}).get("frameworks", {}).values():
        for dep in framework.get("downloadDependencies", []):
            name = dep.get("name", "")
            if name.lower() == f"microsoft.netcore.app.runtime.{rid}".lower():
                version = dep["version"].strip("[]").split(",")[0]
                path = os.path.join(packages_root, name.lower(), version)
                if os.path.isdir(path):
                    return name, version, path
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", default=os.path.join(ROOT, "src", "MyVideoEditor.App", "obj", "project.assets.json"))
    parser.add_argument("--rid", help="runtime identifier the build targets, e.g. linux-x64 or win-x64")
    parser.add_argument("--output", required=True)
    parser.add_argument("--ffmpeg", help="folder with the bundled FFmpeg (FFMPEG-SOURCE.txt, FFMPEG-LICENSE.txt)")
    parser.add_argument("--upscalers", help="folder with bundled AI upscalers (one subfolder per component, as in the MSIX)")
    args = parser.parse_args()

    assets = json.load(open(args.assets, encoding="utf-8"))
    packages_root = assets.get("project", {}).get("restore", {}).get("packagesPath") or os.path.expanduser("~/.nuget/packages")

    sections = []  # (title, header lines, body)

    def add(title, header, body):
        sections.append((title, [h for h in header if h], body))

    add("MyVideoEditor", ["https://github.com/StuMP90/myvideoeditor", "Licence: MIT"], read(os.path.join(ROOT, "LICENSE")))

    if args.ffmpeg:
        source = os.path.join(args.ffmpeg, "FFMPEG-SOURCE.txt")
        if not os.path.exists(source) or not os.path.exists(os.path.join(args.ffmpeg, "FFMPEG-LICENSE.txt")):
            print(f"error: FFmpeg notices not found in {args.ffmpeg}", file=sys.stderr)
            return 1
        add("FFmpeg (bundled in the ffmpeg folder, run as separate programs)",
            ["https://ffmpeg.org", "Licence: GPL-3.0-or-later (full text: ffmpeg/FFMPEG-LICENSE.txt)"],
            read(source))

    # Upscaling models shipped with every build (src/MyVideoEditor.Core/Upscaling/Models).
    for model in ["artcnn", "realesrgan-models"]:
        body = read(os.path.join(NOTICES, "upscalers", f"{model}.txt"))
        add(body.splitlines()[0].split(":")[0], [], body)

    if args.upscalers:
        bundled = sorted(d for d in os.listdir(args.upscalers) if os.path.exists(os.path.join(args.upscalers, d, ".installed")))
        if not bundled:
            print(f"error: no bundled upscalers found in {args.upscalers}", file=sys.stderr)
            return 1
        for component in bundled + ["ncnn"]:
            text = os.path.join(NOTICES, "upscalers", f"{component}.txt")
            if not os.path.exists(text):
                print(f"error: no licence text for bundled upscaler {component} (expected {text})", file=sys.stderr)
                return 1
            body = read(text)
            add(body.splitlines()[0].split(":")[0] + " (bundled in the upscalers folder)", [], body)

    pack = runtime_pack_dir(assets, args.rid, packages_root) if args.rid else None
    if pack:
        name, version, path = pack
        body = "\n\n".join(read(f) for f in licence_files(path))
        add(f".NET runtime ({name} {version}, bundled with the app)", ["https://github.com/dotnet/runtime", "Licence: MIT"], body)
    else:
        print("warning: .NET runtime pack not found (publish self-contained for --rid first)", file=sys.stderr)

    add("Inter font (bundled by Avalonia.Fonts.Inter)", ["https://github.com/rsms/inter", "Licence: SIL Open Font License 1.1"],
        read(os.path.join(NOTICES, "Inter-font.txt")))

    seen = {}  # licence body hash -> first package that printed it
    missing = []
    for package, version in runtime_packages(assets, args.rid):
        package_dir = os.path.join(packages_root, package.lower(), version.lower())
        if not os.path.isdir(package_dir):
            missing.append(f"{package} {version} (not restored)")
            continue
        info = nuspec_info(package_dir)
        expression = info["expression"] or KNOWN_LICENCES.get(package.lower())
        header = [info["url"], f"Licence: {expression or 'see below'}"]

        override = os.path.join(NOTICES, f"{package}.txt")
        files = licence_files(package_dir)
        if os.path.exists(override):
            body = read(override)  # carries its own copyright lines
        elif files:
            body = "\n\n".join(read(f) for f in files)
        elif expression and standard_text(expression):
            # Standard text: the package's copyright line goes above it.
            copyright = info["copyright"] or info["authors"]
            if copyright and not re.match(r"(?i)(copyright|\(c\)|©)", copyright):
                copyright = f"Copyright (c) {copyright}"
            body = standard_text(expression)
            if copyright:
                body = f"{copyright}\n\n{body}"
        else:
            missing.append(f"{package} {version} ({expression or 'no licence information'})")
            continue

        digest = hashlib.sha256(body.encode()).hexdigest()
        if digest in seen:
            body = f"Same copyright and licence text as {seen[digest]} above."
        else:
            seen[digest] = package
        add(f"{package} {version}", header, body)

    if missing:
        print("error: no licence text for:\n  " + "\n  ".join(missing), file=sys.stderr)
        return 1

    with open(args.output, "w", encoding="utf-8", newline="\n") as out:
        out.write("MyVideoEditor: THIRD-PARTY NOTICES\n\n")
        out.write("MyVideoEditor is released under the MIT License. It includes the software listed below,\n")
        out.write("each under its own licence, reproduced here as those licences require.\n\n")
        out.write("Contents:\n")
        for title, _, _ in sections:
            out.write(f"  - {title}\n")
        for title, header, body in sections:
            out.write(f"\n\n{RULE}\n{title}\n")
            for line in header:
                out.write(f"{line}\n")
            out.write(f"{RULE}\n\n{body}\n")
    print(f"Wrote {args.output} ({len(sections)} components)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
