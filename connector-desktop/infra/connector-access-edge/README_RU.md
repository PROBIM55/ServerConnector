# TCP passthrough для Connector API

Этот пакет добавляет один TCP relay в уже существующую Docker-сеть `bim_web`.
Traefik 2.11 выбирает его только для SNI `connector-access.structura-most.ru`
на entrypoint `websecure` и передаёт TLS-поток без терминации в HAProxy, затем
на выделенный Windows Kestrel listener `62.113.36.107:24443`. Сертификат
публичного HTTPS оканчивается на Windows; Traefik/HAProxy не расшифровывают TLS
и не передают сертификат клиента заголовками.

В HAProxy закреплён официальный образ `haproxy:3.4.5-alpine3.24` по manifest
digest `sha256:7ffdd3845020aa4c97ffaa56c8cab3ad473ecae0b8ea5e55d2378d663755e092`
(проверено в Docker Hub registry 06.10.2026). HAProxy слушает только внутри
Docker-сети на 24443, без `ports`, панели статистики, admin socket или второго
backend. Traefik получает единственный сервис и TLS passthrough роутер из labels.
Проверка backend выполняет только TCP connect к заданному Kestrel порту.

Состояние на момент подготовки: production не изменялся, DNS
`connector-access.structura-most.ru` отсутствует, API DNS credentials не выданы.
Read-only проверка Windows 62.113.36.107 через SSH 06.10.2026 не обнаружила
слушателя TCP 24443 (`netstat -ano | findstr :24443` — пусто). Пакет пока не
разворачивать.

## Обязательные предусловия

До выкладки должны быть выполнены и независимо проверены все пункты:

1. Публичный DNS A `connector-access.structura-most.ru` указывает на Linux VPS,
   где живёт Traefik `websecure`. На момент аудита это `109.73.194.38`, но адрес
   сверить перед выкладкой. Нужен доступ к DNS-провайдеру для выпуска и
   автоматического обновления сертификата; его API-токена пока нет.
2. На Windows должен работать выделенный Kestrel listener TCP 24443 с native
   TLS client certificate validation для Connector API. Listener принимает
   соединения только от исходящего IP Linux VPS `109.73.194.38`; все остальные
   источники запрещены Windows Firewall/provider firewall. Не разрешать
   порт 24443 от всего Интернета и не публиковать его как пользовательский
   обход Traefik.
3. Windows должен иметь публично доверенный HTTPS-сертификат с SAN для
   `connector-access.structura-most.ru` и `connector-gateway.structura-most.ru`.
   Второе имя настраивается как private DNS в NetBird и должно разрешаться в
   проверенный overlay IP Windows gateway после его provision. Публичный
   DNS-01 challenge для обоих имён подтверждает контроль над зоной; публичная
   A-запись для private FQDN не требуется.
4. Проверить, что сертификат загружает именно Windows Kestrel, цепочка
   доверена Windows-клиентам, продление обновляет сертификат у Kestrel без ручного
   перезапуска соседних сервисов и срок действия контролируется. Доступ к DNS API,
   DNS-01 credentials и установленное автоматическое продление пока отсутствуют;
   это реальные блокеры готовности.
5. Отдельно подготовить и проверить Connector device issuer CA для клиентских
   сертификатов. Это не HTTPS server certificate и не Authenticode-подпись MSI;
   подменять одну цепочку другой нельзя.
6. До включения роутера локально запустить endpoint smoke Kestrel на Windows:
   TLS server certificate валиден для обоих DNS SAN, запрос без клиентского
   сертификата отклоняется на mTLS endpoints, одноразовое enrollment поведение
   соответствует приложению. Эти проверки ещё не выполнялись.

Пока эти условия не выполнены, `connector-access.json` должен оставаться
выключенным, а адреса feed/API не публиковать. Этот пакет не создаёт PKI,
сертификаты, runtime app config, DNS, firewall rules или Kestrel listener.

## Адресная выкладка после проверок

Выкладывать после DNS, сертификата и механизма его продления, Kestrel listener и
firewall preflight, плюс актуальной проверки production deployment gate. В checkout
репозитория `ServerConnector` запустить только указанный Compose project/service;
не перезапускать Caddy или Traefik и не применять весь stack:

```bash
repo=/opt/structura-connector/source
compose="$repo/connector-desktop/infra/connector-access-edge/compose.yaml"
sudo docker network inspect bim_web >/dev/null
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" config --quiet
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" pull connector-access-relay
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" up -d connector-access-relay
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" ps
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" logs --tail=80 connector-access-relay
```

HAProxy `healthy` подтверждает валидный локальный конфиг; HAProxy отдельно
проверяет TCP доступность Kestrel. Это само по себе не доказывает работу TLS
passthrough или mTLS. Приёмка после выкладки должна проверять сертификат,
полученный клиентом с публичным SNI, и отрицательный запрос без client cert;
подтвердить отсутствие прямого доступа к Windows :24443 извне.

Для rollback после проверки влияния остановить только relay service и сохранить
его конфиг. Роутер исчезнет из динамической Docker-конфигурации Traefik; edge и
соседние сервисы не перезапускать:

```bash
sudo docker compose --project-name connector-access-edge --project-directory "$(dirname "$compose")" -f "$compose" stop connector-access-relay
```
