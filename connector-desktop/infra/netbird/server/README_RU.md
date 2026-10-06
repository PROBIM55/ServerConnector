# Сервер управления NetBird для Structura

Состояние: 06.10.2026 сервер управления опубликован с доверенным TLS;
первичная настройка закрыта, политика All → All выключена. Реальные узлы,
ограниченные политики и VPN-сценарии ещё проходят интеграцию. На Linux VPS
`structura-prod` Traefik 2.11 обслуживает TCP 80/443 и наблюдает метки Docker через
`bim_web`. За файлом `/dynamic.yml` следит отдельный файловый источник
конфигурации; этот пакет использует только источник Docker и не меняет настройки
Traefik.

## Перед первым запуском

Публичные A-записи `netbird.structura-most.ru` и
`connector-access.structura-most.ru` созданы в Cloudflare как DNS-only и
проверены через публичный resolver: обе указывают на `109.73.194.38`.
Подстановочная запись DNS не нужна для базового сервера
управления; она требуется, только если отдельно включать необязательный NetBird
Proxy. В этом пакете Proxy выключен.

На Linux VPS UDP 3478 занят `nextcloud-talk-hpb` (TCP и UDP), менять его нельзя.
NetBird публикует UDP 3479 через Docker; одного разрешения UFW INPUT недостаточно,
поскольку опубликованный порт проходит через DNAT/FORWARD и цепочку `DOCKER-USER`.
До подключения узлов проверьте UDP 3479 на стороне провайдера, установите
узкое исключение `DOCKER-USER` из раздела ниже и проверьте ответ STUN извне.
Старые пользователи продолжают использовать текущий публичный SMB 445 через Windows
Connector.

Свободно около 15 GB из 154 GB на Linux VPS. Образы закреплены по релизным
тегам и digest манифеста: NetBird Server `0.78.2`, Dashboard `v2.94.0`.
Digest сверены с официальным Docker Hub 06.10.2026. Размер журналов ограничен
ротацией Compose; постоянный том SQLite/данных создаётся отдельно и не удаляется
при обычных командах `up` или `stop`.

## Защищённая подготовка конфигурации

Скрипт `prepare-runtime.py` — небольшой формирователь файлов по проверенной
схеме конфигурации исходного NetBird Server `v0.78.2`. Он создаёт три независимых
криптографических секрета, `config.yaml` и `dashboard.env` вне Git, с режимами
каталога `0700` и файлов `0600`. Скрипт не скачивает код, не запускает Compose и
отказывается перезаписывать существующие файлы. Решение 06.10.2026 заменяет прежнее
требование ждать DNS перед любой подготовкой: runtime и loopback-only bootstrap
можно выполнить заранее. Для этого сервер управления запускается с
`NETBIRD_PUBLIC_ENABLED=false`, панель и подключение узлов выключены.
Публичный запуск требует DNS, TLS и последующей сетевой приёмки.
Проверенный IP Traefik задаётся отдельно в `trustedPeers` для gRPC и
`trustedHTTPProxies` для HTTP: NetBird не должен доверять IP-заголовкам от
произвольного отправителя. После смены IP контейнера Traefik эти значения
нужно адресно обновить до подключения клиентов.

```bash
repo=/opt/structura-netbird/source
traefik_ip=$(sudo docker inspect -f '{{with index .NetworkSettings.Networks "bim_web"}}{{.IPAddress}}{{end}}' traefik)
test -n "$traefik_ip"
sudo python3 "$repo/connector-desktop/infra/netbird/server/prepare-runtime.py" \
  --domain netbird.structura-most.ru --traefik-ip "$traefik_ip" \
  --stun-port 3479 --output-dir /etc/structura/netbird
sudo stat -c '%a %n' /etc/structura/netbird /etc/structura/netbird/config.yaml /etc/structura/netbird/dashboard.env
sudo grep -E 'exposedAddress|stunPorts|trustedPeers|trustedHTTPProxies' /etc/structura/netbird/config.yaml
```

Перед запуском `compose up` проверьте, что адреса и STUN-порт совпадают с DNS и
сетевым планом; не выводите секреты. Если файлы runtime нужно заменить, сначала
согласуйте сохранение/ротацию состояния, затем вручную переместите старые файлы
в защищённое хранилище. Не копируйте runtime конфигурацию в Git checkout.

## Первичная настройка владельца

По умолчанию Compose задаёт обоим сервисам метку `traefik.enable=false`, поэтому
публичных маршрутизаторов Traefik нет. На время начальной настройки API
управления доступен только через `127.0.0.1:18080`; панель управления не
запускается. В NetBird v0.78.2 запросы `GET /api/instance` и `POST /api/setup`
не требуют аутентификации; второй создаёт первого владельца, только пока
требуется настройка экземпляра. PAT выдаётся только при
`NB_SETUP_PAT_ENABLED=true` и `create_pat:true`. Скрипт `bootstrap-owner.py`
сначала проверяет состояние, запрашивает пароль без отображения, обращается
только к локальному API, сохраняет одноразовый PAT со сроком действия один день
в root-файл с режимом `0600`, затем проверяет `setup_required=false` и успешный
аутентифицированный запрос `GET /api/users`. Пароль, PAT и тело ответа скрипт
не выводит.

Сначала поднимите только сервер управления, оставив внешнюю публикацию
выключенной. Это не
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

При успехе `stat` должен показать `600`. Сохраните PAT в защищённом хранилище
секретов и удалите локальную копию после подтверждённой передачи; срок действия
токена — одни сутки. Если скрипт сообщает, что настройка уже завершена, он ничего
не сбрасывает и не создаёт нового владельца или PAT. Остановитесь и используйте
существующую учётную запись владельца. Если после успешного `/api/setup` скрипт
не смог сохранить PAT, повторный вызов его не восстановит: владелец уже создан,
войдите под существующей учётной записью и выпустите новый PAT.

Публичные маршруты включайте только если скрипт настройки вернул код 0 и прошёл
проверки состояния/API. В том же проекте и с тем же томом Compose выключите
выдачу setup PAT и запустите оба сервиса с публичными маршрутами:

```bash
sudo env NB_SETUP_PAT_ENABLED=false NETBIRD_PUBLIC_ENABLED=true \
  docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" \
  -f "$compose" up -d netbird-server dashboard
```

Это пересоздаёт только службы NetBird и не сбрасывает именованный том,
владельца или PAT. Локальный адрес `127.0.0.1:18080` остаётся доступен только
с самого сервера;
После включения маршрутов внешний запрос к `/api/setup` будет отклонён как
`setup already completed`: первый владелец уже создан, а выдача PAT выключена.

## Обязательная настройка политики доступа до подключения узлов

В новом экземпляре NetBird создаётся начальная политика `All → All`, разрешающая
обмен трафиком между всеми узлами. До создания первого ключа подключения и до
регистрации любого узла владелец должен проверить политики и группы в панели
управления или через API и отключить только эту начальную политику «все ко всем».
Затем настройте необходимые группы, ограничительные политики доступа, маршруты и
привязку шлюза, проверьте, что разрешённый доступ соответствует требуемому, а
неразрешённый трафик блокируется. Нельзя считать, что провайдер NetBird настроит
это автоматически: автоматическая конфигурация в данном пакете не реализована.
До завершения этой проверки не выдавайте ключи и не подключайте клиентские или
шлюзовые узлы. Это обязательное условие до фактического подключения клиентов.
См. официальные рекомендации по
[управлению доступом к сети](https://docs.netbird.io/manage/access-control/manage-network-access).

## Адресная выкладка

При первом запуске создайте отдельную рабочую копию репозитория ServerConnector;
последующие обновления выполняйте только в ней. Не используйте рабочую копию
`/root/structura`, в которой находится основной стек Structura.

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

Затем выполните раздел «Первичная настройка владельца» до включения любого
публичного маршрута. До подключения клиентов владелец инфраструктуры должен
разрешить UDP 3479 в firewall провайдера и проверить доступность порта из Интернета.
Правило UFW INPUT не заменяет исключение для перенаправленного Docker-трафика в
`DOCKER-USER`.

На этом хосте включён `structura-docker-user-firewall.service`: он пересобирает
`DOCKER-USER` с белым списком и завершающим DROP. Если проверка подтверждает именно
этот загрузчик и DROP `-i eth0 -j DROP`, установите узкое правило из пакета:

```bash
repo=/opt/structura-netbird/source
sudo install -o root -g root -m 0755 "$repo/connector-desktop/infra/netbird/server/allow-stun-forward.sh" /usr/local/sbin/structura-netbird-stun-forward.sh
sudo install -o root -g root -m 0644 "$repo/connector-desktop/infra/netbird/server/structura-netbird-stun-forward.service" /etc/systemd/system/structura-netbird-stun-forward.service
sudo systemctl daemon-reload
sudo systemctl enable --now structura-netbird-stun-forward.service
sudo iptables -S DOCKER-USER
```

Скрипт только добавляет возвратное исключение для входящего UDP 3479 на
`109.73.194.38` через `eth0`, перед ожидаемым DROP. Он проверяет метку и точный
контракт правила, не очищает и не удаляет чужие правила, а при неизвестной форме
цепочки завершается без вставки. Unit запускается после Docker и
`structura-docker-user-firewall.service`, а также повторно применяет исключение
после перезапуска этих служб. Compose запускает только службы NetBird и не
перезапускает Traefik, Nextcloud или остальной стек.

## Проверка и откат

После `up` проверьте только этот проект и публичный адрес готовности NetBird:

```bash
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" ps
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" logs --tail=100 netbird-server dashboard
curl -fsS https://netbird.structura-most.ru/oauth2/.well-known/openid-configuration
sudo ss -H -lun | grep ':3479'
```

Войдите под владельцем, созданным через локальный API, затем зарегистрируйте
тестовый Windows-узел и Windows-узел-шлюз. Проверьте связь с сервером управления,
прямое соединение между узлами или работу ретранслятора, маршрут к нужному
SMB-ресурсу и отзыв политики доступа до включения любой конфигурации клиента.
Сам по себе этот сервис не предоставляет авторизацию SMB и доверенный
издатель сертификатов устройств Connector.

Для первичного отката остановите только эти две службы, сохранив их данные:

```bash
sudo docker compose --project-name structura-netbird --project-directory "$(dirname "$compose")" -f "$compose" stop netbird-server dashboard
```

Не используйте `down --volumes`: команда удалит сохранённые данные NetBird —
идентификатор, узлы, политики и базу. Этот порядок действий не предусматривает
перезапуск Caddy или Traefik.

## Для Connector mTLS требуется отдельный этап

API Windows Connector сейчас находится за TLS-терминацией Caddy. Первичная
регистрация по одноразовому токену использует обычный HTTPS без сертификата
клиента; продолжение сессии, восстановление и запросы зарегистрированных
устройств требуют сертификат клиента, переданный нативным TLS Kestrel. Реализация
читает `HttpContext.Connection.GetClientCertificateAsync` и отклоняет заголовки
`X-Client-Cert`, переданные через прокси. Поэтому HTTP-проксирование Caddy не
может передать нужную идентичность.

Чтобы сохранить используемый клиентами публичный порт, выделите имя SNI
(`connector-access.structura-most.ru`) и настройте на Linux Traefik TCP/443
передачу TLS без терминации к отдельному mTLS listener Kestrel на Windows. Небольшой
TCP relay контейнер в `bim_web` может направлять соединение на этот backend-порт,
а TCP router Traefik выбирает его по имени. Доступ к Windows listener нужно
ограничить IP-адресом Linux-прокси. Пока такой маршрут, listener и firewall rule
не установлены и не приняты. Публичный TLS-сертификат этого имени должен
завершаться на Windows; его обновление должно выполняться без оператора, а
закрытый ключ должен быть защищён для процесса Platform. Для автоматизации
DNS-01 необходим доступ к API DNS-провайдера. Нельзя заранее рассчитывать на
HTTP-01, так как порты 80/443 обслуживают другие продукты. Публичный HTTPS
сертификат сервера, CA для сертификатов устройств Connector и подпись пакета
Authenticode — это три разных ключа и цепочки доверия. Для доверия Windows к
публичному HTTPS-сертификату покупка сертификата code-sign не требуется.

Пока маршрут и автоматическое обновление не проверены и не приняты, оставьте
`Connector.Desktop/connector-access.json` выключенным, а настройки API и ленты
обновлений production — не заполненными. Публичный listener `:24443` не заменяет
маршрут клиентских запросов через порт 443.
