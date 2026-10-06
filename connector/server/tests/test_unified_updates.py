"""Release routing must never expose legacy updates, config, or signing material."""

import json

import pytest
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient

from unified_updates import create_unified_update_router, resolve_feed_file


@pytest.fixture
def feed(tmp_path):
    root = tmp_path / "unified-updates"
    release = root / "stable" / "releases" / "1.1.0"
    release.mkdir(parents=True)
    (root / "stable" / "current.json").write_text(
        json.dumps({"schemaVersion": 1, "release": "1.1.0"}), encoding="utf-8"
    )
    (release / "connector-release.stable.json").write_bytes(b'{"signed":"catalog"}')
    (release / "connector-release.stable.json.sig").write_bytes(b"s" * 64)
    (release / "Structura.Connector.Desktop-1.1.0-stable-full.nupkg").write_bytes(b"package")
    return root, release


def test_feed_returns_exact_bytes_without_redirect_or_legacy_route(feed):
    root, _ = feed
    app = FastAPI()
    app.include_router(create_unified_update_router(root))
    with TestClient(app) as client:
        catalog = client.get("/updates/unified/stable/connector-release.stable.json")
        assert catalog.status_code == 200
        assert catalog.content == b'{"signed":"catalog"}'
        assert catalog.headers["cache-control"] == "no-store"
        assert "location" not in catalog.headers
        package = client.get("/updates/unified/stable/Structura.Connector.Desktop-1.1.0-stable-full.nupkg")
        assert package.content == b"package"
        assert "immutable" in package.headers["cache-control"]
        assert client.get("/updates/latest.json").status_code == 404


def test_inflight_package_remains_available_after_pointer_moves(feed):
    root, _ = feed
    next_release = root / "stable" / "releases" / "1.1.1"
    next_release.mkdir()
    (next_release / "connector-release.stable.json").write_bytes(b"next")
    (root / "stable" / "current.json").write_text('{"schemaVersion":1,"release":"1.1.1"}')
    assert resolve_feed_file(root, "stable", "connector-release.stable.json")[0].read_bytes() == b"next"
    assert resolve_feed_file(root, "stable", "Structura.Connector.Desktop-1.1.0-stable-full.nupkg")[0].read_bytes() == b"package"


def test_preview_package_routes_by_native_version_and_exact_channel(tmp_path):
    root = tmp_path / "unified-updates"
    release = root / "preview" / "releases" / "1.1.0-preview.11"
    release.mkdir(parents=True)
    (root / "preview" / "current.json").write_text(
        '{"schemaVersion":1,"release":"1.1.0-preview.11"}', encoding="utf-8"
    )
    package = "Structura.Connector.Desktop-1.1.0-preview.11-preview-full.nupkg"
    (release / package).write_bytes(b"preview package")

    path, immutable = resolve_feed_file(root, "preview", package)

    assert path == release / package
    assert path.read_bytes() == b"preview package"
    assert immutable is True
    with pytest.raises(HTTPException):
        resolve_feed_file(root, "stable", package)


def test_delta_package_uses_channel_suffix_and_base_version(feed):
    root, release = feed
    name = "Structura.Connector.Desktop-1.1.1-stable-delta.1.1.0.nupkg"
    (release.parent / "1.1.1").mkdir()
    (release.parent / "1.1.1" / name).write_bytes(b"delta package")

    path, immutable = resolve_feed_file(root, "stable", name)

    assert path.parent.name == "1.1.1"
    assert path.read_bytes() == b"delta package"
    assert immutable is True


def test_real_app_keeps_legacy_manifest_and_uses_persistent_runtime_root(app_module, runtime_paths, monkeypatch):
    legacy = {"version": "1.0.31", "msiUrl": "https://example.test/legacy.msi"}
    monkeypatch.setattr(app_module, "load_update_manifest", lambda: legacy)
    channel = runtime_paths["runtime"] / "unified-updates" / "stable"
    release = channel / "releases" / "1.1.0"
    release.mkdir(parents=True)
    (channel / "current.json").write_text('{"schemaVersion":1,"release":"1.1.0"}')
    (release / "connector-release.stable.json").write_bytes(b"persistent feed")
    # Exercise the mounted application routes; this file-only endpoint does not
    # require the unrelated database migration performed by application lifespan.
    client = TestClient(app_module.app)
    try:
        assert client.get("/updates/latest.json").json() == legacy
        assert client.get("/updates/unified/stable/connector-release.stable.json").content == b"persistent feed"
    finally:
        client.close()


@pytest.mark.parametrize("channel,filename", [
    ("..", "connector-release.stable.json"),
    ("stable", "../config.json"),
    ("stable", "..\\config.json"),
    ("stable", "config.json"),
    ("stable", "current.json"),
    ("stable", "connector-release.stable.json:secret"),
    ("stable", "connector-release.preview.json"),
    ("stable", "Structura.Connector.Desktop-1.1.0-preview.11-preview-full.nupkg"),
    ("preview", "Structura.Connector.Desktop-1.1.0-stable-full.nupkg"),
    ("stable", "release-p256-private.pem"),
])
def test_rejects_paths_secrets_and_other_channels(feed, channel, filename):
    with pytest.raises(HTTPException) as error:
        resolve_feed_file(feed[0], channel, filename)
    assert error.value.status_code == 404


@pytest.mark.parametrize("pointer", [
    '{"schemaVersion":1,"release":"../../outside"}',
    '{"schemaVersion":true,"release":"1.1.0"}',
    '{"schemaVersion":1,"release":"1.1.0","extra":"secret"}',
    '[]', '{', 'x' * 1025,
])
def test_rejects_corrupt_or_escaping_pointer(feed, pointer):
    root, _ = feed
    (root / "stable" / "current.json").write_text(pointer)
    with pytest.raises(HTTPException):
        resolve_feed_file(root, "stable", "connector-release.stable.json")


def test_rejects_windows_reparse_attribute(feed, monkeypatch):
    from pathlib import Path
    from types import SimpleNamespace
    root, release = feed
    original = Path.lstat

    def reparse(path):
        info = original(path)
        if path == release:
            return SimpleNamespace(st_mode=info.st_mode, st_file_attributes=0x400)
        return info

    monkeypatch.setattr(Path, "lstat", reparse)
    with pytest.raises(HTTPException):
        resolve_feed_file(root, "stable", "connector-release.stable.json")
