"""Derive expected consumer pins from reviewed preimages, never candidate API results."""

import argparse
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import stat
import sys

PREIMAGES_SHA = "53a5c2b462121af1465fca59ceecf9851604962fb71cc0a47fec75a4d0977b85"
FIXTURE_SHA = "be31b964c83ac42a018df4340ae659254b14df7091b3e2b3604c738ab2192371"
EXACT_PROOF_SHA = "750613ca9ba9b7e25cf592256fa621fa968e2933959b1b7b20b621335487d878"
REVIEWED_SOURCE = "neutral-source-v3:707a52981fc9d1f1c360b81b6676d131be05ae899a9b59fd05886c581de11794"
EXACT_KEY = "f4d094606a0f40ccd43c1c9b8f0b6027f3d4316b91cd8f76c2e37a4145ca797a"
EXACT_VALUE = "912e53432f4f2eaa10dfbb3a27bca593aeaae4a0180ab7ccf75374d99dd9280b"
EMPTY = hashlib.sha256(b"").hexdigest()
SCHEMA = "bglang-neutral-runtime-package.v1"
FAMILIES = {"prefix": 16, "suffix": 16, "fuzzy": 16, "spelling": 10, "stress-group": 19, "row25-resource": 1}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def read_json(path, expected_sha=None):
    require(path.stat().st_size <= 2_000_000, f"JSON input too large: {path}")
    data = path.read_bytes()
    require(expected_sha is None or sha(data) == expected_sha, f"Pinned input SHA mismatch: {path}")
    value = json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_object,
                       parse_constant=lambda value: require(False, f"Invalid JSON number: {value}"))
    return value, {"path": str(path), "bytes": len(data), "sha256": sha(data)}


def snapshot(path):
    info = path.lstat()
    reparse = getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    require(not reparse and not path.is_symlink(), f"Unsafe linked path: {path}")
    return (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def package_id(manifest):
    fields = ["bglang.neutral-runtime-package.v1"]
    for name in ("lexicon", "wordFormation"):
        component = manifest[name]
        fields.extend(str(component[key]) for key in ("path", "bytes", "sha256", "sourceId"))
    # LexicalIdentity.HashLengthPrefixed uses .NET UTF-16 lengths, then UTF-8 bytes.
    material = b"".join(str(len(value.encode("utf-16-le")) // 2).encode("ascii") + b":" +
                        value.encode("utf-8") + b"\n" for value in fields)
    return "bglang-neutral:" + sha(material)[:16]


def validate_package(path):
    require(path.name == "manifest.json", "Candidate entry point must be manifest.json")
    snapshot(path.parent)
    before = snapshot(path)
    manifest, identity = read_json(path)
    require(type(manifest) is dict and set(manifest) ==
            {"schemaVersion", "packageId", "createdAtUtc", "lexicon", "wordFormation"}, "Unexpected manifest fields")
    require(manifest["schemaVersion"] == SCHEMA, "Only unannotated manifest v1 is supported")
    created = datetime.fromisoformat(manifest["createdAtUtc"])
    require(created.tzinfo is not None and created.year > 1, "Invalid manifest timestamp")
    expected_names = {"manifest.json", "lexicon.v3.jsonl", "word-formation.v0.json"}
    require({entry.name for entry in path.parent.iterdir()} == expected_names, "Unexpected package file set")
    snapshots = {path: before}
    for name, filename in (("lexicon", "lexicon.v3.jsonl"), ("wordFormation", "word-formation.v0.json")):
        component = manifest[name]
        require(type(component) is dict and set(component) == {"path", "bytes", "sha256", "sourceId"}, "Unexpected component fields")
        require(component["path"] == filename, f"Unexpected {name} path")
        require(type(component["bytes"]) is int and 0 < component["bytes"] <= 2**63 - 1, "Invalid component size")
        require(isinstance(component["sha256"], str) and re.fullmatch(r"[0-9a-f]{64}", component["sha256"]), "Invalid component SHA")
        target = path.parent / filename
        snapshots[target] = snapshot(target)
        require(target.is_file() and snapshots[target][2] == component["bytes"], f"Component size mismatch: {filename}")
        with target.open("rb") as stream:
            require(hashlib.file_digest(stream, "sha256").hexdigest() == component["sha256"], f"Component SHA mismatch: {filename}")
    source = manifest["lexicon"]["sourceId"]
    require(isinstance(source, str) and re.fullmatch(r"neutral-source-v3:[0-9a-f]{64}", source), "Invalid native source ID")
    require(source != REVIEWED_SOURCE, "Candidate must have a new source identity")
    require(manifest["wordFormation"]["sourceId"] == "word-formation-source-v0", "Unexpected graph source ID")
    require(manifest["packageId"] == package_id(manifest), "Component-derived package ID mismatch")
    require(all(snapshot(file) == stamp for file, stamp in snapshots.items()), "Package changed during validation")
    require({entry.name for entry in path.parent.iterdir()} == expected_names, "Package file set changed")
    return manifest, identity


def rebind(preimage, family, manifest, manifest_sha):
    require(not preimage or preimage.endswith("\n"), "Preimage must preserve its final LF")
    lines = preimage[:-1].split("\n") if preimage else []
    for index, line in enumerate(lines):
        if family == "row25-resource":
            fields = line.split("|")
            if index == 0:
                require(len(fields) == 8 and fields[0] == "provenance" and fields[4] == REVIEWED_SOURCE, "Invalid provenance row")
                fields = ["provenance", manifest["packageId"], SCHEMA, manifest_sha,
                          manifest["lexicon"]["sourceId"], manifest["lexicon"]["sha256"],
                          manifest["wordFormation"]["sourceId"], manifest["wordFormation"]["sha256"]]
            elif fields[0] == "reading":
                require(len(fields) == 9 and fields[5] == REVIEWED_SOURCE, "Invalid resource reading")
                fields[5] = manifest["lexicon"]["sourceId"]
            else:
                require(len(fields) == 4 and fields[0] == "form", "Invalid resource form")
            lines[index] = "|".join(fields)
        else:
            fields = line.split("\x1f")
            source_index = 6 if family == "fuzzy" else 4
            require(len(fields) == source_index + 4 and fields[source_index] == REVIEWED_SOURCE and
                    fields[-3:] == ["null"] * 3, "Invalid consumer reading")
            fields[source_index] = manifest["lexicon"]["sourceId"]
            lines[index] = "\x1f".join(fields)
    return "\n".join(lines) + ("\n" if lines else "")


def derive(preimages, fixture, manifest, manifest_sha):
    require(fixture["schemaVersion"] == "bglang-reviewed-stored-consumer-pins.v1" and
            fixture["predecessorProofSha256"] == PREIMAGES_SHA and
            fixture["exactPredecessorPlus13ProofSha256"] == EXACT_PROOF_SHA, "Fixture authority mismatch")
    require(preimages["schemaVersion"] == "bglang-source-rebind-pin-derivation.v1", "Preimage schema mismatch")
    require(Counter(row["family"] for row in preimages["reports"]) == FAMILIES, "Expected 78 reviewed cases")
    old = fixture["digests"]
    result = {}
    for row in preimages["reports"]:
        key = row["oldPin"]
        require(row["status"] == "DERIVED" and sha(row["oldPreimage"].encode("utf-8")) == key and
                sha(row["newPreimage"].encode("utf-8")) == row["newPin"] == old[key], "Reviewed preimage/pin mismatch")
        value = sha(rebind(row["newPreimage"], row["family"], manifest, manifest_sha).encode("utf-8"))
        require(result.setdefault(key, value) == value, "Conflicting duplicate pin")
    require(old[EXACT_KEY] == EXACT_VALUE, "Exact semantic authority mismatch")
    result[EXACT_KEY] = EXACT_VALUE
    unchanged = {key for key in result if result[key] == old[key]}
    require(len(result) == 50 and result.keys() == old.keys() and unchanged == {EMPTY, EXACT_KEY},
            "Expected exactly 50 keys, 48 changed pins and two unchanged pins")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reviewed_preimages_json", type=Path)
    parser.add_argument("candidate_manifest", type=Path)
    parser.add_argument("new_output_json", type=Path)
    args = parser.parse_args()
    output = args.new_output_json.resolve()
    require(not output.exists() and output.parent != args.candidate_manifest.parent.resolve(), "Output must be new and outside the package")
    fixture_path = Path(__file__).resolve().parents[2] / "tests/BgLang.Tests/Kernel/Fixtures/reviewed-stored-consumer-pins.v1.json"
    preimages, preimage_identity = read_json(args.reviewed_preimages_json.absolute(), PREIMAGES_SHA)
    fixture, fixture_identity = read_json(fixture_path, FIXTURE_SHA)
    manifest, manifest_identity = validate_package(args.candidate_manifest.absolute())
    pins = derive(preimages, fixture, manifest, manifest_identity["sha256"])
    result = {"schemaVersion": "bglang-native-consumer-pins.v1", "digests": pins,
              "proof": {"reviewedPreimages": preimage_identity, "reviewedFixture": fixture_identity,
                        "exactPredecessorPlus13ProofSha256": EXACT_PROOF_SHA,
                        "candidateManifest": manifest_identity, "candidateMetadata": manifest,
                        "caseCount": 78, "pinCount": 50, "changedPins": 48, "unchangedPins": 2,
                        "candidateApiOpened": False,
                        "scope": "Expected preserved readings with new provenance; component bytes and package ID verified. "
                                 "Lexicon semantic identity, generation equality and consumer acceptance still require independent validation."}}
    data = (json.dumps(result, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    with output.open("xb") as stream:
        stream.write(data)
    print(json.dumps({"path": str(output), "bytes": len(data), "sha256": sha(data), "pins": 50, "changed": 48, "unchanged": 2}))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, TypeError, KeyError) as error:
        sys.exit(f"derive_native_pins: {error}")
