# Восстановленный контекст проекта Structura Connector

Этот файл собран для восстановления утраченного локального контекста по проекту. Он фиксирует текущее понимание продукта, инфраструктуры, процессов релиза, деплоя и недавних проблем.

Важно:

- это восстановленный контекст, а не исторический архив всех утраченных файлов;
- реальные секреты не записываются сюда в открытом виде;
- для секретов и доступов см. `RECOVERED_ACCESS_POINTERS_RU.md` и локальные private-файлы.

## 1. Что это за продукт

`Structura Connector` — это Windows-centric продукт для удаленной работы с BIM-инфраструктурой.

Он состоит из двух основных частей:

- сервер `connector/server` на FastAPI/Python;
- desktop-клиент `connector-desktop/Connector.Desktop` на WPF/.NET 8.

Основные задачи продукта:

- подключение пользователя по токену;
- bootstrap параметров устройства с сервера;
- heartbeat и мониторинг состояния клиента;
- выдача SMB-доступа к `BIM_Models`;
- управление firewall allowlist под текущий IP пользователя;
- автообновление desktop-клиента;
- публикация и раскатка папки фирмы Tekla.

## 2. Бизнес-логика в одном абзаце

Пользователь вводит только токен в desktop Connector. Сервер возвращает device session, SMB-доступ и update endpoints. После этого клиент работает в фоне, отправляет heartbeat, подключает SMB и может обновляться. Для Tekla пользователи работают не напрямую с сетевой runtime-папкой, а с локальной копией `C:\Company\TeklaFirm`, которую Connector поддерживает в актуальном состоянии.

## 3. Актуальная инфраструктура

Прод-топология:

- домен Connector: `server.structura-most.ru`
- reverse proxy: Caddy
- backend Connector API/UI: `127.0.0.1:8080`
- сервер: Windows VPS
- SMB share: `\\62.113.36.107\BIM_Models`

Соседний сервис:

- `budget.structura-most.ru`
- это отдельный продукт, его нельзя случайно ломать при изменениях Connector.

## 4. Ключевые runtime-пути

На сервере:

- код/рантайм сервера: `C:\Connector\server`
- runtime config: `C:\Connector\server\config.json`
- runtime DB: `C:\Connector\server\connector.db`
- логи и служебные файлы также живут в `C:\Connector\server`

У пользователя на ПК:

- локальные настройки клиента: `%LOCALAPPDATA%\ConnectorAgentDesktop\settings.json`
- локальная папка фирмы Tekla: `C:\Company\TeklaFirm`

## 5. Технологии

Server:

- Python 3.11+
- FastAPI
- SQLite
- PowerShell-скрипты для firewall/SMB automation

Desktop:

- C#
- WPF
- .NET 8
- DPAPI для шифрования токена

Installer/release:

- WiX MSI
- GitHub Actions
- GitHub Releases

## 6. Основные API и сценарии

### Bootstrap

- `POST /connect/bootstrap`
- токен передается в `X-Device-Token`
- сервер возвращает `device_id`, `session_id`, SMB-доступ, heartbeat interval, update manifest URL, роль `admin_firm`

### Heartbeat

- `POST /heartbeat`
- клиент отправляет `X-Device-Token` и `X-Device-Session`
- действует правило: один токен = одна активная сессия

### Desktop updates

- `GET /updates/latest.json`
- сервер обычно берет latest release из GitHub Releases
- есть ручной refresh кэша через `POST /admin/updates/refresh`

### Tekla publish

Реальный пользовательский admin_firm flow идет через desktop UI, а не через ручное редактирование manifest.

Desktop отправляет на сервер только:

- `source_path`
- `comment`

Сервер сам:

- проверяет source;
- синкает в git-worktree;
- делает `git add/commit/push`;
- вычисляет следующую версию;
- получает ревизию из git commit;
- обновляет Tekla manifest.

## 7. Папка фирмы Tekla — текущая модель

Серверный корень:

- `\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ`

Синхронизируемая runtime-папка:

- `\\62.113.36.107\BIM_Models\Tekla\02_ПАПКА ФИРМЫ\01_XS_FIRM`

Именно `01_XS_FIRM` публикуется и уходит пользователям локально в:

- `C:\Company\TeklaFirm`

Остальные подпапки внутри `02_ПАПКА ФИРМЫ` могут содержать:

- обучение;
- видео;
- установщики;
- плагины;
- скрипты;
- прочие вспомогательные материалы.

Они не должны синхронизироваться через Connector в локальную runtime-папку.

## 8. Почему Tekla runtime локальный, а не сетевой

Это умышленная архитектура.

Причины:

- Tekla стабильнее работает с локальными файлами;
- меньше зависимость от качества SMB и интернета во время рабочей сессии;
- легче обеспечить одинаковую целевую версию у всех пользователей;
- ниже риск случайного редактирования боевого runtime напрямую на сетевой папке.

## 9. Реальный интерфейс публикации Tekla

Это критично, потому что документация уже расходилась с фактом.

В desktop UI во вкладке `Стандарт Tekla` у `admin_firm` реально есть:

- поле `Путь к эталонной папке`
- поле `Комментарий`
- кнопка `Опубликовать`

Важно:

- администратор не вводит version и revision вручную в desktop flow;
- version/revision генерируются на сервере автоматически;
- ошибка `source_path must point to an existing directory` возникает, если указан путь, которого сервер не видит.

### Очень важный gotcha по source_path

Путь для публикации должен быть виден серверу.

Нельзя указывать клиентский mapped drive, например:

- `P:\Tekla\02_ПАПКА ФИРМЫ\01_XS_FIRM`

Нужно использовать server-visible path, предпочтительно UNC.

## 10. Недавние реальные инциденты и выводы

### 10.1 Почти заполненный диск C:

Симптомы:

- очень медленный SMB;
- нестабильная работа сервера.

Выяснено:

- на уровне VPS диск был увеличен, но Windows volume не был расширен;
- после расширения `C:` скорость SMB восстановилась.

Вывод:

- при изменении размера диска на VPS нужно дополнительно расширять том в Windows.

### 10.2 Токены «пропали» в админке

Симптом:

- в UI казалось, что токены исчезли.

Фактически:

- токены в БД были на месте;
- причиной был frontend bug в `admin_ui.html` (`deviceIdValue` использовался до инициализации).

Вывод:

- различать UI-поломку и потерю данных в БД.

### 10.3 Ошибка `Invalid token`

Симптом:

- пользователи получали `HTTP 401: {"detail":"Invalid token"}`.

Фактически:

- часть токенов на клиентах была устаревшей;
- сервер в некоторых моментах также страдал от нестабильного runtime-запуска.

Вывод:

- смотреть и токен в БД, и runtime API availability, и client-side cached token.

### 10.4 Нестабильный runtime Connector

Обнаруживалось:

- дубли uvicorn;
- конфликтующие способы запуска;
- bind-ошибки на `8080`.

Вывод:

- нужно держать один консистентный production launch path.

## 11. Release-линия, известная по текущему состоянию

По текущему git-логy видны как минимум:

- `v1.0.11`
- `v1.0.12`
- далее release-коммиты `1.0.13`, `1.0.14`, `1.0.15`

Текущая release-линия уже ушла дальше, чем ранний ручной контекст. Новому агенту нужно ориентироваться на текущее состояние репозитория и runtime, а не на старые заметки.

## 12. Как делаются релизы desktop

Фактический flow:

1. локально собрать MSI:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "connector-desktop/build_msi.ps1"
```

2. закоммитить изменения;
3. создать tag `vX.Y.Z`;
4. `git push origin master vX.Y.Z`;
5. GitHub Actions workflow собирает MSI и публикует GitHub Release;
6. затем нужно обновить кэш server update manifest, если он еще не подхватил новый release.

Workflow:

- `.github/workflows/release-connector-desktop.yml`

## 13. Как делается deploy/ops

Ключевые вещи:

- проверять `/health`;
- проверять `/admin/login` и `/admin/tokens`;
- проверять bootstrap реальным токеном;
- проверять `updates/latest.json`;
- если нужно — обновлять manifest cache через `/admin/updates/refresh`.

## 14. Что уже подготовлено в документации

Сейчас в repo уже есть полезные документы:

- `MIGRATION_PLAYBOOK_RU.md`
- `CONNECTOR_USER_GUIDE_RU.md`
- `FIRM_FOLDER_USER_GUIDE_RU.md`
- `FIRM_FOLDER_ADMIN_GUIDE_RU.md`
- `RECOVERED_ACCESS_POINTERS_RU.md`

## 15. Что важно помнить новому агенту

1. Не класть реальные секреты в git-документы.
2. Не полагаться на старую документацию, если она расходится с реальным UI.
3. Проверять прод-факт через код + runtime + логи, а не только через интерфейс.
4. Для Tekla publish всегда помнить: source path должен быть server-visible.
5. При проблемах с SMB всегда учитывать:
   - состояние диска `C:`;
   - сетевую нестабильность;
   - Windows Defender scanning;
   - фактический путь и права на share.
