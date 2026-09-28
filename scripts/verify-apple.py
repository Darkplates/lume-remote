"""Validate built Apple bundles using Xcode tools; never marks device QA passed."""
import argparse
import hashlib
import json
import platform
import plistlib
import subprocess
from datetime import datetime, timezone
from pathlib import Path


def run(*args):
    return subprocess.check_output(args, text=True, stderr=subprocess.STDOUT).strip()


def verify(root, mode):
    if platform.system() != "Darwin":
        raise RuntimeError("Apple artifact verification requires macOS and Xcode tools")
    checked = []

    def binary(path, architectures):
        found = set(run("xcrun", "lipo", "-archs", str(path)).split())
        if found != set(architectures):
            raise ValueError(f"Unexpected architectures in {path.name}: {sorted(found)}")
        dependencies = run("xcrun", "otool", "-L", str(path))
        for line in dependencies.splitlines():
            value = line.strip().split(" (", 1)[0]
            if " (" not in line:
                continue  # Architecture/file headings.
            if not value.startswith(("/System/Library/", "/usr/lib/", "@rpath/", "@loader_path/", "@executable_path/")):
                raise ValueError(f"Nonportable dependency in {path.name}: {value}")
        checked.append({"path": str(path.relative_to(root)), "architectures": sorted(found),
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})

    if mode != "ios":
        app = root / "Lume.app"
        info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
        if info.get("LSMinimumSystemVersion") != "13.0":
            raise ValueError("Unexpected macOS deployment target")
        for key in ("NSMicrophoneUsageDescription", "NSAudioCaptureUsageDescription", "NSLocalNetworkUsageDescription"):
            if not info.get(key):
                raise ValueError(f"Missing macOS privacy description: {key}")
        for name in ("Lume", "libdatachannel.dylib", "lume-audio", "lume-print"):
            binary(app / "Contents/MacOS" / name, ("arm64", "x86_64"))
        run("codesign", "--verify", "--deep", "--strict", str(app))
    if mode != "macos":
        app = root / "DerivedData/Build/Products/Debug-iphonesimulator/Lume.app"
        info = plistlib.loads((app / "Info.plist").read_bytes())
        if info.get("CADisableMinimumFrameDurationOnPhone") is not True:
            raise ValueError("The high-refresh presentation setting is missing")
        for key in ("NSMicrophoneUsageDescription", "NSLocalNetworkUsageDescription"):
            if not info.get(key):
                raise ValueError(f"Missing iOS privacy description: {key}")
        binary(app / "Lume", ("arm64", "x86_64"))
        peer = app / "Frameworks/LumePeer.framework/LumePeer"
        binary(peer, ("arm64", "x86_64"))
    return {"status": "passed", "mode": mode, "checked_utc": datetime.now(timezone.utc).isoformat(), "binaries": checked,
            "scope": "Build, dependency paths, architectures, privacy metadata and macOS ad-hoc signature only",
            "unverified": ["physical devices", "screen/input/audio permission flows", "WAN", "notarization", "distribution signing"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--mode", choices=("all", "macos", "ios"), default="all")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.root.resolve(), args.mode)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print("PASS Apple bundle verification. Device execution remains unverified.")
