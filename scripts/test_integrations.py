#!/usr/bin/env python3
"""Standard-library sanity checks for the discovery-only integration catalog."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "integrations" / "catalog.json"


def main():
    raw = CATALOG.read_text(encoding="utf-8")
    assert len(raw.encode("utf-8")) <= 64 * 1024, "catalog is too large"
    data = json.loads(raw)
    assert data.get("schemaVersion") == 1
    entries = data.get("integrations")
    assert isinstance(entries, list) and 0 < len(entries) <= 64
    ids = set()
    for entry in entries:
        assert entry["id"] not in ids, f"duplicate integration id: {entry['id']}"
        ids.add(entry["id"])
        assert entry["type"] in {"declarative", "addin"}
        for key in ("id", "name", "summary", "publisher", "minHostVersion", "maxHostVersion", "jellyfinAbi"):
            value = entry.get(key)
            assert isinstance(value, str) and value.strip(), f"{entry['id']}: invalid {key}"
            assert "<" not in value and ">" not in value, f"{entry['id']}: markup in {key}"
        assert entry.get("platforms") and entry.get("capabilities") is not None
        if entry["type"] == "declarative":
            assert entry.get("builtinProviderId") and not entry.get("installGuideUrl")
        else:
            assert entry.get("builtinProviderId") == "Plex", "third-party executable entries are not open"
            assert entry.get("installGuideUrl", "").startswith("https://github.com/")
        assert "package" not in entry and "script" not in entry and "html" not in entry
    print(f"PASS: {len(entries)} discovery-only integration catalog entry(s)")


if __name__ == "__main__":
    main()
