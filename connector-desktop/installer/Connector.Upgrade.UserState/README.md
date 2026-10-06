# Snapshot пользовательского состояния при замене MSI

Компонент сохраняет **полные деревья файлов** прежних Structura Connector и
Platform Connector, а также нового каталога настроек Platform внутри единого
коннектора. Он не разбирает и не пересериализует JSON, не расшифровывает
DPAPI и не выбирает известный список файлов. Поэтому `settings.json`, неизвестные
поля, токены, `managed-sync`, `bundled-tools`, логи и backup-файлы переносятся как
исходные байты.

Три канонических корня задаёт `LegacyUserStateRoots`:

- `%LocalAppData%\ConnectorAgentDesktop`;
- `%LocalAppData%\Platform\Connector`;
- `%LocalAppData%\Structura Connector\Platform` — новое состояние; при откате
  восстанавливаются его прежние байты либо его отсутствие до миграции.

`CreateSnapshotAsync` сначала инвентаризует всё дерево, затем потоково копирует и
SHA-256-хеширует каждый файл, повторно сверяет дерево и исходные хеши и только
после этого атомарно публикует каталог snapshot. Handle закрепляет SID, размер и
SHA-256 manifest. Неполный `.partial`, отсутствующий `commit.json`, изменённый
manifest или payload не принимаются. Лимиты файлов, общего объёма, глубины,
числа записей, buffer и manifest явные; дефолт покрывает измеренные каталоги
порядка 1 GiB без загрузки содержимого файлов в память.

Публичный service не принимает source/target paths: production policy всегда
возвращает ровно три канонических корня текущего `%LocalAppData%`. Подмена путей и
SID доступна только friend test assembly. Перед применением вызывающий код
получает `InspectTargetsAsync` и передаёт fingerprint в `ApplySnapshotAsync`.

Incoming, backup и `apply-journal.json` находятся в аттестованном stage того же
тома. Temp journal сбрасывается через `Flush(true)`, затем на Windows публикуется
через `MoveFileEx` с `MOVEFILE_WRITE_THROUGH` до первого target rename и после
каждого перехода.
`RecoverInterruptedApplyAsync` сначала проверяет manifest, SID, канонические пути,
fingerprint всех трёх target/incoming/backup и только затем продолжает незавершённый
swap. Неожиданное изменение любого корня блокирует recovery до новых записей.
`ListPendingRecoveryOperationsAsync` различает committed journal, единственный
проверяемый temp journal и orphan workspace, требующий ручного восстановления.

## Обязанности установщика

`IProtectedUserStateStage` намеренно не имеет небезопасной реализации по
умолчанию. Повышенная часть установщика должна заранее создать локальный stage,
проверить владельца и DACL для ожидаемого SID, Administrators и SYSTEM, запретить
запись остальным пользователям и аттестовать это в
`AssertProtectedForUserAsync`. Stage нельзя размещать внутри source/target или в
доверенном только по имени пользовательском каталоге. Его нужно сохранять до
окончательной проверки нового клиента и удаления recovery-потребности.

Установщик обязан остановить/штатно закрыть оба legacy-процесса. Snapshot не
является VSS-снимком; изменение дерева во время двух проходов приводит к отказу.
Операция привязана к текущему Windows SID, поэтому elevated-процесс под другим
пользователем отклоняется. Reparse points/symlinks и NTFS named streams приводят
к отказу: они не обходятся и не отбрасываются молча. ACL, owner, creation/access
time и EFS-контекст файлов не переносятся; стандартный поток, относительный путь,
пустые каталоги, attributes и last-write time сохраняются. Между заменами каталогов
нет общей файловой транзакции; durable journal обеспечивает проверяемое
продолжение после внезапного завершения. Stage и все три канонических корня должны
быть на одном томе, иначе операция прекращается до mutation. Интеграции с
`Connector.Upgrade.Core` в этом пакете нет.
