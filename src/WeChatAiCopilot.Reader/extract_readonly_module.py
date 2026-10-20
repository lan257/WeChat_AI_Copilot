from __future__ import annotations

import base64
import csv
import hashlib
import io
import sys
import zipfile
from pathlib import Path


PINNED_VERSION = "1.2.7.0"
DIST_PREFIX = "wechatauto_replica-"


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: py -3 extract_readonly_module.py <wheel-directory> <target-directory>", file=sys.stderr)
        return 2

    wheel_dir = Path(sys.argv[1])
    target = Path(sys.argv[2])
    wheels = sorted(wheel_dir.glob(f"{DIST_PREFIX}{PINNED_VERSION}-*.whl"))
    if len(wheels) != 1:
        print(f"Expected one pinned wechatauto-replica {PINNED_VERSION} wheel; found {len(wheels)}.", file=sys.stderr)
        return 1

    wheel = wheels[0]
    with zipfile.ZipFile(wheel) as archive:
        names = archive.namelist()
        metadata_path = next((name for name in names if name.endswith(".dist-info/METADATA")), None)
        record_path = next((name for name in names if name.endswith(".dist-info/RECORD")), None)
        db_path = "wechatauto/db.py"
        if not metadata_path or not record_path or db_path not in names:
            print("The pinned wheel is missing required metadata or its database module.", file=sys.stderr)
            return 1

        metadata = archive.read(metadata_path).decode("utf-8", errors="replace")
        if f"Version: {PINNED_VERSION}" not in metadata:
            print("The downloaded wheel version does not match the pinned release.", file=sys.stderr)
            return 1

        db_source = archive.read(db_path)
        rows = csv.reader(io.StringIO(archive.read(record_path).decode("utf-8")))
        db_record = next((row for row in rows if row and row[0] == db_path), None)
        if not db_record or not db_record[1].startswith("sha256="):
            print("The wheel RECORD has no SHA-256 entry for wechatauto/db.py.", file=sys.stderr)
            return 1
        recorded_digest = db_record[1].split("=", 1)[1].rstrip("=")
        actual_digest = base64.urlsafe_b64encode(hashlib.sha256(db_source).digest()).decode("ascii").rstrip("=")
        if actual_digest != recorded_digest:
            print("The upstream database module failed its wheel integrity check.", file=sys.stderr)
            return 1

        source_text = db_source.decode("utf-8")
        if "ReadProcessMemory" not in source_text or "WriteProcessMemory" in source_text or "SendMsg" in source_text:
            print("The upstream module did not pass the read-only source boundary check.", file=sys.stderr)
            return 1

        package = target / "wechatauto"
        package.mkdir(parents=True, exist_ok=True)
        (package / "db.py").write_bytes(db_source)
        (package / "__init__.py").write_text(
            '"""Receive-only database subset; no UI automation or message sending modules."""\n',
            encoding="utf-8",
        )
        (package / "logger.py").write_text(
            "class _NullLogger:\n"
            "    def debug(self, *args, **kwargs): pass\n"
            "    def info(self, *args, **kwargs): pass\n"
            "    def warning(self, *args, **kwargs): pass\n"
            "    def error(self, *args, **kwargs): pass\n"
            "    def critical(self, *args, **kwargs): pass\n"
            "\n"
            "wxlog = _NullLogger()\n",
            encoding="utf-8",
        )

        license_path = next(
            (name for name in names if name.upper().endswith("/LICENSE") or name.upper() == "LICENSE"),
            None,
        )
        if license_path:
            (target / "LICENSE-upstream.txt").write_bytes(archive.read(license_path))
        digest_hex = hashlib.sha256(db_source).hexdigest()
        (target / "UPSTREAM.txt").write_text(
            "Source: https://github.com/fanyuantaier/wechatauto-replica\n"
            f"PyPI release: {PINNED_VERSION}\n"
            f"Wheel: {wheel.name}\n"
            f"wechatauto/db.py SHA-256: {digest_hex}\n"
            "Extracted subset: db.py, empty package initializer, no-op logger shim.\n"
            "Upstream UI automation, sending, CLI, media, and Moments modules are not extracted.\n",
            encoding="utf-8",
        )

    print(f"Prepared receive-only database module {PINNED_VERSION} (SHA-256 {digest_hex}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
