# Backup BIM_Models в Я.Диск через rclone

## Что делает

Ежедневно в 03:00 MSK копирует `C:\BIM_Models` (≈9 GB, 25k файлов) на
Yandex.Disk в папку `Structura/BIM_Backup/`. Только изменившиеся файлы
заливаются заново; заменённые/удалённые сохраняются в `archive/<date>/`
для восстановления. Архив хранится 90 дней.

## Архитектура

| Компонент | Где |
|---|---|
| rclone бинарь | `C:\Tools\rclone\rclone.exe` |
| rclone config (с OAuth-токеном) | `C:\Connector\runtime\rclone.conf` (gitignore'д через расположение в runtime\) |
| Backup-скрипт | `C:\Connector\src\scripts\backup_bim_to_yandex.ps1` (в git) |
| Scheduled Task | `ConnectorYandexBackup`, daily 03:00 SYSTEM |
| Логи | `C:\Connector\runtime\logs\backup_yandex.log` |

## Раскладка на Я.Диске

```
Structura/
└── BIM_Backup/
    ├── current/                  ← всегда mirror C:\BIM_Models
    │   ├── Revit/
    │   ├── Tekla/
    │   └── Tokens/
    └── archive/                  ← снимки изменений по датам
        ├── 2026-05-10/
        ├── 2026-05-11/
        └── ...                   ← хранятся 90 дней, потом авто-удаление
```

## Initial setup (одноразовый)

### 1. Получить OAuth-токен Yandex.Disk

OAuth-flow требует браузер, на VPS его нет. Делается на ЛОКАЛЬНОЙ Windows-машине
с браузером:

```powershell
# Скачать rclone (~25 MB, без установки):
$tmp = Join-Path $env:TEMP 'rclone-oauth'
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
Invoke-WebRequest 'https://downloads.rclone.org/rclone-current-windows-amd64.zip' -OutFile "$tmp\rclone.zip"
Expand-Archive "$tmp\rclone.zip" -DestinationPath $tmp -Force
$rclone = (Get-ChildItem $tmp -Recurse -Filter rclone.exe | Select -First 1).FullName

# Запустить OAuth flow — откроется браузер:
& $rclone authorize "yandex"
```

В браузере залогинься в нужный Я.Диск-аккаунт, разреши rclone доступ.
В консоли rclone напечатает однострочный JSON-токен:

```
{"access_token":"y0_AgA...","token_type":"OAuth","refresh_token":"...","expiry":"2026..."}
```

Скопируй **всю строку JSON** (она в одинарных кавычках после `Paste the following into your remote machine -->`). Это и есть твой токен.

### 2. Применить токен на VPS

Передай мне токен (один скопированный JSON-блок), либо сам выполни:

```powershell
# На VPS под opwork_admin (через SSH или RDP):
powershell -NoProfile -ExecutionPolicy Bypass `
    -File C:\Connector\src\scripts\setup_yandex_rclone_config.ps1 `
    -Token '<вставь сюда JSON-токен в одинарных кавычках>'
```

Скрипт создаст `C:\Connector\runtime\rclone.conf` и сделает test-запрос
(`rclone lsd yandex_disk:`) — должен показать список папок Я.Диска.

### 3. Initial backup (manual)

Первый запуск — вручную, чтобы убедиться, что всё работает и оценить время:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
    -File C:\Connector\src\scripts\backup_bim_to_yandex.ps1
```

Первый sync 9 GB по типичной скорости Я.Диска API (~5-10 MB/s) занимает 20-40 мин.
Лог пишется в реальном времени в `C:\Connector\runtime\logs\backup_yandex.log`.

### 4. Scheduled Task

После успешного initial backup'а:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
    -File C:\Connector\src\scripts\setup_yandex_backup_task.ps1
```

Scheduled Task `ConnectorYandexBackup` создан. Можно проверить:

```powershell
Get-ScheduledTask -TaskName ConnectorYandexBackup
```

С этого момента бэкап будет идти автоматически каждую ночь в 03:00 MSK.

## Восстановление файла из бэкапа

### Восстановить актуальную версию

В Я.Диске зайти в `Structura/BIM_Backup/current/<путь к файлу>` — это
последняя сохранённая копия.

### Восстановить старую версию (например, 5 дней назад)

В `Structura/BIM_Backup/archive/2026-05-04/<путь>` — версия файла, которая
была заменена/удалена 4 мая. Открыть, скачать.

### Массовое восстановление за день (rclone)

```powershell
& 'C:\Tools\rclone\rclone.exe' --config C:\Connector\runtime\rclone.conf `
    copy yandex_disk:Structura/BIM_Backup/archive/2026-05-04 `
         C:\BIM_Models_restored
```

## Мониторинг

### Проверить что последний backup прошёл успешно

```powershell
Get-Content C:\Connector\runtime\logs\backup_yandex.log -Tail 5
# Ищи "BACKUP DONE total=..." в конце
```

```powershell
Get-ScheduledTaskInfo -TaskName ConnectorYandexBackup
# LastTaskResult должен быть 0
```

### Альтернатива через rclone

```powershell
& 'C:\Tools\rclone\rclone.exe' --config C:\Connector\runtime\rclone.conf `
    size yandex_disk:Structura/BIM_Backup/current
# Покажет size+count папки current — должно ≈ соответствовать C:\BIM_Models
```

## Что в gitignore / что в репо

В git живут:
- `scripts/backup_bim_to_yandex.ps1` — main script
- `scripts/setup_yandex_rclone_config.ps1` — bootstrap rclone config из токена
- `scripts/setup_yandex_backup_task.ps1` — bootstrap Scheduled Task
- `doc/YANDEX_BACKUP_RU.md` — этот документ

В git **НЕ** живут (на сервере runtime\):
- `C:\Connector\runtime\rclone.conf` — содержит OAuth-токен Я.Диска
- `C:\Connector\runtime\logs\backup_yandex.log` — оперативные логи

## Что трогать нельзя

- **Не удалять `runtime/rclone.conf`** — придётся заново проходить OAuth.
- **Не запускать `rclone sync` руками с другим dest** — может уничтожить уже
  залитое (sync = mirror). Если нужно эксперимент — `rclone copy` (без sync).
- **Не отключать Scheduled Task** без причины — пропуск даже одной ночи
  означает потерю point-in-time снимка для archive/.

## Известные ограничения

- **Открытые/locked файлы** (например, активная Tekla-сессия пишет в `.db1`)
  — rclone скипает с warning, в следующий запуск (на следующий день, когда
  файл закрыт) — заберёт. Не критично, но в логе будут warning'и.
- **Yandex API rate limits** — при 25k файлов первый run может ловить 429.
  `--tpslimit 5` ограничивает 5 RPS — стандартный лимит без проблем.
- **Большие файлы (>2 GB)** — Я.Диск ограничивает single file 50 GB. У нас
  максимум 158 MB (по inventory), запас огромный.

## Будущие улучшения

- **Telegram alert** при failed backup — после реализации Этапа 1 из ROADMAP_RU.md (общая alert-инфра).
- **Зашифровать backup** через `rclone crypt` backend — если бизнес-данные
  считаются чувствительными. Сейчас не делаем (BIM-модели обычно не PII).
- **Метрики backup'а** в Prometheus после Этапа 1 ROADMAP — последний успех,
  размер последнего sync'а, длительность.
