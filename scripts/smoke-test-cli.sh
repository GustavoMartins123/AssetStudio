#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <path-to-AssetStudio.CLI> [expected-version]" >&2
  exit 2
fi

cli_bin="$1"
expected_version="${2:-}"

if [[ ! -x "$cli_bin" ]]; then
  echo "CLI executable not found or not executable: '$cli_bin'." >&2
  exit 1
fi

tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT

echo "[smoke] Checking --help..."
help_out="$("$cli_bin" --help)"
if [[ "$help_out" != *"AssetStudio.CLI"* ]]; then
  echo "--help output did not contain AssetStudio.CLI banner." >&2
  exit 1
fi

echo "[smoke] Checking --version..."
ver_out="$("$cli_bin" --version)"
if [[ "$ver_out" != AssetStudio.CLI\ v* ]]; then
  echo "Unexpected --version output: '$ver_out'." >&2
  exit 1
fi
if [[ -n "$expected_version" && "$ver_out" != *"$expected_version"* ]]; then
  echo "--version output '$ver_out' did not contain expected version '$expected_version'." >&2
  exit 1
fi
echo "[smoke] Version verified: $ver_out"

fixture_dir="$tmp_dir/fixture"
mkdir -p "$fixture_dir"
fixture_path="$fixture_dir/level0"

python3 - "$fixture_path" <<'PY'
import struct, sys

out_path = sys.argv[1]

def aligned_string(text: str) -> bytes:
    raw = text.encode("utf-8")
    pad = (4 - (len(raw) % 4)) % 4
    return struct.pack("<i", len(raw)) + raw + (b"\x00" * pad)

assets = [
    (101, "greeting", "hello-from-cli-smoke"),
    (102, "config", "mode=release"),
]
blobs = [aligned_string(name) + aligned_string(script) for _, name, script in assets]

buf = bytearray(48)
buf += b"2022.3.20f1\x00"
buf += struct.pack("<i", 19)  # StandaloneWindows64
buf += struct.pack("<B", 0)   # no TypeTrees
buf += struct.pack("<i", 1)   # type count
buf += struct.pack("<i", 49)  # TextAsset
buf += struct.pack("<B", 0)   # stripped
buf += struct.pack("<h", -1)  # script type index
buf += b"\x00" * 16           # old type hash

buf += struct.pack("<i", len(blobs))
start = 0
for (path_id, _, _), blob in zip(assets, blobs):
    while len(buf) % 4 != 0:
        buf.append(0)
    buf += struct.pack("<qqIi", path_id, start, len(blob), 0)
    start += (len(blob) + 7) & ~7

buf += struct.pack("<iiiB", 0, 0, 0, 0)
metadata_size = len(buf) - 48
while len(buf) % 16 != 0:
    buf.append(0)
data_offset = len(buf)
for blob in blobs:
    buf += blob
    while (len(buf) - data_offset) % 8 != 0:
        buf.append(0)

struct.pack_into(">I", buf, 8, 22)
struct.pack_into(">I", buf, 20, metadata_size)
struct.pack_into(">q", buf, 24, len(buf))
struct.pack_into(">q", buf, 32, data_offset)

with open(out_path, "wb") as fh:
    fh.write(buf)
PY

echo "[smoke] Checking scan --format json..."
scan_json="$("$cli_bin" scan --no-local-profiles "$fixture_dir" --format json)"
python3 - "$scan_json" <<'PY'
import json, sys
data = json.loads(sys.argv[1])
assert data["serializedFileCount"] == 1, f"Expected serializedFileCount=1, got {data}"
PY

echo "[smoke] Checking list --format json..."
list_json="$("$cli_bin" list --no-local-profiles --no-decompression-cache "$fixture_path" --format json)"
python3 - "$list_json" <<'PY'
import json, sys
data = json.loads(sys.argv[1])
assert data["count"] == 2, f"Expected count=2, got {data}"
PY

echo "[smoke] Checking export --type TextAsset --report..."
out_dir="$tmp_dir/out"
report_path="$tmp_dir/report.json"
"$cli_bin" export --no-local-profiles --no-decompression-cache "$fixture_path" --output "$out_dir" --type TextAsset --report "$report_path" >/dev/null

python3 - "$out_dir" "$report_path" <<'PY'
import json, os, sys
out_dir, report_path = sys.argv[1], sys.argv[2]
with open(os.path.join(out_dir, "TextAsset", "greeting.txt"), "r", encoding="utf-8") as fh:
    assert fh.read() == "hello-from-cli-smoke"
with open(os.path.join(out_dir, "TextAsset", "config.txt"), "r", encoding="utf-8") as fh:
    assert fh.read() == "mode=release"
with open(report_path, "r", encoding="utf-8") as fh:
    report = json.load(fh)
assert report["schemaVersion"] == 1, report
assert report["status"] == "Completed", report
assert report["exportedCount"] == 2, report
assert report["failedCount"] == 0, report
PY

echo "[smoke] Checking export --format json..."
out_json_dir="$tmp_dir/out-json"
export_json="$("$cli_bin" export --no-local-profiles --no-decompression-cache "$fixture_path" --output "$out_json_dir" --type TextAsset --format json)"
python3 - "$export_json" <<'PY'
import json, sys
data = json.loads(sys.argv[1])
assert data["schemaVersion"] == 1, data
assert data["status"] == "Completed", data
assert data["exportedCount"] == 2, data
PY

echo "[smoke] Checking exit code semantics..."
set +e
"$cli_bin" unknown-command >/dev/null 2>&1
rc_invalid=$?
"$cli_bin" scan --no-local-profiles "$tmp_dir/does-not-exist" >/dev/null 2>&1
rc_missing=$?
set -e

if [[ $rc_invalid -ne 2 ]]; then
  echo "Expected exit code 2 for unknown command, got $rc_invalid." >&2
  exit 1
fi
if [[ $rc_missing -ne 1 ]]; then
  echo "Expected exit code 1 for missing path, got $rc_missing." >&2
  exit 1
fi

echo "[smoke] All AssetStudio.CLI smoke checks passed!"
