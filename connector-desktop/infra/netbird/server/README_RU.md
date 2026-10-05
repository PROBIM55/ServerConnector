# NetBird control plane для Structura

Состояние: подготовлен локальный Compose-пакет; production не менялся. При
read-only проверке 06.10.2026 на Linux VPS `structura-prod` NetBird отсутствовал,
Traefik 2.11 обслуживал TCP 80/443 и наблюдал Docker labels через `bim_web`.
Отдельный file-provider следит за `/dynamic.yml`; этот пакет использует только
Docker provider и не меняет конфигурацию Traefik.

## Перед первым запуском

Публичные A-записи `netbird.structura-most.ru` и
`connector-access.structura-most.ru` через корпоративный DNS `192.168.100.1`
пока отвечают NXDOMAIN. Запросите их добавление на `109.73.194.38` у владельца
DNS зоны. Wildcard не нужен для базового control plane; он требуется только
если отдельно включать необязательный NetBird Proxy. Proxy в этом пакете не
включён.

На Linux VPS UDP 3478 занят `nextcloud-talk-hpb` (TCP и UDP), менять его нельзя.
UDP 3479 на момент проверки не имел слушателя, но UFW включён с политикой
`deny incoming` и разрешает 3478, не 3479. До запуска владелец инфраструктуры
должен отдельно разрешить входящий UDP 3479 в host/provider firewall и проверить
его снаружи. Старые пользователи продолжают использовать текущий публичный SMB
445 через Windows Connector.

Свободно около 15 GB из 154 GB на Linux VPS. Образы закреплены по релизным
тегам и manifest digest: NetBird Server `0.78.2`, Dashboard `v2.94.0`.
Digest'ы сверены с официальным Docker Hub 06.10.2026. Логи ограничены Compose
rotation; persistent SQLite/data volume создаётся отдельно и не удаляется при
обычном `up` или `stop`.

## Защищённая подготовка конфигурации

Скрипт `prepare-runtime.py` — небольшой renderer только подтверждённой схемы
конфигурации upstream NetBird Server `v0.78.2`. Он создаёт три независимых
криптографических секрета, `config.yaml` и `dashboard.env` вне Git, с режимами
каталога `0700` и файлов `0600`. Скрипт не скачивает код, не запускает Compose и
отказывается перезаписывать существующие файлы. Генерируйте runtime только после
появления DNS и принятого плана окна запуска:

```bash
repo=/opt/structura-netbird/source
traefik_ip=$(sudo docker inspect -f '{{with index .NetworkSettings.Networks "bim_web"}}{{.IPAddress}}{{end}}' traefik)
test -n "$traefik_ip"
sudo python3 "$repo/connector-desktop/infra/netbird/server/prepare-runtime.py" \
  --domain netbird.structura-most.ru --traefik-ip "$traefik_ip" \
  --stun-port 3479 --output-dir /etc/structura/netbird
sudo stat -c '%a %n' /etc/structura/netbird /etc/structura/netbird/config.yaml /etc/structura/netbird/dashboard.env
sudo grep -E 'exposedAddress|stunPorts|trustedHTTPProxies' /etc/structura/netbird/config.yaml
```

Перед `compose up` проверьте, что адреса и STUN-порт совпадают с DNS и сетевым
планом; секреты не выводите. Если runtime-файлы нужно заменить, сначала
согласуйте сохранение/ротацию состояния, затем вручную переместите старые файлы
в защищённое хранилище. Не копируйте runtime конфигурацию в Git checkout.

## Первичный owner bootstrap

Compose по умолчанию ставит `traefik.enable=false` на обоих сервисах: публичных
Traefik routers нет. Management API временно опубликован только на
`127.0.0.1:18080`; dashboard при bootstrap не запускается. Официальный
NetBird v0.78.2 регистрирует unauthenticated `GET /api/instance` и `POST
/api/setup`; второй создаёт первого owner только пока setup required. PAT
выдаётся лишь при `NB_SETUP_PAT_ENABLED=true` и `create_pat:true`. Скрипт
`bootstrap-owner.py` сначала проверяет статус, запрашивает пароль без echo,
отправляет только локальный loopback API, сохраняет одноразовый однодневный PAT
под root с режимом `0600`, затем проверяет `setup_required=false` и
авторизованный `GET /api/users`. Он не печатает пароль, PAT или тело ответа.

Сначала поднимите только management с внешней публикацией выключенной. Это не
перезапускает соседние сервисы:

```bash
repo=/opt/structura-netbird/source
compose="$repo/connector-desktop/infra/netbird/server/compose.yaml"
sudo env NB_SETUP_PAT_ENABLED=true NETBIRD_PUBLIC_ENABLED=false \
  docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" \
  -f "$compose" up -d netbird-server
sudo python3 "$repo/connector-desktop/infra/netbird/server/bootstrap-owner.py"
sudo stat -c '%a %n' /etc/structura/netbird/bootstrap-owner.pat
```

При успехе `stat` должен показать `600`. Сохраните PAT в закрытый менеджер
секретов, затем удалите локальную копию после подтверждённой передачи; токен
истекает через сутки. Если скрипт сообщает, что setup уже завершён, он намеренно
ничего не сбрасывает и не создаёт нового owner/PAT. Остановитесь и используйте
существующую owner учётную запись. Если скрипт не смог сохранить PAT после
успешного `/api/setup`, повторный вызов не восстановит его: owner уже создан,
выпустите новый PAT после входа существующим owner.

Публичные routes включайте только после кода возврата 0 bootstrap скрипта и
проверки его успешных instance/API проверок. В той же Compose project/volume
выключите setup PAT и запустите оба сервиса с routes on:

```bash
sudo env NB_SETUP_PAT_ENABLED=false NETBIRD_PUBLIC_ENABLED=true \
  docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" \
  -f "$compose" up -d netbird-server dashboard
```

Это пересоздаёт только NetBird services, не сбрасывает named volume, owner или
публичный токен. Локальная петля `127.0.0.1:18080` остаётся ограниченной loopback;
`/api/setup` снаружи после включения routes вернёт отказ `setup already
completed`, поскольку первый owner уже есть, а PAT issuance выключен.

## Адресная выкладка

Первый раз создайте отдельный checkout репозитория ServerConnector; последующие
обновления забирают master только в этом checkout. Не используйте checkout
`/root/structura`, который содержит основной Structura stack.

```bash
sudo git clone --branch master --single-branch https://github.com/PROBIM55/ServerConnector.git /opt/structura-netbird/source
sudo git -C /opt/structura-netbird/source pull --ff-only origin master
repo=/opt/structura-netbird/source
compose="$repo/connector-desktop/infra/netbird/server/compose.yaml"
sudo test -s /etc/structura/netbird/config.yaml
sudo test -s /etc/structura/netbird/dashboard.env
sudo test "$(stat -c '%a' /etc/structura/netbird/config.yaml)" = 600
sudo test "$(stat -c '%a' /etc/structura/netbird/dashboard.env)" = 600
if sudo ss -H -lun | grep -q ':3479'; then
  echo 'UDP 3479 already in use'
  exit 1
fi
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" config --quiet
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" pull netbird-server dashboard
```

Then follow “Первичный owner bootstrap” before any public route is enabled. The
infrastructure owner must add the narrowly scoped UDP 3479 allow rule and confirm
it is reachable from the Internet before client enrollment. Compose only starts
the named NetBird services; it does not restart Traefik, Nextcloud, or the rest
of the stack. Traefik's running Docker provider reads the labels dynamically.

## Smoke and rollback

After `up`, inspect only this project and the public NetBird readiness endpoint:

```bash
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" ps
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" logs --tail=100 netbird-server dashboard
curl -fsS https://netbird.structura-most.ru/oauth2/.well-known/openid-configuration
sudo ss -H -lun | grep ':3479'
```

Sign in as the owner created by the loopback API, then enroll a test Windows peer
and a Windows server gateway peer. Verify management connectivity,
peer-to-peer or relay behavior, routing to the intended SMB destination, and
access-policy revoke before enabling any client config. This service alone does
not provide SMB authorization or a trusted Connector device issuer.

For an initial rollback, stop only these two services and keep their data:

```bash
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" stop netbird-server dashboard
```

Do not use `down --volumes`; it deletes the persisted NetBird identity, peers,
policies and database. No Caddy/Traefik restart is part of this runbook.

## Connector mTLS remains a separate gate

The Windows Connector API currently sits behind Caddy TLS termination. Initial
one-time-token enrollment uses normal HTTPS without a client certificate; resume,
recovery and registered-device API calls require a client certificate delivered
by native Kestrel TLS. The implementation reads
`HttpContext.Connection.GetClientCertificateAsync` and rejects forwarded
`X-Client-Cert` headers. Caddy HTTP proxying therefore cannot carry the required
identity.

The path to preserve the existing public client port is a separate SNI hostname
(`connector-access.structura-most.ru`) on Linux Traefik TCP/443 with TLS
passthrough to a dedicated Kestrel mTLS listener on Windows. A small TCP relay
container on `bim_web` can target that dedicated backend port while a Traefik
Docker TCP router matches the hostname; the Windows listener must be restricted
to the Linux proxy source. No such route/listener/firewall rule is installed or
accepted yet. The hostname's public TLS certificate must terminate on Windows,
with unattended renewal and the private key protected for the Platform process.
DNS-01 automation needs access to the DNS provider API; HTTP-01 cannot be assumed
because existing port 80/443 owners serve other products. The public HTTPS server
certificate, the Connector device issuer CA and Authenticode package signing
are three separate keys/trust chains. Code-signing purchase is not a requirement
for Windows to trust a publicly issued HTTPS server certificate.

Until that route and renewal are accepted, leave
`Connector.Desktop/connector-access.json` disabled and all production feed/API
configuration unset. A public `:24443` listener is not a substitute for the
requested port-443 client route.
