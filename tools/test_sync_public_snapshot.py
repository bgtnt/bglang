"""Synthetic Git repositories only; no Product or network operations."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import sync_public_snapshot as sync


GRAPH = "data/word-formation/word-formation-source.v0.json"
FIXTURE = "tests/BgLang.Public.Tests/Fixtures/public-literal-readings.v1.json"


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="sync-test-", dir=Path(__file__).parent)
        self.root = Path(self.temp.name)
        self.source, self.target = self.root / "source", self.root / "public"
        self.source.mkdir()
        self.target.mkdir()
        self.git("init", "-q")
        self.git("config", "user.name", "Synthetic Test")
        self.git("config", "user.email", "synthetic@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "commit.gpgsign", "false")
        self.graph_raw = b'{\r\n  "graph": "synthetic"\r\n}\r\n'
        self.fixture = b'{"literal": "independent"}\n'
        self.files = [sync.ALLOWLIST, sync.INPUT_LOCK, ".gitattributes", "README.md", GRAPH, FIXTURE, "old.txt"]
        self.write(".gitattributes", (GRAPH + " text eol=crlf\n").encode())
        self.write("README.md", b"# Synthetic source\n")
        self.write(GRAPH, self.graph_raw.replace(b"\r\n", b"\n"))
        self.write(FIXTURE, self.fixture)
        self.write("old.txt", b"old tracked content\n")
        self.write(sync.INPUT_LOCK, sync.json_bytes({"schema": "bglang.public-input-lock.v1",
                   "graph": {"releaseRelativePath": GRAPH, **sync.digest(self.graph_raw)},
                   "independentLiteralFixture": {"releaseRelativePath": FIXTURE, **sync.digest(self.fixture)}}))
        self.commit = self.commit_files()

    def tearDown(self):
        # git may write read-only loose objects; chmod only our synthetic directory.
        def onerror(func, path, info):
            os.chmod(path, 0o700)
            func(path)
        import shutil
        shutil.rmtree(self.root, onerror=onerror)
        self.temp.cleanup()

    def git(self, *args, input=None):
        return subprocess.run(["git", "-C", str(self.source), *args], input=input,
                              capture_output=True, check=True).stdout

    def write(self, path, data):
        output = self.source / path
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(data)

    def commit_files(self):
        self.write(sync.ALLOWLIST, sync.json_bytes({"schema": "bglang.public-source-allowlist.v1", "files": self.files}))
        self.git("add", "--all")
        self.git("commit", "-q", "-m", "Synthetic snapshot")
        return self.git("rev-parse", "HEAD").decode().strip()

    def args(self, **changes):
        values = dict(source=str(self.source), target=str(self.target), commit=self.commit,
                      public_only=[], readme_prefix_file=None)
        values.update(changes)
        return argparse.Namespace(**values)

    def initialize_target(self):
        subprocess.run(["git", "-C", str(self.target), "init", "-q"], capture_output=True, check=True)

    def target_snapshot(self):
        return sync.target_files(self.target)

    def rejected_unchanged(self, message=None, **args):
        before = self.target_snapshot()
        with self.assertRaises(ValueError) as caught:
            sync.synchronize(self.args(**args))
        if message:
            self.assertIn(message, str(caught.exception))
        self.assertEqual(before, self.target_snapshot())

    def test_exact_commit_dirty_source_prefix_and_idempotence(self):
        self.initialize_target()
        (self.target / "PREVIEW.md").write_bytes(b"Public-only review notes\n")
        (self.target / "packaging").mkdir()
        (self.target / "packaging/public-readme-prefix.md").write_bytes(b"# Preview\n\n")
        self.write("README.md", b"UNACCEPTED working tree\n")
        self.write("private-secret.txt", b"synthetic secret must remain private\n")
        self.git("add", "private-secret.txt")
        status = self.git("status", "--porcelain=v1", "-z")
        index = (self.source / ".git/index").read_bytes()
        result = sync.synchronize(self.args(readme_prefix_file="packaging/public-readme-prefix.md"))
        self.assertEqual(result["managedFiles"], 7)
        self.assertEqual((self.target / GRAPH).read_bytes(), self.graph_raw)
        self.assertEqual((self.target / FIXTURE).read_bytes(), self.fixture)
        self.assertEqual((self.target / "README.md").read_bytes(), b"# Preview\n\n# Synthetic source\n")
        self.assertFalse((self.target / "private-secret.txt").exists())
        manifest = (self.target / sync.MANIFEST).read_bytes()
        self.assertNotIn(str(self.source).encode(), manifest)
        before = self.target_snapshot()
        timestamps = {p: (self.target / p).stat().st_mtime_ns for p in before}
        sync.synchronize(self.args())
        self.assertEqual(before, self.target_snapshot())
        self.assertEqual(timestamps, {p: (self.target / p).stat().st_mtime_ns for p in before})
        self.assertEqual(status, self.git("status", "--porcelain=v1", "-z"))
        self.assertEqual(index, (self.source / ".git/index").read_bytes())

    def test_successor_add_update_delete_and_public_only_retention(self):
        self.initialize_target()
        (self.target / "PREVIEW.md").write_bytes(b"keep me\n")
        sync.synchronize(self.args())
        self.files.remove("old.txt")
        self.files.append("new.txt")
        self.write("new.txt", b"new\n")
        self.write("README.md", b"# Accepted successor\n")
        self.commit = self.commit_files()
        sync.synchronize(self.args())
        self.assertFalse((self.target / "old.txt").exists())
        self.assertEqual((self.target / "new.txt").read_bytes(), b"new\n")
        self.assertEqual((self.target / "PREVIEW.md").read_bytes(), b"keep me\n")

    def test_changed_managed_file_prevents_every_write(self):
        sync.synchronize(self.args())
        (self.target / "old.txt").write_bytes(b"public correction\n")
        self.rejected_unchanged("edited/deleted")

    def test_deleted_managed_file_prevents_every_write(self):
        sync.synchronize(self.args())
        (self.target / "old.txt").unlink()
        self.rejected_unchanged("edited/deleted")

    def test_unknown_extra_prevents_every_write(self):
        self.initialize_target()
        (self.target / "unexpected.bin").write_bytes(b"do not delete")
        self.rejected_unchanged("Unmanaged target files")

    def test_unreadable_directory_inventory_fails_closed(self):
        def unreadable(*args, **kwargs):
            kwargs["onerror"](PermissionError("Synthetic unreadable directory"))
        with patch.object(sync.os, "walk", unreadable):
            with self.assertRaisesRegex(PermissionError, "unreadable"):
                sync.target_files(self.target)

    def test_traversal_and_git_paths_rejected(self):
        for path in ("../escape.txt", "/absolute", "C:/escape", ".git/config", "a/../b", "nul.txt"):
            with self.subTest(path=path):
                with self.assertRaises(ValueError):
                    sync.safe_relative(path)

    def test_case_collision_in_allowlist_rejected(self):
        self.files.append("readme.md")
        self.commit = self.commit_files()
        self.rejected_unchanged("case-colliding")

    def commit_target_baseline(self):
        subprocess.run(["git", "-C", str(self.target), "add", "--all"], capture_output=True, check=True)
        subprocess.run(["git", "-C", str(self.target), "-c", "user.name=Synthetic Test",
                        "-c", "user.email=synthetic@example.invalid", "-c", "commit.gpgsign=false",
                        "commit", "-q", "-m", "Public test baseline"], capture_output=True, check=True)

    def full_target_state(self):
        directories = sorted(p.relative_to(self.target).as_posix() for p in self.target.rglob("*")
                             if p.is_dir() and p.relative_to(self.target).parts[0] != ".git")
        return {"files": self.target_snapshot(), "directorySpellings": directories,
                "index": (self.target / ".git/index").read_bytes(),
                "manifest": (self.target / sync.MANIFEST).read_bytes()}

    def casing_rejected_without_any_mutation(self):
        before = self.full_target_state()
        with patch.object(sync, "apply_transaction", side_effect=AssertionError("Apply must not start")) as apply:
            with self.assertRaisesRegex(ValueError, "component casing"):
                sync.synchronize(self.args())
            apply.assert_not_called()
        self.assertEqual(before, self.full_target_state())

    def test_different_filename_directory_prefix_casing_fails_before_apply(self):
        self.initialize_target()
        sync.synchronize(self.args())
        self.commit_target_baseline()
        extras = {"Dir/a.txt": b"A\n", "dir/b.txt": b"B\n"}
        self.files.extend(extras)
        self.write("README.md", b"Changed README must not be applied\n")
        self.write(sync.ALLOWLIST, sync.json_bytes({"schema": "bglang.public-source-allowlist.v1", "files": self.files}))
        self.git("add", sync.ALLOWLIST, "README.md")
        # Build the exact case-inconsistent Git tree even on case-insensitive Windows.
        for path, data in extras.items():
            oid = self.git("hash-object", "-w", "--stdin", input=data).decode().strip()
            self.git("update-index", "--add", "--cacheinfo", "100644," + oid + "," + path)
        self.git("commit", "-q", "-m", "Conflicting directory prefixes")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()
        self.casing_rejected_without_any_mutation()

    def test_existing_empty_directory_casing_fails_before_apply(self):
        self.initialize_target()
        sync.synchronize(self.args())
        self.commit_target_baseline()
        (self.target / "TOOLS").mkdir()
        self.files.append("tools/a.py")
        self.write("tools/a.py", b"print('synthetic')\n")
        self.write("README.md", b"Changed README must not be applied\n")
        self.commit = self.commit_files()
        self.casing_rejected_without_any_mutation()

    def test_target_inside_source_even_drive_case_change(self):
        child = self.source / "public-child"
        child.mkdir()
        target = str(child)
        if os.name == "nt":
            target = target[0].lower() + target[1:]
        self.rejected_unchanged("must not overlap", target=target)

    def test_source_inside_target_rejected(self):
        self.rejected_unchanged("must not overlap", target=str(self.root))

    def test_source_subdirectory_rejected(self):
        self.rejected_unchanged("source repository root", source=str(self.source / "data"))

    def test_full_object_id_required(self):
        for name in ("HEAD", self.commit[:12], self.commit + "~1"):
            with self.subTest(name=name):
                self.rejected_unchanged("full commit object ID", commit=name)

    def test_source_symlink_mode_rejected(self):
        self.files.append("symbolic-link")
        self.write(sync.ALLOWLIST, sync.json_bytes({"schema": "bglang.public-source-allowlist.v1", "files": self.files}))
        self.git("add", sync.ALLOWLIST)
        oid = self.git("hash-object", "-w", "--stdin", input=b"../../private\n").decode().strip()
        self.git("update-index", "--add", "--cacheinfo", "120000," + oid + ",symbolic-link")
        self.git("commit", "-q", "-m", "Synthetic symlink")
        self.commit = self.git("rev-parse", "HEAD").decode().strip()
        self.rejected_unchanged("Source symlink/gitlink")

    def test_target_junction_or_symlink_rejected(self):
        self.initialize_target()
        outside = self.root / "outside"
        outside.mkdir()
        link = self.target / "linked"
        if os.name == "nt":
            command = "New-Item -ItemType Junction -Path '" + str(link) + "' -Target '" + str(outside) + "' | Out-Null"
            subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, check=True)
        else:
            link.symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "Symlink/reparse"):
            sync.synchronize(self.args())
        self.assertEqual(list(outside.iterdir()), [])
        link.rmdir() if os.name == "nt" else link.unlink()

    def test_prefix_change_requires_explicit_selection(self):
        self.initialize_target()
        (self.target / "packaging").mkdir()
        prefix = self.target / "packaging/public-readme-prefix.md"
        prefix.write_bytes(b"Preview version1\n\n")
        sync.synchronize(self.args(readme_prefix_file="packaging/public-readme-prefix.md"))
        prefix.write_bytes(b"Preview version2\n\n")
        self.rejected_unchanged("prefix changed")
        sync.synchronize(self.args(readme_prefix_file="packaging/public-readme-prefix.md"))
        self.assertTrue((self.target / "README.md").read_bytes().startswith(b"Preview version2\n\n"))

    def test_literal_fixture_mismatch_never_normalized(self):
        self.write(FIXTURE, self.fixture.replace(b"\n", b"\r\n"))
        self.commit = self.commit_files()
        self.rejected_unchanged("independentLiteralFixture")

    def test_graph_crlf_requires_explicit_attribute(self):
        self.write(".gitattributes", b"# No graph EOL contract\n")
        self.commit = self.commit_files()
        self.rejected_unchanged("explicit committed eol=crlf")

    def test_replacement_objects_cannot_substitute_accepted_commit(self):
        accepted = self.commit
        self.write("README.md", b"# Replacement must never export\n")
        replacement = self.commit_files()
        self.git("replace", accepted, replacement)
        sync.synchronize(self.args(commit=accepted))
        self.assertEqual((self.target / "README.md").read_bytes(), b"# Synthetic source\n")

    def test_write_failure_rolls_back_whole_snapshot(self):
        sync.synchronize(self.args())
        before = self.target_snapshot()
        self.write("README.md", b"# Changed\n")
        self.write("old.txt", b"new old content\n")
        self.commit = self.commit_files()
        real_replace = sync.os.replace
        count = 0

        def fail_second(*args):
            nonlocal count
            count += 1
            if count == 2:
                raise OSError("Synthetic interrupted write")
            return real_replace(*args)

        with patch.object(sync.os, "replace", fail_second):
            with self.assertRaisesRegex(OSError, "Synthetic interrupted write"):
                sync.synchronize(self.args())
        self.assertEqual(before, self.target_snapshot())

    def test_persistent_second_path_failure_restores_prior_write(self):
        sync.synchronize(self.args())
        before = self.target_snapshot()
        self.write("README.md", b"# Changed\n")
        self.write("old.txt", b"changed content\n")
        self.commit = self.commit_files()
        real_replace = sync.os.replace

        def fail_old(source, destination):
            if Path(destination) == self.target / "old.txt":
                raise PermissionError("Persistent path-specific denial")
            return real_replace(source, destination)

        with patch.object(sync.os, "replace", fail_old):
            with self.assertRaisesRegex(PermissionError, "Persistent"):
                sync.synchronize(self.args())
        self.assertEqual(before, self.target_snapshot())

    def test_staged_public_changes_prevent_writes(self):
        self.initialize_target()
        sync.synchronize(self.args())
        subprocess.run(["git", "-C", str(self.target), "add", "README.md"], capture_output=True, check=True)
        index = (self.target / ".git/index").read_bytes()
        self.rejected_unchanged("staged changes")
        self.assertEqual(index, (self.target / ".git/index").read_bytes())

    def test_removed_managed_can_be_explicitly_retained_public_only(self):
        sync.synchronize(self.args())
        self.files.remove("old.txt")
        self.commit = self.commit_files()
        sync.synchronize(self.args(public_only=["old.txt"]))
        self.assertEqual((self.target / "old.txt").read_bytes(), b"old tracked content\n")
        manifest = sync.parse_json((self.target / sync.MANIFEST).read_bytes())
        self.assertIn("old.txt", {row["path"] for row in manifest["publicOnlyFiles"]})

    def test_destination_directory_conflict_is_prevalidated(self):
        self.initialize_target()
        (self.target / "old.txt").mkdir()
        self.rejected_unchanged("destination is a directory")


if __name__ == "__main__":
    print("Actual temp:", tempfile.gettempdir(), flush=True)
    unittest.main(verbosity=2)
