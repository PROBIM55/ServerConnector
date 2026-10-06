"""Isolated, persistent feed for the unified desktop; legacy updates stay unchanged."""

import json
import re
import stat
from pathlib import Path

from fastapi import APIRouter, HTTPException
from fastapi.responses import FileResponse


_CHANNEL = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}\Z")
_VERSION_TEXT = r"\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?"
_VERSION = re.compile(_VERSION_TEXT + r"\Z")


def _package_pattern(channel: str) -> re.Pattern[str]:
    # Velopack and the signed catalog place the channel between version and
    # package type, e.g. 1.1.0-preview.11-preview-full.nupkg. Anchor it to
    # the requested route channel so a package can never select another
    # channel's release directory.
    return re.compile(
        r"Structura\.Connector\.Desktop-(?P<version>" + _VERSION_TEXT + r")-"
        + re.escape(channel)
        + r"-(?:full|delta(?:\." + _VERSION_TEXT + r")?)\.nupkg\Z"
    )


def _regular_path(path: Path, *, directory: bool = False) -> bool:
    """Refuse symbolic links and Windows junctions, including parent directories."""
    try:
        for candidate in (path, *path.parents):
            info = candidate.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                return False
        return path.is_dir() if directory else path.is_file()
    except OSError:
        return False


def _not_found() -> HTTPException:
    return HTTPException(status_code=404, detail="Unified update file not found")


def resolve_feed_file(root: Path, channel: str, filename: str) -> tuple[Path, bool]:
    """Metadata follows an atomic pointer; versioned packages remain downloadable."""
    if not _CHANNEL.fullmatch(channel) or len(filename) > 240:
        raise _not_found()
    if filename != Path(filename).name or any(c in filename for c in "/\\:"):
        raise _not_found()
    package = _package_pattern(channel).fullmatch(filename)
    metadata = {
        f"connector-release.{channel}.json",
        f"connector-release.{channel}.json.sig",
        f"releases.{channel}.json",
        f"assets.{channel}.json",
        f"RELEASES-{channel}",
        "Structura.Connector.Installer.exe",
        "Connector.Unified.Setup.msi",
        "checksums.sha256",
    }
    if package is None and filename not in metadata:
        raise _not_found()
    channel_root = root / channel
    if not _regular_path(channel_root, directory=True):
        raise _not_found()
    if package is not None:
        version = package.group("version")
    else:
        pointer = channel_root / "current.json"
        if not _regular_path(pointer) or pointer.stat().st_size > 1024:
            raise _not_found()
        try:
            current = json.loads(pointer.read_text(encoding="utf-8"))
            if (not isinstance(current, dict) or set(current) != {"schemaVersion", "release"}
                    or type(current["schemaVersion"]) is not int or current["schemaVersion"] != 1
                    or not isinstance(current["release"], str)
                    or not _VERSION.fullmatch(current["release"])):
                raise ValueError("Invalid release pointer")
            version = current["release"]
        except (OSError, ValueError, UnicodeError):
            raise _not_found() from None
    path = channel_root / "releases" / version / filename
    if not _regular_path(path):
        raise _not_found()
    return path, package is not None


def create_unified_update_router(root: Path) -> APIRouter:
    router = APIRouter()

    @router.get("/updates/unified/{channel}/{filename}")
    def unified_update_file(channel: str, filename: str) -> FileResponse:
        path, immutable = resolve_feed_file(root, channel, filename)
        return FileResponse(
            path=path,
            filename=filename,
            media_type="application/octet-stream",
            headers={
                "Cache-Control": "public, max-age=31536000, immutable" if immutable else "no-store",
                "X-Content-Type-Options": "nosniff",
            },
        )

    return router
