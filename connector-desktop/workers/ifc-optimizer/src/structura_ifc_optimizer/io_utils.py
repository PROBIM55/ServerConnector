from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any

STEP_PREAMBLE = b"ISO-10303-21;"


def require_ifc_file(path_value: str | Path) -> Path:
    path = Path(path_value).expanduser().resolve()
    if not path.exists():
        raise FileNotFoundError(f"IFC file not found: {path}")
    if not path.is_file():
        raise ValueError(f"IFC source is not a file: {path}")
    if path.suffix.lower() != ".ifc":
        raise ValueError(f"Expected a plain .ifc file: {path}")
    with path.open("rb") as handle:
        prefix = handle.read(4096)
    normalized_prefix = prefix.lstrip(b"\xef\xbb\xbf\x00\t\r\n ")
    if (
        not normalized_prefix.startswith(STEP_PREAMBLE)
        or b"HEADER;" not in prefix.upper()
    ):
        raise ValueError(f"Invalid IFC STEP header: {path}")
    return path


def require_distinct_output(input_path: Path, output_value: str | Path) -> Path:
    output_path = Path(output_value).expanduser().resolve()
    if output_path.suffix.lower() != ".ifc":
        raise ValueError("Output must be a plain .ifc file")
    if output_path == input_path:
        raise ValueError("The source IFC cannot be overwritten")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    return output_path


def sha256_file(path: Path, *, chunk_size: int = 8 * 1024 * 1024) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(chunk_size), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json_atomic(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    with temp_path.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temp_path, path)


def bytes_label(value: int) -> str:
    units = ("B", "KB", "MB", "GB", "TB")
    amount = float(value)
    for unit in units:
        if amount < 1024.0 or unit == units[-1]:
            return f"{amount:.1f} {unit}"
        amount /= 1024.0
    return f"{value} B"
