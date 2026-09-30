#!/usr/bin/env python3
"""Prepare a verified native peer source overlay without modifying upstream."""

import argparse
from functools import lru_cache
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile


PINS = {
    "libdatachannel": "6b1e2e620f1e37f0eafeee702eaea0043cb305fd",
    "libjuice": "b89c792e3612faf2f12cf35bcc56857313a06be3",
    "mbedtls": "068ff080b369adfac81509f9b57b2afabaf82dc5",
}
PROVENANCE = "source-provenance.json"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def io_path(path):
    """Use Win32 extended paths for I/O while retaining ordinary guard paths."""
    if os.name != "nt":
        return path
    absolute = os.path.abspath(path)
    if absolute.startswith("\\\\?\\"):
        return Path(absolute)
    if absolute.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + absolute[2:])
    return Path("\\\\?\\" + absolute)


def normalized(path):
    return io_path(path).read_bytes().decode("utf-8-sig").replace("\r\n", "\n").encode("utf-8")


@lru_cache(maxsize=None)
def check_directory(root, directory):
    if directory == root:
        return
    if root not in directory.parents:
        raise ValueError("Native source directory leaves its intended root")
    check_directory(root, directory.parent)
    directory_io = io_path(directory)
    if directory_io.is_symlink() or (hasattr(directory_io, "is_junction") and directory_io.is_junction()):
        raise ValueError("Native source directories must not be links")


def child(root, relative):
    parts = PurePosixPath(relative)
    if parts.is_absolute() or not parts.parts or ".." in parts.parts or "\\" in relative or ":" in relative:
        raise ValueError("Native source paths must be bounded relative paths")
    result = root.joinpath(*parts.parts)
    check_directory(root, result.parent)
    if io_path(result).is_symlink():
        raise ValueError("Native source files must not be links")
    return result


def git(directory, *args, data=None, isolated=False):
    environment = os.environ.copy()
    if isolated:
        # A build overlay can be inside the application checkout. Do not let
        # git apply discover that ancestor as its working tree.
        environment["GIT_CEILING_DIRECTORIES"] = str(directory.parent)
    result = subprocess.run(
        ["git", "-C", str(directory), *args], input=data, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, env=environment, check=False,
    )
    if result.returncode:
        raise ValueError("Native source Git operation failed: " + result.stderr.decode("utf-8", "replace").strip())
    return result.stdout


def load_inputs(source, manifest_path):
    manifest = json.loads(io_path(manifest_path).read_text(encoding="utf-8-sig"))
    if manifest.get("version") != 1 or manifest.get("pins") != PINS:
        raise ValueError("Unsupported native peer patch version or upstream pins")
    provenance = []
    for name, key in (("libdatachannel", "libdatachannel"),
                      ("libdatachannel/deps/libjuice", "libjuice"), ("mbedtls", "mbedtls")):
        directory = child(source, name)
        revision = git(directory, "rev-parse", "HEAD").decode().strip()
        if revision != PINS[key]:
            raise ValueError("Unexpected pinned native source revision: " + name)
        if git(directory, "status", "--porcelain", "--untracked-files=no").strip():
            raise ValueError("Modified upstream native source; preserving it: " + name)
        submodules = git(directory, "submodule", "status", "--recursive").decode().splitlines()
        if any(line and line[0] in "-+U" for line in submodules):
            raise ValueError("Pinned native submodules are incomplete: " + name)
        provenance.append({"name": name, "commit": revision, "submodules": submodules})
    patches = []
    for entry in manifest["patches"]:
        patch_path = child(manifest_path.parent, entry["patch"])
        patch_data = normalized(patch_path)
        if digest(patch_data) != entry["patchSha256"]:
            raise ValueError("Native peer patch digest mismatch: " + entry["patch"])
        input_path = child(source, entry["source"])
        if digest(normalized(input_path)) != entry["beforeSha256"]:
            raise ValueError("Unexpected pinned native source digest: " + entry["source"])
        patches.append((entry, patch_data))
    return manifest, provenance, patches


def collect_files(source):
    files = {}
    for name in ("libdatachannel", "mbedtls"):
        directory = source / name
        for item in git(directory, "ls-files", "-z", "--recurse-submodules").split(b"\0"):
            if not item:
                continue
            relative = name + "/" + os.fsdecode(item)
            path = child(source, relative)
            path_io = io_path(path)
            if path_io.is_symlink() or not path_io.is_file():
                raise ValueError("Tracked native source must be a regular file: " + relative)
            files[relative] = digest(path_io.read_bytes())
    return files


def verify_overlay(output, expected_files, manifest_digest, provenance):
    output_io = io_path(output)
    record_path = output_io / PROVENANCE
    if not record_path.is_file() or record_path.is_symlink():
        raise ValueError("Existing source overlay has no valid provenance; preserving it")
    record = json.loads(record_path.read_text(encoding="utf-8"))
    if (record.get("version") != 1 or record.get("patchManifestSha256") != manifest_digest
            or record.get("sourcePins") != provenance or record.get("files") != expected_files):
        raise ValueError("Existing source overlay provenance differs; preserving it")
    actual = set()
    for path in output_io.rglob("*"):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise ValueError("Existing source overlay contains a link; preserving it")
        if not path.is_file():
            continue
        relative = path.relative_to(output_io).as_posix()
        if relative == PROVENANCE:
            continue
        actual.add(relative)
        if relative not in expected_files or digest(path.read_bytes()) != expected_files[relative]:
            raise ValueError("Existing source overlay was modified; preserving it: " + relative)
    if actual != set(expected_files):
        raise ValueError("Existing source overlay is incomplete; preserving it")


def prepare(source, output, manifest_path, verify_only=False):
    source, output, manifest_path = source.resolve(), output.resolve(), manifest_path.resolve()
    if source == output or source in output.parents or output in source.parents:
        raise ValueError("Native source overlay must be separate from upstream source")
    manifest, provenance, patches = load_inputs(source, manifest_path)
    print("Validated pinned upstream and native patch inputs", flush=True)
    files = collect_files(source)
    print("Verified " + str(len(files)) + " tracked native source files", flush=True)
    expected = dict(files)
    for entry, _ in patches:
        expected[entry["source"]] = entry["afterSha256"]
    manifest_digest = digest(normalized(manifest_path))
    if io_path(output).exists():
        verify_overlay(output, expected, manifest_digest, provenance)
        print("Verified existing native peer source overlay: " + str(output))
        return
    if verify_only:
        raise ValueError("Native peer source overlay does not exist")
    io_path(output.parent).mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix="." + output.name + ".preparing-", dir=output.parent)).resolve()
    if staging.parent != output.parent:
        raise ValueError("Native source staging directory leaves its intended build directory")
    try:
        for relative in files:
            target = child(staging, relative)
            io_path(target.parent).mkdir(parents=True, exist_ok=True)
            shutil.copy2(io_path(child(source, relative)), io_path(target))
        for entry, patch_data in patches:
            target = child(staging, entry["source"])
            io_path(target).write_bytes(normalized(target))
            git(staging, "apply", "--check", "--whitespace=nowarn", "-", data=patch_data, isolated=True)
            git(staging, "apply", "--whitespace=nowarn", "-", data=patch_data, isolated=True)
            # Global Git autocrlf settings may rewrite a patched file. Store the
            # canonical bytes recorded in the portable patch manifest.
            io_path(target).write_bytes(normalized(target))
            if digest(io_path(target).read_bytes()) != entry["afterSha256"]:
                raise ValueError("Native peer patch output digest mismatch: " + entry["source"])
        record = {"version": 1, "originalSourceRoot": str(source), "sourcePins": provenance,
                  "patchManifestSha256": manifest_digest, "files": expected}
        io_path(staging / PROVENANCE).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
        verify_overlay(staging, expected, manifest_digest, provenance)
        if io_path(output).exists():
            raise ValueError("Native source overlay appeared during preparation; preserving both trees")
        io_path(staging).rename(io_path(output))
    except Exception as error:
        raise ValueError(str(error) + "; retained isolated staging: " + str(staging)) from error
    print("Prepared verified native peer source overlay: " + str(output))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, default=Path(__file__).resolve().parent.parent / "native/peer/patches/manual-signaling-v1.json")
    parser.add_argument("--verify-only", action="store_true")
    options = parser.parse_args()
    try:
        prepare(options.source_root, options.output, options.manifest, options.verify_only)
    except (OSError, ValueError, KeyError) as error:
        print("Native peer source preparation failed: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
