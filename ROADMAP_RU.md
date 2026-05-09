# Connector Roadmap — потенциальные улучшения

> Дата составления: 2026-05-09. Контекст: после миграции на PostgreSQL и переезда деплоя на src/runtime/backup. Документ — рабочий черновик; обновлять по мере реализации/отмены пунктов.
>
> Приоритизация: ROI = (влияние на пользователей + риск без действия) ÷ затраты. Этапы 1–3 рекомендую делать в порядке. Пункты второго эшелона — по необходимости. Стратегические — только при росте масштаба.

## Содержание

- [Что уже сделано (контекст)](#что-уже-сделано-контекст)
- [Топ-3 на ближайшее время](#топ-3-на-ближайшее-время)
  - [Этап 1. Observability (логи + метрики + uptime)](#этап-1-observability)
  - [Этап 2. Token lifecycle (expiry + rotation + revoke)](#этап-2-token-lifecycle)
  - [Этап 3. OpenAPI / Swagger UI](#этап-3-openapi--swagger-ui)
- [Второй эшелон](#второй-эшелон)
  - [Этап 4. Tekla publish preview](#этап-4-tekla-publish-preview)
  - [Этап 5. Audit log retention + индексы](#этап-5-audit-log-retention--индексы)
  - [Этап 6. Admin credentials rotation API](#этап-6-admin-credentials-rotation-api)
- [Стратегические — отложено](#стратегические--отложено)
- [Что не делаем (с обоснованием)](#что-не-делаем-с-обоснованием)
- [Открытые вопросы](#открытые-вопросы)

---

## Что уже сделано (контекст)

Перечисляю кратко, чтобы новый агент / ревью видел текущую базу:

| Спринт | Что сделано | Коммиты-маркеры |
|---|---|---|
| 2026-05-08 Phase 1 | Раскладка `C:\Connector\src\` + `runtime\` + `backup\`, env-aware пути в `app.py`, `runtime_launch.ps1` лаунчер | `66474d5`, `ee21c61` |
| 2026-05-08 Phase 2 | yoyo-migrations + baseline-схема, `init_db()` через миграции (без `CREATE TABLE` в коде) | `86d0f38`, `5179cd6` |
| 2026-05-08 Phase 3 | CI/CD workflow с `test+deploy`, auto-rollback, smoke на `/health`+`/admin/version` | `6632e39`, `7d1c2ea` |
| 2026-05-08 Phase 4 | pytest-набор (test_health, test_paths, test_migrations) + блокировка деплоя при red CI | `b7182fc` |
| 2026-05-08 Phase 5 | Удаление `CREATE TABLE` из app.py, deprecation deploy-скриптов, retention task `ConnectorBackupRetention` | `5179cd6`, `7f615b0` |
| 2026-05-09 PG migration | SQLite → PostgreSQL `connector_prod` (общий инстанс с Platform/FamilyBudget), `db.py` wrapper, `runtime/.env` для `CONNECTOR_DB_URL`, миграции в `migrations/{postgres,sqlite}/` | `3a7f1db`, `8a2a61e`, server-side cutover |

Текущая база — production-grade в плане deploy/storage. Дальнейшие пункты — про **operability** и **product**.

---

## Топ-3 на ближайшее время

### Этап 1. Observability

**Проблема:** сейчас если Connector упадёт ночью или начнёт ловить 500 на каком-то endpoint, мы узнаем только когда пользователь напишет. `runner.log` пишет минимум, `uvicorn.{out,err}.log` — текстовые портянки, парсить руками. Нет агрегированных метрик. Нет внешнего health-check'а.

**Цель:** обнаружение инцидентов **до** жалоб пользователей + способность объективно ответить «всё ли работает».

**Что менять:**

1. **Структурированные JSON-логи** через middleware FastAPI:
   - Один JSON на запрос: `request_id, ts, method, path, status, duration_ms, device_id, user, error_type, error_msg`.
   - Ротация: `logging.handlers.RotatingFileHandler` 50 MB × 5 файлов, в `C:\Connector\runtime\logs\connector.json.log`.
   - Существующие `uvicorn.{out,err}.log` остаются для отладки (stderr/stdout).
2. **`/metrics` endpoint** через `prometheus-fastapi-instrumentator` (`pip install` → 1 строка регистрации).
   - Метрики «из коробки»: `http_requests_total{path,status}`, `http_request_duration_seconds_bucket`, `http_requests_inprogress`.
   - Custom counters: `connector_heartbeats_total{device_id}`, `connector_admin_login_failures_total`, `connector_token_revoke_total`, `connector_smb_provision_total{outcome}`.
   - По умолчанию `/metrics` доступен публично; за `X-Admin-Key` или Caddy basic-auth — на твоё решение.
3. **Внешний uptime monitor:**
   - **Рекомендуемо:** [Better Uptime free tier](https://betterstack.com/uptime) — 10 monitors, 3-минутная частота, Telegram/email/Slack alert.
   - Альтернатива: поднять `uptime-kuma` в Docker на dev-машине (бесплатно, self-hosted).
   - Минимум: `GET /health` каждые 60–180 сек, alert при ≥3 fail подряд.
4. **Telegram alert на критичные события** (опционально, если есть Telegram bot):
   - При smoke-fail в CI deploy (через GH Actions notify-step).
   - При rollback на сервере (из `server_deploy_step.ps1` через `curl https://api.telegram.org/...`).
   - При >5 admin login failures за 5 минут (брутфорс-сигнал).

**Оценка трудозатрат:** 3–4 часа.

**Файлы, которые меняются:**
- `connector/server/app.py` — middleware, `/metrics` registration.
- `connector/server/requirements.txt` — `prometheus-fastapi-instrumentator==7.0.0`.
- `connector/server/logging_config.py` — новый, JSON formatter + rotation.
- `connector/server/runtime_launch.ps1` — настройка LOG_LEVEL env.
- Тесты: `tests/test_logging.py` (smoke что middleware пишет JSON).
- `CLAUDE.md` — раздел про observability и где смотреть.

**Критерии приёмки:**
- ✅ `runtime/logs/connector.json.log` содержит по строке-JSON на каждый HTTP-запрос.
- ✅ `GET /metrics` возвращает Prometheus-формат с `http_requests_total` и custom-метриками.
- ✅ Внешний monitor дёргает `/health` ежеминутно, отправляет alert при downtime ≥3 мин.
- ✅ pytest-suite зелёная.

**Риски:** минимальные. Middleware — стандартный паттерн FastAPI. `/metrics` не должен ломать существующие routes.

---

### Этап 2. Token lifecycle

**Проблема:** `device_tokens.created_at` есть, `revoked_at` есть, но **нет** `expires_at` и **нет** auto-revoke по inactivity. Если у компании ушёл сотрудник, его токен надо вручную revoke в админке (если админ помнит). Если ноутбук пропал — то же. Это реальный security-risk: бывший сотрудник может SMB-маунтить `\\62.113.36.107\BIM_Models` с любого IP в allowlist'е.

Дополнительно: если admin-API-key утечёт, компрометация **всех** токенов — у админа нет UI-кнопки rotate каждого без вмешательства в БД.

**Цель:** автоматическое expire по политике + ручная rotation за один клик в админке + audit при revoke.

**Что менять:**

1. **Миграции** (в обе папки):
   - `migrations/postgres/0002_token_lifecycle.sql`:
     ```sql
     ALTER TABLE device_tokens ADD COLUMN expires_at TEXT;
     ALTER TABLE device_tokens ADD COLUMN revoke_reason TEXT;
     ALTER TABLE device_tokens ADD COLUMN last_seen_ip TEXT;
     CREATE INDEX idx_device_tokens_expires_at ON device_tokens(expires_at) WHERE expires_at IS NOT NULL;
     ```
   - `migrations/sqlite/0002_token_lifecycle.sql` — аналогично, без частичного индекса.

2. **Конфиг политики** в `config.json`:
   - `token_default_ttl_days: 365` — токен живёт максимум год с создания.
   - `token_inactivity_revoke_days: 90` — auto-revoke если не использовался 90 дней.
   - `token_warning_days_before_expiry: 14` — за 2 недели до истечения предупреждать в `/admin/tokens` (показывать жёлтым).

3. **Background sweep** в startup-задаче (раз в час):
   - SELECT устаревшие токены, UPDATE `revoked_at = now(), revoke_reason = 'auto-expired' OR 'auto-inactive'`.
   - INSERT в `audit_events` каждое revocation.

4. **Endpoint'ы:**
   - `POST /admin/tokens/{device_id}/rotate` — генерит новый token (256-bit URL-safe), revoke'ает старый с `reason='admin-rotated'`, возвращает новый plain-text **один раз**.
   - `POST /admin/tokens/{device_id}/revoke` (был раньше? проверить) — manual revoke с reason из body.
   - `POST /admin/tokens/{device_id}/extend` — продлить `expires_at` на N дней.

5. **Admin UI кнопки** в `admin_ui.html`:
   - Рядом с каждым токеном: «Rotate», «Revoke», «Extend +180d».
   - Цветовая маркировка: зелёный (>60 дней до expire), жёлтый (≤14), красный (revoked).

6. **Telegram alert** (если есть бот) при auto-revoke и при manual rotate.

7. **Backwards compatibility:** существующие токены без `expires_at` — оставить вечными до явного rotate. Применить TTL только к новым (созданным после deploy).

**Оценка трудозатрат:** 1 рабочий день.

**Файлы:**
- `connector/server/migrations/{postgres,sqlite}/0002_token_lifecycle.sql`.
- `connector/server/app.py` — endpoints + sweep task.
- `connector/server/admin_ui.html` — UI кнопки.
- `connector/server/config.example.json` — добавить новые ключи.
- Тесты: `tests/test_token_lifecycle.py` — expire, inactivity, rotate-revoke chain.

**Критерии приёмки:**
- ✅ Создаётся новый токен → у него `expires_at = created_at + 365d`.
- ✅ Через 91 день без heartbeat → auto-revoked, в audit запись.
- ✅ POST /admin/tokens/.../rotate возвращает новый токен и revoke'ает старый. Старый возвращает `401 Invalid token` на следующем `/connect/bootstrap`.
- ✅ Существующий клиент с unrevoked токеном продолжает работать.
- ✅ pytest-suite зелёная.

**Риски:** средние. Самый чувствительный — sweep task, может случайно revoke живые токены, если что-то с timestamp. Mitigation: dry-run mode + сначала только log, потом включить enforcement.

**Подготовка:** перед merge — отдельный pre-flight на staging-DB или на копии прод-DB локально, проверить что sweep ничего лишнего не revoke'ает.

---

### Этап 3. OpenAPI / Swagger UI

**Проблема:** FastAPI автоматически генерирует OpenAPI-схему, но мы её не используем. Это значит:
- Новый разработчик читает `app.py` (3271 строка) чтобы понять API.
- Нет машиночитаемого контракта для интеграций (Bitrix, Speckle, monitoring).
- Нет интерактивной документации для ручного тестирования.

**Цель:** доступная документация API за минимум усилий.

**Что менять:**

1. **Включить docs urls** в `FastAPI()` constructor:
   - `app = FastAPI(title="Structura Connector API", version=_GIT_SHA_SHORT, docs_url="/admin/docs", redoc_url="/admin/redoc", openapi_url="/admin/openapi.json")`.
   - Префикс `/admin/` нужен чтобы Caddy уже-настроенный route `/admin*` → `8080` подхватил без изменений в Caddyfile.
2. **Защита auth-middleware'ом** для `/admin/docs*`, `/admin/openapi.json` — Basic Auth + X-Admin-Key (как остальные admin-endpoints). Даже на публичном repo URL не должен быть доступен анонимно.
3. **Аннотации в коде:**
   - На каждый `@app.get/post/put` добавить `summary` и `description`.
   - Pydantic модели для request/response — где сейчас `dict`, заменить на BaseModel'и (опционально, можно постепенно).
   - Группировка через `tags=["admin"|"device"|"updates"|"tekla"]`.
4. **Tags description** в `app = FastAPI(openapi_tags=[...])` — короткое описание каждой группы.

**Оценка трудозатрат:** 30 мин на включение, 2–3 часа на хорошие descriptions.

**Файлы:**
- `connector/server/app.py` — конфиг + аннотации (постепенно).
- Опционально `connector/server/admin_login.html` — добавить ссылку на `/admin/docs` в шапке.
- Тесты: `tests/test_docs.py` — `/admin/docs` 200, `/admin/openapi.json` JSON, без auth — 401.

**Критерии приёмки:**
- ✅ `https://server.structura-most.ru/admin/docs` показывает Swagger UI с auth.
- ✅ Все endpoints имеют summary.
- ✅ Без X-Admin-Key возвращает 401.

**Риски:** минимальные.

---

## Второй эшелон

Эти этапы — приоритет 2. Делать после первой тройки или параллельно, если задача актуальна.

### Этап 4. Tekla publish preview

**Проблема:** admin кликает «Опубликовать» в desktop UI вслепую, не видя, что именно пойдёт в коммит. Прецеденты бывали (засинхронили обучающие материалы случайно). После коммита откатить тяжело.

**Цель:** показать diff/file list перед commit, дать кнопку «подтвердить» или «отменить».

**Что менять:**

1. На сервере, в Tekla publish flow:
   - После `git add`, до `git commit`, не делать commit сразу. Вернуть API ответом:
     ```json
     {
       "status": "preview",
       "preview_id": "<uuid>",
       "expires_at": "<utc, +5min>",
       "files_added": [{"path": "...", "size": 12345}, ...],
       "files_modified": [{"path": "...", "size_delta": +120}, ...],
       "files_deleted": [{"path": "..."}, ...],
       "total_files": N,
       "total_size_bytes": M
     }
     ```
   - Сохранить `preview_id` → working tree state в server-side cache (in-memory dict с TTL).
2. Новый endpoint `POST /tekla/publish/confirm/{preview_id}` — выполняет `git commit && git push` если preview ещё валиден (не expired, не уже использован).
3. Новый endpoint `POST /tekla/publish/cancel/{preview_id}` — `git reset` working tree.
4. Desktop UI:
   - После «Опубликовать» — показать таблицу changes + кнопки «Подтвердить» / «Отменить».
   - Загрузить с сервера preview, рендерить, ждать выбора.

**Оценка:** 6–8 часов (server + desktop). Если только server-side endpoints — 3–4 часа.

**Файлы:**
- `connector/server/app.py` — preview endpoints + cache.
- `connector-desktop/Connector.Desktop/...` — UI изменения (отдельный коммит desktop релиза).
- Тесты: добавить preview/confirm flow.

**Критерии приёмки:**
- ✅ Preview возвращает корректный список добавлений / изменений / удалений.
- ✅ Confirm с валидным preview_id делает commit+push.
- ✅ Cancel или истечение TTL — `git reset`, ничего не закоммичено.
- ✅ Desktop UI показывает таблицу и кнопки.

**Риски:** stale preview cache — если процесс рестартанул, preview_id невалиден. Mitigation: в audit отдавать понятную ошибку и просить повторить publish.

---

### Этап 5. Audit log retention + индексы

**Проблема:** `audit_events` уже **58k+** строк (по последнему счёту). Через год — сотни тысяч. Сейчас:
- Нет индексов на `event_type`, `device_id`, `created_at` — фильтры в админке через full scan.
- Нет cleanup'а — таблица растёт безгранично.

**Цель:** держать оперативный аудит быстрым (< 100 ms на типичных фильтрах), долгосрочный — в архиве.

**Что менять:**

1. **Миграция** `migrations/postgres/0003_audit_indexes.sql`:
   ```sql
   CREATE INDEX IF NOT EXISTS idx_audit_event_type_created
     ON audit_events(event_type, created_at DESC);
   CREATE INDEX IF NOT EXISTS idx_audit_device_created
     ON audit_events(device_id, created_at DESC) WHERE device_id IS NOT NULL;
   CREATE INDEX IF NOT EXISTS idx_audit_created
     ON audit_events(created_at DESC);
   ```
   Аналогичная sqlite-версия (без `WHERE` в индексе для совместимости со старой sqlite).
2. **Cleanup task** (раз в неделю или daily через Scheduled Task `ConnectorAuditArchive`):
   - Скрипт `scripts/audit_archive.py`:
     - Выбирает события `created_at < now() - 365d`.
     - Дампит их в JSONL-файл `C:\Connector\backup\audit\audit-archive-<year>.jsonl.gz` (`gzip` сразу).
     - DELETE FROM audit_events WHERE id IN (...).
3. **Cursor-based pagination** в `/admin/audit` (если есть такой endpoint) — вместо `LIMIT/OFFSET`. Cursor = `created_at` последней строки.

**Оценка:** 4 часа.

**Файлы:**
- `connector/server/migrations/{postgres,sqlite}/0003_audit_indexes.sql`.
- `scripts/audit_archive.py`.
- Server-side: создание Scheduled Task.
- `connector/server/app.py` — cursor pagination в audit endpoints.
- Тесты: `tests/test_audit_pagination.py`, `tests/test_audit_archive.py`.

**Критерии приёмки:**
- ✅ EXPLAIN на типичный фильтр audit показывает Index Scan, не Seq Scan.
- ✅ После запуска cleanup task: события <1 года в БД, >1 года — в архиве.
- ✅ JSONL.gz открывается и парсится корректно.
- ✅ pytest-suite зелёная.

**Риски:** низкие. DELETE по batch-индексу + транзакция = безопасно.

---

### Этап 6. Admin credentials rotation API

**Проблема:** сейчас, чтобы сменить admin password или X-Admin-Key — это править `runtime/config.json` руками через SSH, потом restart Scheduled Task. Никаких UI-flow.

**Цель:** ротация без SSH, через API + UI в админке.

**Что менять:**

1. Новый endpoint `POST /admin/credentials/rotate-api-key` (auth: Basic Auth с current creds):
   - Генерит новый ключ (256-bit URL-safe).
   - Атомарно обновляет `runtime/config.json` (read → modify → write atomic через `Path.replace`).
   - Возвращает в ответе один раз.
2. Новый endpoint `POST /admin/credentials/change-password`:
   - Body `{"current_password", "new_password"}`.
   - Проверяет current через `verify_admin_password`.
   - Записывает hash нового в config.json.
3. **In-memory cache config'а** — после изменения форсированно перезагружать.
4. Audit-events на каждое изменение.

**Оценка:** half day.

**Файлы:**
- `connector/server/app.py` — endpoints + atomic config write helper.
- `connector/server/admin_ui.html` — формы.
- Тесты: `tests/test_credentials_rotation.py`.

**Критерии приёмки:**
- ✅ После rotate — старый api-key возвращает 401, новый работает.
- ✅ Старый password не работает после change-password, новый работает.
- ✅ Race-condition: одновременные запросы — один выигрывает, другой падает с 409 или повторяется (semaphore).

**Риски:** средние. Atomic write config'а — критично, иначе можем оставить полузаписанный JSON и сервер падает на startup. Mitigation: запись через temp file + atomic rename.

---

## Стратегические — отложено

Делать когда вырастет масштаб. Сейчас — over-engineering.

| Идея | Когда делать |
|---|---|
| **WebSocket для real-time client status** (вместо polling) | Когда heartbeat-задержка до минуты начнёт мешать конкретному use-case |
| **Background task queue** (`arq`/Redis) | Когда HTTP handlers начнут блокироваться на >1 сек на SMB или git операциях |
| **Multi-VPS / HA setup** | Когда single-VPS станет business-критичным риском (резервный сервер с реплицируемой БД) |
| **Read-replica PG для отчётов** | Когда отчёты начнут конкурировать за коннекшены с heartbeat'ом |
| **Telemetry с desktop клиентов** (анонимные ошибки, использование) | Когда захочется data-driven product решения |
| **Cross-product audit reporting** в общий PG-инстанс с Platform | Если возникнет потребность в общей админке для нескольких продуктов |
| **API versioning (`/v1/...`)** | Когда появятся внешние интеграторы вне нашего MSI |

---

## Что не делаем (с обоснованием)

| Идея | Почему не делаем |
|---|---|
| **Staging environment** | Один разработчик, низкая частота релизов, smoke + auto-rollback в CI достаточно. Stagingo стоит timeовых затрат setup'а и поддержки больше, чем экономит. |
| **Full async/await refactor** | Текущий sync FastAPI + uvicorn справляется с нагрузкой (десятки RPS). Async имеет смысл при сотнях RPS или I/O-bound операциях, чего у нас нет. |
| **Containerization Connector** | Connector тесно завязан на Windows-зависимости: `firewall_manager.ps1` (Windows Firewall), `smb_user_manager.ps1` (Windows local accounts). Docker не помогает — наоборот, добавляет проблемы. |
| **Application-level rate limiting на /admin/** | Низкий трафик, защита уже на firewall-уровне (allowlist по `public_ip` клиентов). Дополнительный слой rate-limit нужен при публичной экспозиции, чего у нас нет. |
| **Замена FastAPI на что-то другое** (Flask, Litestar, etc.) | Стек работает, тесты пишутся легко, документация авто-генерится. Замена — чистый risk без выигрыша. |
| **Полный JS-SPA для admin UI** | Текущий vanilla HTML+inline JS работает, время разработки минимальное. SPA имеет смысл при сложной UI-логике, чего у нас нет. |

---

## Открытые вопросы

Эти вопросы нужно решить **прежде** чем начинать соответствующие этапы. Записаны для коллективного обсуждения.

1. **Telegram bot для alerts** — уже есть в инфре? Если нет — заводим? (Этап 1 + 2 хотят alert canal.)
2. **Token TTL по умолчанию** — 365 дней приемлемо? Или короче (90 дней)? Длиннее ставить нельзя — компромисс security vs UX.
3. **Token inactivity threshold** — 90 дней приемлемо? Что с длинными отпусками сотрудников?
4. **OpenAPI публично или за auth** — `/admin/docs` за auth, или открыть как сервисный эндпоинт?
5. **Migration to async** — никогда? Или всё-таки когда-то имеет смысл?
6. **Owner of Telegram alerts** — кому уведомления приходят? Один человек или channel?
7. **Audit retention period** — 365 дней приемлемо? Compliance-требований нет (видимо)?

---

## Рекомендуемая последовательность

```
Сегодня        →  Phase 1 PG migration + cleanup (DONE)
Неделя 1       →  Этап 1. Observability ← рекомендую начать здесь
Неделя 2       →  Этап 2. Token lifecycle
Неделя 2 (par) →  Этап 3. OpenAPI/Swagger (≤ день, делается параллельно)
Неделя 3+      →  Этап 4–6 по приоритетам владельца
Через ≥6 мес   →  Стратегические (когда масштаб реально вырастет)
```

Не делать всё одним большим pull request. Каждый этап — отдельная серия коммитов с green CI, отдельный smoke на проде.

---

## Изменения в этом документе

Каждое значимое решение — добавлять запись.

| Дата | Что | Кто |
|---|---|---|
| 2026-05-09 | Создан после анализа состояния после PG cutover'а | Claude (assisted) |
