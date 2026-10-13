#!/usr/bin/env python3
"""Export one explicit, accepted source commit; never commit or push."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import tempfile

ALLOWLIST = "packaging/public-source-allowlist.v1.json"
INPUT_LOCK = "data/dictionary/public-input-lock.v1.json"
MANIFEST = "public-source-manifest.json"
SCHEMA = "bglang.public-sync-manifest.v1"
PUBLIC_ONLY = {
    "PREVIEW.md", "PUBLIC-SYNC.md", "tools/sync_public_snapshot.py",
    "packaging/public-readme-prefix.md", "packaging/Get-PublicInputs.ps1",
    "packaging/public-delivery.json",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def parse_json(data):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON key: " + key)
            result[key] = value
        return result
    return json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique)


def safe_relative(value):
    require(isinstance(value, str) and value, "Empty/non-string path")
    require("\\" not in value and ":" not in value, "Unsafe path: " + value)
    parts = value.split("/")
    for part in parts:
        require(part not in ("", ".", "..") and part.lower() != ".git",
                "Unsafe path: " + value)
        require(not part.endswith((".", " ")) and not any(ord(c) < 32 for c in part),
                "Unsafe path: " + value)
        require(not any(c in part for c in '<>"|?*'), "Unsafe path: " + value)
        require(not re.fullmatch(r"(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part, re.I),
                "Reserved path: " + value)
    require(not PurePosixPath(value).is_absolute(), "Absolute path")
    return value


def consistent_path_casing(paths):
    spellings = {}
    for path in paths:
        parts = safe_relative(path).split("/")
        for length in range(1, len(parts) + 1):
            prefix = "/".join(parts[:length])
            previous = spellings.setdefault(prefix.casefold(), prefix)
            require(previous == prefix,
                    "Inconsistent path component casing: " + previous + " / " + prefix)


def path_set(paths):
    result = [safe_relative(p) for p in paths]
    folded = [p.casefold() for p in result]
    require(len(folded) == len(set(folded)), "Duplicate/case-colliding paths")
    consistent_path_casing(result)
    names = set(folded)
    for name in names:
        require(not any("/".join(name.split("/")[:i]) in names
                        for i in range(1, len(name.split("/")))), "File/directory path collision")
    return set(result)


def check_plain(path, directory=None):
    info = path.lstat()
    require(not stat.S_ISLNK(info.st_mode) and not (
        getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)),
        "Symlink/reparse point: " + str(path))
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode),
            "Unexpected filesystem object: " + str(path))


def plain_root(value):
    root = Path(os.path.abspath(value))
    for parent in reversed([root, *root.parents]):
        check_plain(parent, directory=True)
    return root.resolve(strict=True)


def git(source, *args):
    env = os.environ.copy()
    env["GIT_OPTIONAL_LOCKS"] = "0"
    env["GIT_TERMINAL_PROMPT"] = "0"
    env["GIT_NO_REPLACE_OBJECTS"] = "1"
    for key in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR",
                "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"):
        env.pop(key, None)
    run = subprocess.run(["git", "--no-pager", "--literal-pathspecs", "-C", str(source), *args],
                         capture_output=True, env=env, check=False)
    require(run.returncode == 0, "Git read failed: " + run.stderr.decode("utf-8", errors="replace"))
    return run.stdout


def source_payload(source, commit):
    require(re.fullmatch(r"[0-9a-fA-F]{40}|[0-9a-fA-F]{64}", commit) is not None,
            "--commit must be a full commit object ID; HEAD/branches/abbreviations are refused")
    resolved = git(source, "rev-parse", "--verify", commit + "^{commit}").decode().strip()
    require(resolved == commit.lower(), "Commit does not resolve to the exact supplied object ID")
    tree = {}
    for entry in git(source, "ls-tree", "-rz", "--full-tree", resolved).split(b"\0"):
        if entry:
            metadata, path = entry.split(b"\t", 1)
            mode, kind, oid = metadata.decode("ascii").split()
            tree[path.decode("utf-8")] = (mode, kind, oid)

    def blob(path):
        require(path in tree, "Missing source path: " + path)
        mode, kind, oid = tree[path]
        require(kind == "blob" and mode in ("100644", "100755"),
                "Source symlink/gitlink/non-file: " + path)
        return git(source, "cat-file", "blob", oid)

    allow_bytes = blob(ALLOWLIST)
    allow = parse_json(allow_bytes)
    require(allow.get("schema") == "bglang.public-source-allowlist.v1", "Wrong allowlist schema")
    files = path_set(allow["files"])
    require({ALLOWLIST, INPUT_LOCK, ".gitattributes", "README.md"} <= files,
            "Required public contract files are absent from the allowlist")
    require(MANIFEST not in files, "Generated manifest cannot be source-managed")
    payload = {path: blob(path) for path in sorted(files)}
    rows = {path: {"path": path, "sourceBlob": tree[path][2],
                  "sourceBytes": len(data), "sourceSha256": hashlib.sha256(data).hexdigest(),
                  "checkoutTransform": "git-blob"} for path, data in payload.items()}
    lock = parse_json(payload[INPUT_LOCK])
    require(lock.get("schema") == "bglang.public-input-lock.v1", "Wrong input-lock schema")
    for label in ("graph", "independentLiteralFixture"):
        pin = lock[label]
        path = safe_relative(pin["releaseRelativePath"])
        require(path in payload, "Locked file is not allowlisted: " + path)
        if label == "graph" and digest(payload[path]) != {"bytes": pin["bytes"], "sha256": pin["sha256"]}:
            lines = payload[".gitattributes"].decode("utf-8-sig").splitlines()
            literal_rule = any(line.split() and line.split()[0] == path and
                               "eol=crlf" in line.split()[1:] for line in lines)
            attr = git(source, "check-attr", "--source=" + resolved, "-z", "eol", "--", path)
            require(literal_rule and attr == (path + "\0eol\0crlf\0").encode(),
                    "Graph RAW mismatch without explicit committed eol=crlf")
            require(b"\r" not in payload[path], "Graph is not canonical LF; refusing conversion")
            payload[path] = payload[path].replace(b"\n", b"\r\n")
            rows[path]["checkoutTransform"] = "lf-to-crlf-locked"
        require(digest(payload[path]) == {"bytes": pin["bytes"], "sha256": pin["sha256"]},
                "Exact RAW contract mismatch: " + label)
    return resolved, payload, rows, {"path": ALLOWLIST, "sourceBlob": tree[ALLOWLIST][2], **digest(allow_bytes)}


def target_files(target, directory_spellings=None):
    def enumeration_failed(error):
        raise error
    result = {}
    directories = set()
    for directory, dirs, files in os.walk(target, followlinks=False, onerror=enumeration_failed):
        for name in list(dirs):
            child = Path(directory) / name
            check_plain(child, directory=True)
            if child == target / ".git":
                dirs.remove(name)
            else:
                directories.add(safe_relative(child.relative_to(target).as_posix()))
        for name in files:
            child = Path(directory) / name
            check_plain(child)
            relative = safe_relative(child.relative_to(target).as_posix())
            result[relative] = child.read_bytes()
    path_set(result)
    consistent_path_casing([*result, *directories])
    if directory_spellings is not None:
        directory_spellings.update(directories)
    return result


def apply_transaction(target, before, after, removed):
    """Validation is complete. Roll back ordinary write failures; manifest is written last."""
    changed = {p: b for p, b in after.items() if before.get(p) != b}
    if not changed and not removed:
        return
    original = {p: before.get(p) for p in set(changed) | removed}
    created_dirs = []
    touched = []
    # Staged files live on the same volume for atomic individual-file replacement.
    with tempfile.TemporaryDirectory(prefix="bglang-sync-", dir=target.parent) as staging:
        staged = {}
        for index, (path, data) in enumerate(changed.items()):
            staged[path] = Path(staging) / str(index)
            staged[path].write_bytes(data)
        try:
            # Remove only prevalidated old managed files; the manifest commits the plan last.
            operations = [(p, False) for p in sorted(changed) if p != MANIFEST]
            operations += [(p, True) for p in sorted(removed)]
            if MANIFEST in changed:
                operations.append((MANIFEST, False))
            for path, remove in operations:
                dest = target / path
                if remove:
                    dest.unlink()
                    touched.append(path)
                    continue
                absent = []
                parent = dest.parent
                while not parent.exists():
                    absent.append(parent)
                    parent = parent.parent
                for parent in reversed(absent):
                    parent.mkdir()
                    created_dirs.append(parent)
                os.replace(staged[path], dest)
                touched.append(path)
        except Exception as original_error:
            recovery_errors = []
            for index, path in enumerate(reversed(touched)):
                dest = target / path
                try:
                    if original[path] is None:
                        dest.unlink()
                    else:
                        restore = Path(staging) / ("restore-" + str(index))
                        restore.write_bytes(original[path])
                        os.replace(restore, dest)
                except OSError as recovery_error:
                    recovery_errors.append(path + ": " + str(recovery_error))
            for parent in reversed(created_dirs):
                if parent.exists() and not any(parent.iterdir()):
                    parent.rmdir()
            if recovery_errors:
                raise OSError("Write failed; manual recovery required: " + "; ".join(recovery_errors)) from original_error
            raise


def synchronize(args):
    source, target = plain_root(args.source), plain_root(args.target)
    actual_source = Path(git(source, "rev-parse", "--show-toplevel").decode("utf-8").rstrip("\r\n"))
    require(os.path.normcase(str(actual_source)) == os.path.normcase(str(source)),
            "--source must name the source repository root")
    try:
        common = os.path.normcase(os.path.commonpath([source, target]))
    except ValueError:  # Different Windows volumes.
        common = ""
    require(common not in (os.path.normcase(str(source)), os.path.normcase(str(target))),
            "Source and target must not overlap or contain one another")
    require((target / ".git").is_dir() or not any(target.iterdir()) or (target / MANIFEST).is_file(),
            "Target must be an empty directory or independently initialized Git checkout")
    if (target / ".git").exists():
        check_plain(target / ".git", directory=True)
        actual_target = Path(git(target, "rev-parse", "--show-toplevel").decode("utf-8").rstrip("\r\n"))
        require(os.path.normcase(str(actual_target)) == os.path.normcase(str(target)), "Target Git root differs")
        require(not git(target, "diff", "--cached", "--name-only", "-z"),
                "Target has staged changes; finish or unstage them before synchronization")
    before_directories = set()
    before = target_files(target, before_directories)
    previous = parse_json(before[MANIFEST]) if MANIFEST in before else None
    old_rows = {}
    public_only = set(PUBLIC_ONLY)
    if previous:
        require(previous.get("schema") == SCHEMA, "Unrecognized existing source manifest; refusing adoption")
        paths = path_set(row["path"] for row in previous["files"])
        old_rows = {row["path"]: row for row in previous["files"]}
        require(len(old_rows) == len(paths), "Duplicate previous manifest entries")
        public_only.update(path_set(row["path"] for row in previous.get("publicOnlyFiles", [])))
        for path, row in old_rows.items():
            require(path in before and digest(before[path]) == {"bytes": row["bytes"], "sha256": row["sha256"]},
                    "Managed target file was edited/deleted: " + path)
    public_only.update(path_set(args.public_only))
    prefix = None
    explicit_prefix = args.readme_prefix_file is not None
    prefix_path = args.readme_prefix_file or (previous or {}).get("readmePrefix", {}).get("path")
    if prefix_path:
        prefix_path = safe_relative(prefix_path)
        public_only.add(prefix_path)
        require(prefix_path in before, "Missing public README prefix file")
        before[prefix_path].decode("utf-8")
        prefix = {"path": prefix_path, **digest(before[prefix_path])}
        if previous and previous.get("readmePrefix") and not explicit_prefix:
            require(prefix == previous["readmePrefix"],
                    "README prefix changed; review it and pass --readme-prefix-file explicitly")
    resolved, payload, rows, allow_pin = source_payload(source, args.commit)
    require(not (set(payload) & public_only), "Source/public-only path conflict")
    path_set([*payload, *public_only, MANIFEST])
    path_set(set(payload) | set(old_rows) | public_only | {MANIFEST})
    consistent_path_casing([*payload, *old_rows, *public_only, MANIFEST,
                            *before, *before_directories])
    unknown = set(before) - set(old_rows) - public_only - {MANIFEST}
    require(not unknown, "Unmanaged target files: " + ", ".join(sorted(unknown)))
    require(not (set(payload) - set(old_rows)) & set(before), "New managed path already exists")
    for path in payload:
        dest = target / path
        require(not dest.exists() or dest.is_file(), "Managed destination is a directory: " + path)
        parent = dest.parent
        while parent != target:
            require(not parent.exists() or parent.is_dir(), "Managed parent is a file: " + path)
            parent = parent.parent
    if prefix:
        payload["README.md"] = before[prefix_path] + payload["README.md"]
        rows["README.md"]["checkoutTransform"] = "public-readme-prefix"
    for path, data in payload.items():
        rows[path].update(digest(data))
    public_rows = [{"path": path, **digest(before[path])} for path in sorted(public_only) if path in before]
    manifest = {"schema": SCHEMA, "sourceCommit": resolved, "allowlist": allow_pin,
                "readmePrefix": prefix or {}, "files": [rows[p] for p in sorted(rows)],
                "publicOnlyFiles": public_rows,
                "policy": "Explicit source snapshot only. No source history, package inputs, commit, push or build performed."}
    payload[MANIFEST] = json_bytes(manifest)
    removed = set(old_rows) - set(rows) - public_only
    # Detect another writer between planning and application. Concurrent editors remain unsupported.
    current_directories = set()
    require(target_files(target, current_directories) == before and
            current_directories == before_directories, "Target changed while preparing the snapshot")
    apply_transaction(target, before, payload, removed)
    after = target_files(target)
    require(all(after.get(p) == b for p, b in payload.items()) and not (removed & set(after)),
            "Post-write verification failed")
    return {"sourceCommit": resolved, "managedFiles": len(rows), "removedFiles": len(removed),
            "publicOnlyFiles": len(public_rows), "manifest": MANIFEST, **digest(payload[MANIFEST])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--public-only", action="append", default=[], metavar="RELATIVE_PATH")
    parser.add_argument("--readme-prefix-file", metavar="RELATIVE_PATH")
    args = parser.parse_args()
    try:
        print(json.dumps(synchronize(args), ensure_ascii=False))
    except (ValueError, OSError, KeyError, TypeError, UnicodeError) as error:
        print("Snapshot synchronization refused: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
