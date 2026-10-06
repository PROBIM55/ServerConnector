"""Совместимость legacy-клиента с GitHub-каналом обновлений."""

import json

import pytest
from fastapi import HTTPException


SHA256 = "a" * 64


class FakeGithubResponse:
    def __init__(self, payload):
        self.payload = payload

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def read(self):
        return json.dumps(self.payload).encode("utf-8")


def github_config():
    return {
        "github_updates_repo": "PROBIM55/connector",
        "github_updates_asset_name": "Connector.Desktop.Setup.msi",
    }


def github_release(tag_name, assets):
    return {"tag_name": tag_name, "assets": assets, "body": "release notes"}


def msi_asset(name):
    return {
        "name": name,
        "browser_download_url": f"https://example.test/{name}",
        "digest": f"sha256:{SHA256}",
    }


def mock_github_release(monkeypatch, app_module, payload):
    monkeypatch.setattr(app_module, "urlopen", lambda *_args, **_kwargs: FakeGithubResponse(payload))


def test_github_manifest_accepts_exact_asset_from_legacy_tag(app_module, monkeypatch):
    mock_github_release(
        monkeypatch,
        app_module,
        github_release("v1.2.3", [msi_asset("Connector.Desktop.Setup.msi")]),
    )

    assert app_module.load_github_update_manifest(github_config()) == {
        "version": "1.2.3",
        "msiUrl": "https://example.test/Connector.Desktop.Setup.msi",
        "sha256": SHA256,
        "notes": "release notes",
    }


def test_github_manifest_rejects_other_msi_asset(app_module, monkeypatch):
    mock_github_release(monkeypatch, app_module, github_release("v1.2.3", [msi_asset("Unified.Setup.msi")]))

    with pytest.raises(HTTPException, match="configured MSI asset"):
        app_module.load_github_update_manifest(github_config())


def test_github_manifest_rejects_unified_tag(app_module, monkeypatch):
    mock_github_release(
        monkeypatch,
        app_module,
        github_release("unified-v1.2.3", [msi_asset("Connector.Desktop.Setup.msi")]),
    )

    with pytest.raises(HTTPException, match="not a legacy"):
        app_module.load_github_update_manifest(github_config())


def test_update_manifest_falls_back_to_local_for_unified_latest_release(app_module, monkeypatch):
    local_manifest = {"version": "1.0.0", "msiUrl": "/updates/legacy.msi", "sha256": SHA256, "notes": "local"}
    mock_github_release(
        monkeypatch,
        app_module,
        github_release("unified-v1.2.3", [msi_asset("Connector.Desktop.Setup.msi")]),
    )
    monkeypatch.setattr(app_module, "load_local_update_manifest", lambda: local_manifest)
    app_module.clear_cached_github_manifest()

    assert app_module.load_update_manifest({**github_config(), "github_updates_fallback_local_manifest": True}) == local_manifest
