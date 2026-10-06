# Structura Connector Desktop

## Единая кодовая база

Все исходники объединенного Connector находятся в этом репозитории:
`https://github.com/PROBIM55/ServerConnector`. Сборка не обращается к соседним
папкам Платформы или Structura. Исходники импортированы с точными версиями;
`source-provenance.json` хранит исходные commits и SHA-256 для истории, а не
создает механизм синхронизации с прежними проектами.

- `Connector.Desktop/` — существующая WPF-оболочка и функции Structura.
- `src/` — общий runtime и AGR executor, переиспользованные из Платформы.
- `cad/` — адаптеры AutoCAD/Tekla, исходники Tekla bridge и plugins.
- `shared/` — контракты и геометрические библиотеки, необходимые CAD-модулям.
- `runtime/agr-blender/` — обработчик Blender и manifest его версии.
- `workers/ifc-optimizer/` — IFC engine 0.1.2, исходники, тесты и packaging entrypoint.
- `tests/`, `Connector.*.Tests/` — тесты исходных компонентов и объединения.
- `design/` — согласованный Graphite review-интерфейс и знак Платформы.
- `scripts/` — проверка границ, сборка и подготовка пакетных обновлений.

`Unified.Connector.sln` объединяет оболочку, runtime, адаптеры и тесты.
Проверка границ контролирует локальные MSBuild-ссылки и известные привязки
скриптов к прежним checkout/config; это не проверка всех runtime-сценариев.
Служебный экспорт каталога Tekla получает подключение к БД из переменной
`CONNECTOR_CATALOG_DATABASE_CONNECTION_STRING`, без чтения конфигов Платформы;
`psql` ищется в PATH или задается через `CONNECTOR_PSQL_EXECUTABLE`.

```powershell
./scripts/verify_source_boundaries.ps1
dotnet build Connector.Desktop/Connector.Desktop.csproj -c Debug -r win-x64 -p:RestoreLockedMode=true
```

Нативным Tekla bridge/plugins по-прежнему необходим официальный Tekla SDK,
IFC engine — Python 3.11/3.12 и зависимости из `pyproject.toml`; Blender и CAD
устанавливаются как внешние runtime. Это не зависимость от чужих исходников.
Перенос исходников не означает завершение общей очереди, VPN, WPF redesign или
подключения IFC worker. Текущий статус и порядок работ —
[`рабочий чек-лист`](docs/UNIFIED_CONNECTOR_CHECKLIST_RU.md);
[`архитектура и решения`](docs/UNIFIED_CONNECTOR_PLAN_RU.md) сохранены отдельно.

## Текущая функциональность

- Токеновый вход: пользователь вводит только токен.
- Bootstrap параметров с сервера (сессия устройства, настройки доступа, проверка связи).
- Фоновая отправка служебного сигнала связи после успешного подключения.
- Локальное хранение чувствительных данных через DPAPI (`CurrentUser`).
- Работа в трее при закрытии окна.
- Автозапуск в Windows (`HKCU\...\Run`).

## Логика обновлений

- Manifest URL: `https://server.structura-most.ru/updates/latest.json`.
- Проверка обновлений при запуске и каждые 30 минут.
- Manifest и MSI принимаются только по HTTPS; перед запуском установщика
  Connector сверяет его SHA-256 с digest релизного артефакта.
- В интерфейсе одна кнопка действия:
  - `Проверить обновление`;
  - `Скачать и установить` (если найдена более новая версия).
- После обновления токен сохраняется, повторная авторизация не требуется.

## Ограничение по сессиям токена

- Один токен поддерживает одну активную машину в момент времени.
- Новое подключение тем же токеном деактивирует предыдущую сессию.
- Для параллельной работы на нескольких ПК необходимы разные токены.

## Релизы

- Публикация пользовательского MSI выполняется через GitHub Releases.
- Актуальный артефакт релиза: `Connector.Desktop.Setup.msi`.

## Локальная сборка MSI

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "connector-desktop/build_msi.ps1"
```

Во время сборки `build_msi.ps1` автоматически подготавливает встроенный bundled git (MinGit) в `Connector.Desktop/tools/git`, чтобы клиентам не требовалась отдельная установка Git.

## Самодостаточный пакет обновления

`scripts/build_package_update.ps1` собирает новый Velopack-пакет в отдельную
GUID-папку `artifacts/package-update/<channel>/<guid>`. Он не использует
установленный у пользователя IFC engine: путь к заранее подготовленному
`structura-ifc-optimizer.exe` передаётся явно и сверяется с репозиторным
`workers/accepted-engines.json` до копирования. CLI SHA-256 — дополнительная
необязательная проверка. Скрипт также проверяет закреплённый `gltfpack.exe`, набор
Assimp/native-зависимостей и выполняет restore в locked mode.

```powershell
$ifcWorker = 'D:\release-input\structura-ifc-optimizer.exe'
$ifcWorkerSha256 = '<64-hex-sha256-of-that-file>'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build_package_update.ps1 `
  -FeedUrl 'https://updates.example.invalid/connector' -PackVersion '1.0.32' -Channel 'stable' `
  -IfcWorkerPath $ifcWorker -IfcWorkerSha256 $ifcWorkerSha256
```

В publish-артефакт попадают `gltfpack.exe`, private
`workers/ifc-optimizer/structura-ifc-optimizer.exe` и
`workers/engine-manifest.json` с версиями, протоколом и SHA-256. Отсутствующий
файл или несовпадающий digest останавливает упаковку. Сетевую публикацию скрипт
не выполняет. Воспроизводимый source-built IFC worker для CI — отдельная
будущая задача; текущий входной бинарник должен быть предоставлен release
процессом.

Для локальной приёмки без опубликованного канала используется `-LocalOnly`
вместо `-FeedUrl`. Скрипт создаёт самодостаточную папку приложения, portable
ZIP / установочный пакет и `local-package.json`. Пользовательская установка,
настройки и сервер не изменяются. `feedUrl` остаётся пустым; проверка обновлений
в managed-установке останавливается с явным сообщением до настройки канала.

```powershell
.\scripts\build_package_update.ps1 -LocalOnly -PackVersion '1.1.0-preview.1' `
  -Channel 'preview' -IfcWorkerPath $ifcWorker
```

## Smoke check Tekla Adapter

Быстрая проверка manifest и базового контура:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "connector-desktop/scripts/tekla_adapter_smoke_check.ps1" -ServerBaseUrl "https://server.structura-most.ru"
```

## Документы по Tekla Standard Adapter

- `connector-desktop/CONNECTOR_TEKLA_ADAPTER_PLAN_RU.md`
- `connector-desktop/CONNECTOR_TEKLA_ADAPTER_CHECKLIST_RU.md`
- `connector-desktop/CONNECTOR_TEKLA_ADAPTER_PHASES_RU.md`
- `connector-desktop/TEKLA_STANDARD_PUBLISH_GUIDE_RU.md`
- `connector-desktop/TEKLA_STANDARD_USER_GUIDE_RU.md`
- `connector-desktop/TEKLA_STANDARD_RUNBOOK_RU.md`
- `connector-desktop/TEKLA_XS_FIRM_SETUP_RU.md`
- `connector-desktop/TEKLA_STANDARD_TEST_PROTOCOL_RU.md`
