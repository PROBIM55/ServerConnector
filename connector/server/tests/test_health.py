"""Тесты публичного /health и admin /admin/version."""


def test_health_returns_ok_and_version(client):
    response = client.get("/health")
    assert response.status_code == 200
    body = response.json()
    assert body["ok"] is True
    assert "version" in body
    assert isinstance(body["version"], str)
    assert len(body["version"]) >= 1


def test_admin_version_requires_auth(client):
    response = client.get("/admin/version")
    assert response.status_code in (401, 403)


def test_admin_version_with_key(client):
    response = client.get("/admin/version", headers={"X-Admin-Key": "test-admin-key"})
    assert response.status_code == 200
    body = response.json()
    assert "git_sha" in body
    assert "git_sha_short" in body
    assert "deployed_at" in body


def test_admin_version_with_wrong_key(client):
    response = client.get("/admin/version", headers={"X-Admin-Key": "definitely-wrong"})
    assert response.status_code in (401, 403)
