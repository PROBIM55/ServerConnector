#!/usr/bin/env python3
"""Render NetBird v0.78.2 runtime files without starting containers."""

import argparse
import base64
import ipaddress
import os
from pathlib import Path
import re
import secrets
import sys


def secret(padded=True):
    value = base64.b64encode(secrets.token_bytes(32)).decode("ascii")
    return value if padded else value.rstrip("=")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--domain", required=True)
    parser.add_argument("--traefik-ip", required=True)
    parser.add_argument("--stun-port", type=int, required=True)
    parser.add_argument("--output-dir", type=Path, default=Path("/etc/structura/netbird"))
    args = parser.parse_args()

    if not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?", args.domain):
        parser.error("--domain must be a lowercase DNS hostname")
    try:
        proxy_ip = ipaddress.ip_address(args.traefik_ip)
    except ValueError:
        parser.error("--traefik-ip must be an IP address")
    if proxy_ip.version != 4:
        parser.error("--traefik-ip must be IPv4 for the trusted proxy CIDR")
    if not 1024 <= args.stun_port <= 65535:
        parser.error("--stun-port must be in range 1024..65535")

    out = args.output_dir
    out.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(out, 0o700)
    config = out / "config.yaml"
    dashboard = out / "dashboard.env"
    if config.exists() or dashboard.exists():
        parser.error("runtime files already exist; refusing to overwrite generated secrets")

    yaml = f'''server:
  listenAddress: ":80"
  exposedAddress: "https://{args.domain}:443"
  stunPorts: [{args.stun_port}]
  metricsPort: 9090
  healthcheckAddress: ":9000"
  logLevel: info
  logFile: console
  authSecret: "{secret(padded=False)}"
  dataDir: /var/lib/netbird
  auth:
    issuer: "https://{args.domain}/oauth2"
    signKeyRefreshEnabled: true
    sessionCookieEncryptionKey: "{secret()}"
    dashboardRedirectURIs:
      - "https://{args.domain}/nb-auth"
      - "https://{args.domain}/nb-silent-auth"
    cliRedirectURIs:
      - "http://localhost:53000/"
  reverseProxy:
    trustedPeers:
      - "{proxy_ip}/32"
    trustedHTTPProxies:
      - "{proxy_ip}/32"
  store:
    engine: sqlite
    encryptionKey: "{secret()}"
'''
    env = f'''NETBIRD_MGMT_API_ENDPOINT=https://{args.domain}
NETBIRD_MGMT_GRPC_API_ENDPOINT=https://{args.domain}
AUTH_AUDIENCE=netbird-dashboard
AUTH_CLIENT_ID=netbird-dashboard
AUTH_CLIENT_SECRET=
AUTH_AUTHORITY=https://{args.domain}/oauth2
USE_AUTH0=false
AUTH_SUPPORTED_SCOPES=openid profile email groups
AUTH_REDIRECT_URI=/nb-auth
AUTH_SILENT_REDIRECT_URI=/nb-silent-auth
NGINX_SSL_PORT=443
LETSENCRYPT_DOMAIN=none
'''
    created = []
    try:
        for path, content in ((config, yaml), (dashboard, env)):
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write(content)
            os.chmod(path, 0o600)
            created.append(path)
    except Exception:
        for path in created:
            path.unlink(missing_ok=True)
        raise
    print(f"Created root-readable runtime files in {out}; no containers were started.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except OSError as exc:
        print(f"prepare-runtime: {exc}", file=sys.stderr)
        raise SystemExit(1)
