# Structura Connector — заметки для Claude

> Этот файл подгружается автоматически в каждую сессию Claude Code в этом проекте. Он не заменяет код, а фиксирует то, что не видно из git/кода: инфраструктуру, конвенции, подводные камни и текущие риски.

## 1. Что это за продукт

Windows-продукт для удалённой работы с BIM-инфраструктурой. GitHub: `PROBIM55/ServerConnector`.

> ⚠️ **Активная ветка — `master`, не `main`.** `main` на GitHub помечен как default, но содержит только Initial commit с голым README.md. Весь код, история релизов и теги (v1.0.1…v1.0.20) живут на `master`. После `git clone` сразу делать `git checkout master`.

Состоит из двух частей:

- **`connector/server`** — FastAPI/Python (3.11+, FastAPI, SQLite, PowerShell для firewall/SMB).
- **`connector-desktop/Connector.Desktop`** — WPF/.NET 8, MSI через WiX, релизы через GitHub Actions.

Бизнес-логика в одном абзаце: пользователь вводит токен в desktop Connector → сервер возвращает device session, SMB-доступ и update endpoints → клиент работает в фоне (heartbeat, SMB mount, авто-обновление). Для Tekla пользователи работают не с сетевым runtime, а с локальной копией `C:\Company\TeklaFirm`, которую Connector держит в актуальном состоянии.

## 2. Прод-инфраструктура

| Компонент | Адрес/путь |
|---|---|
| VPS host | `62.113.36.107` (Windows Server 2022, hostname `VDSWIN2K22`) |
| Домен | `server.structura-most.ru` |
| Reverse proxy | Caddy → backend `127.0.0.1:8080` |
| SMB share | `\\62.113.36.107\BIM_Models` ↔ `C:\BIM_Models` |
| Server runtime | `C:\Connector\server` (`config.json`, `connector.db`, логи) |
| Tekla repo worktree | `C:\Connector\tekla-firm-standard` (`PROBIM55/tekla-firm-standard`, ref `main`) |
| Tekla на клиентах | `C:\Company\TeklaFirm` (локальная копия из subdir `XS_FIRM`) |
| Соседний сервис | `budget.structura-most.ru` — **не ломать**, отдельный продукт |

Managed firewall ports: `80, 443, 445, 1238, 3389`.

## 3. Ключевые API endpoints

| Endpoint | Метод | Auth | Назначение |
|---|---|---|---|
| `/health` | GET | — | Public health check |
| `/connect/bootstrap` | POST | `X-Device-Token` | Логин клиента → device_id, session_id, SMB, heartbeat interval, manifest URL |
| `/heartbeat` | POST | `X-Device-Token` + `X-Device-Session` | Один токен = одна активная сессия |
| `/updates/latest.json` | GET | — | Manifest desktop-обновлений (берёт из GitHub Releases) |
| `/admin/login` | POST | JSON `{username,password}` → 303 | Логин админа |
| `/admin/tokens` | GET | **`X-Admin-Key`** или Basic Auth | Список токенов и SMB-кредов |
| `/admin/updates/refresh` | POST | admin | Сброс кэша update manifest |

> ⚠️ **Документационный дрейф:** в `CREDENTIALS.md` admin-заголовок назван `X-Admin-Api-Key`, но рабочий — **`X-Admin-Key`**. Сам API-ключ корректный, имя поля в доке устаревшее.

## 4. Релиз-флоу desktop

1. Локально собрать MSI: `powershell -NoProfile -ExecutionPolicy Bypass -File "connector-desktop/build_msi.ps1"`
2. Закоммитить изменения.
3. `git tag vX.Y.Z`
4. `git push origin master vX.Y.Z`
5. GitHub Actions (`.github/workflows/release-connector-desktop.yml`) собирает MSI и публикует Release.
6. При необходимости обновить server-side кэш: `POST /admin/updates/refresh`.

## 5. Tekla publish — критичные правила

Реальный admin_firm flow в desktop UI отдаёт серверу только `source_path` + `comment`. Сервер сам делает sync/git add/commit/push, считает version и revision из git.

**Подводные камни:**

- `source_path` должен быть **server-visible** — UNC-путь, не клиентский mapped drive (например, `P:\Tekla\...` не сработает). Ошибка `source_path must point to an existing directory` = указан путь, который сервер не видит.
- Admin **не** вводит version/revision вручную — генерируются на сервере.
- Серверный корень: `\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ`. Синхронизируется из подпапки `01_XS_FIRM`. Остальные подпапки (обучение, видео, плагины и т.д.) **не должны** синхронизироваться.
- Tekla runtime у пользователей умышленно локальный (`C:\Company\TeklaFirm`), не сетевой — стабильнее, не зависит от SMB во время работы.

## 6. Где лежат секреты (значения **не** в этом файле)

| Что | Где |
|---|---|
| SSH ключ к VPS | `.secrets/opwork_vps_key` (`opwork_admin@62.113.36.107`) |
| Admin login/password/API key | `CREDENTIALS.md` |
| Runtime config (источник правды) | `C:\Connector\server\config.json` на проде |
| GitHub auth | `gh auth status` (Account `Scccoco`, keyring) |
| GitHub Actions secrets | repo settings, plaintext недоступны |

> ⚠️ **`CREDENTIALS.md` лежит в репозитории с реальными паролями.** Перед любым `git add`/`git push` проверить `.gitignore` и `git log --all -- CREDENTIALS.md`. Если он попал в push на `origin` — пароль/API-ключ/SMB-креды скомпрометированы и подлежат ротации.

## 7. Состояние локальной копии (важно при онбординге)

**Локальная копия частично уничтожена** — много файлов обнулено (NUL-байты, размер сохранён), исходного кода `connector/server` и `connector-desktop/Connector.Desktop` фактически нет на диске, `.git` имеет corrupted loose object (`6eb0752f…`) и broken-name теги (`V10~1.10`...).

Уцелели: `RECOVERED_CONTEXT_RU.md`, `RECOVERED_ACCESS_POINTERS_RU.md`, `CREDENTIALS.md`, `SETUP_NOTES.md`, GitHub remote, `.secrets/`.

**Не пытайся чинить `.git` или восстанавливать обнулённые файлы.** Для работы:

- переклонировать рядом: `git clone https://github.com/PROBIM55/ServerConnector.git && cd ServerConnector && git checkout master` (на `main` пусто — см. п.1).
- либо вытягивать актуальное с прод-сервера (`C:\Connector\server\`).

**Уже клонировано:** `E:\00_Cursor\18_Server_fresh` (ветка `master`, HEAD на v1.0.20-tier коммите). Использовать как рабочую копию.

Локальный last commit: `eda421d Release 1.0.15`. Прод сейчас на **1.0.20** (release 2026-04-16). Локальный репо отстаёт на 5 релизов — это следствие повреждения, а не «нормальное» состояние.

## 8. Известные инциденты и выводы (для будущих сессий)

- **«Медленный SMB»** часто = заполненный диск C: на VPS. При увеличении диска через панель VPS не забыть расширить том в Windows.
- **«Токены пропали в админке»** = UI-баг (`deviceIdValue` использовался до инициализации в `admin_ui.html`), а не потеря данных в БД. Различать UI-поломку и data loss.
- **`HTTP 401: Invalid token`** обычно = устаревший client-side cached token, не всегда runtime-проблема.
- **Нестабильный runtime сервера** проявлялся как дубли uvicorn / bind-ошибки на 8080 — держать один консистентный production launch path.
- **Ошибка SSH-команд через PowerShell-сессию:** при `ssh ... "cmd1; cmd2"` точки с запятой парсятся PS-сессией на удалённой стороне как разделители команд PS — может ломаться. Безопаснее: `ssh ... "cmd /c echo ..."` или однострочные команды.

## 9. Конвенции

- **Не класть реальные секреты в `.md` репозитория.** Только pointers и описание назначения.
- **Не полагаться на старую документацию,** если она расходится с реальным UI/кодом — проверять прод-факт через код + runtime + логи.
- **Документация — на русском.** Соответствуй стилю существующих `*_RU.md`.
- **Перед изменением `CREDENTIALS.md`** — убедиться, что он в `.gitignore` и не в публичной истории.
- **Не делать destructive git** (`reset --hard`, force-push, удаление веток) без явного подтверждения от пользователя — особенно учитывая текущее повреждение `.git`.

## 10. Полезные одноразовые проверки

```powershell
# Жив ли сервер
curl.exe -sS https://server.structura-most.ru/health

# Текущая версия desktop
curl.exe -sS https://server.structura-most.ru/updates/latest.json

# Список токенов (admin)
curl.exe -sS -H "X-Admin-Key: <key>" https://server.structura-most.ru/admin/tokens

# SSH (Windows OpenSSH)
ssh -i .secrets\opwork_vps_key opwork_admin@62.113.36.107 "cmd /c echo %USERNAME% %COMPUTERNAME%"

# GitHub
gh repo view PROBIM55/ServerConnector --json name,defaultBranchRef,latestRelease
```

## 11. Указатели на восстановленный контекст

Если нужно глубже — читать в порядке убывания актуальности:

1. `RECOVERED_CONTEXT_RU.md` — продукт, инфра, инциденты, релизы, Tekla-модель.
2. `RECOVERED_ACCESS_POINTERS_RU.md` — карта секретов и где они живут.
3. `CREDENTIALS.md` — фактические значения (с осторожностью, см. п. 6).
4. `SETUP_NOTES.md` — короткая выжимка по VPS/портам.

Остальные `*_RU.md` в корне и `connector-desktop/` сейчас обнулены — игнорировать локально, читать только с GitHub после переклонирования.
