#!/usr/bin/env python3
"""Create the first NetBird owner through the loopback-only v0.78.2 setup API."""

import getpass
import json
import os
from pathlib import Path
import secrets
import sys
import urllib.error
import urllib.request


BASE = "http://127.0.0.1:18080"
TOKEN_FILE = Path("/etc/structura/netbird/bootstrap-owner.pat")


def request(path, method="GET", payload=None, token=None):
    headers = {"Accept": "application/json"}
    data = None
    if payload is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(payload).encode("utf-8")
    if token:
        headers["Authorization"] = f"Token {token}"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=8) as response:
            raw = response.read(1024 * 1024)
            return response.status, json.loads(raw) if raw else {}
    except urllib.error.HTTPError as exc:
        # Never print a body: setup errors can contain user-controlled values.
        raise RuntimeError(f"NetBird API returned HTTP {exc.code} for {path}") from None
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"NetBird API request failed for {path} ({type(exc).__name__})") from None


def is_setup_required(payload):
    value = payload.get("setup_required", payload.get("setupRequired"))
    if not isinstance(value, bool):
        raise RuntimeError("instance status has no boolean setup_required field")
    return value


def save_token(token):
    TOKEN_FILE.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(TOKEN_FILE.parent, 0o700)
    temporary = TOKEN_FILE.with_name(f".{TOKEN_FILE.name}.{secrets.token_hex(8)}.tmp")
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="ascii") as stream:
            stream.write(token + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.link(temporary, TOKEN_FILE)
        os.chmod(TOKEN_FILE, 0o600)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    try:
        _, status = request("/api/instance")
        if not is_setup_required(status):
            print("Setup already complete; no owner or token changed. Stop and inspect existing state.", file=sys.stderr)
            return 2

        email = input("First owner email: ").strip()
        name = input("First owner display name: ").strip()
        password = getpass.getpass("First owner password: ")
        confirm = getpass.getpass("Confirm password: ")
        if not email or not name or not password or password != confirm:
            print("Email/name/password missing or passwords do not match.", file=sys.stderr)
            return 2

        _, result = request("/api/setup", method="POST", payload={
            "email": email,
            "name": name,
            "password": password,
            "create_pat": True,
            "pat_expire_in": 1,
        })
        token = result.get("personal_access_token")
        if not isinstance(token, str) or not token:
            print("Setup response had no one-time PAT; owner may exist. Do not repeat setup blindly.", file=sys.stderr)
            return 3

        save_token(token)
        _, status = request("/api/instance")
        if is_setup_required(status):
            print("Setup API returned a token but setup is still required; do not enable public routes.", file=sys.stderr)
            return 4
        request("/api/users", token=token)
        print(f"Owner created; instance setup and PAT API checks passed. One-day token stored at {TOKEN_FILE} (0600).")
        print("Disable NB_SETUP_PAT_ENABLED before enabling public Traefik routes.")
        return 0
    except (RuntimeError, OSError) as exc:
        print(f"bootstrap-owner: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
