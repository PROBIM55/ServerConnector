# Windows protected rollback payload

Этот пакет — узкий Windows-адаптер для `IVerifiedRollbackPayloadLease`. Он не
запускает MSI, не удаляет установленные продукты и не меняет пользовательское
состояние.

Источник истины — встроенный в `Connector.Upgrade.Core` lock двух legacy MSI.
Для каждого payload проверяются точные `ProductCode`, `UpgradeCode`, версия,
имя, размер и SHA-256 до и после копирования. Источник открывается без sharing
на запись/удаление, а staging-файл остаётся открытым тем же `SafeFileHandle` до
`DisposeAsync`. Rollback-адаптер должен использовать `StagedPath` именно этого
живого `IWindowsVerifiedRollbackPayloadLease`; копия или исходный пользовательский
путь не являются допустимым rollback payload.

User-owned build source может иметь пользовательский write ACL: доверие к его
пути не требуется. Подмена блокируется открытым source handle, а байты и MSI
identity сверяются с embedded lock до и после копирования. Поле Core
`RollbackPayloadInspection.TrustedSource=true` означает аутентифицированное
точное содержимое уже в защищённом destination staging, а не trusted ACL
исходного пути.

Production staging разрешён только под `ProgramData`/`Program Files`, из
elevated admin, SYSTEM или TrustedInstaller. Общие каталоги используют единый
защищённый DACL: SYSTEM, Administrators и TrustedInstaller имеют полный доступ,
`BUILTIN\\Users` — только чтение и выполнение. Безопасные legacy ACL общих
каталогов мигрируют после предварительной проверки всей существующей цепочки.
Batch-каталоги и payload-файлы остаются machine-only. Владельцы,
write/delete/change-ACL grants и reparse-компоненты повторно проверяются. После
сбоя процесса durable `HandleId` открывает тот же staging-путь заново и повторяет
все content/identity/ACL проверки.

Граница угроз: обычный пользователь и подмена user-owned build artifact входят
в модель. Администратор, SYSTEM/TrustedInstaller, компрометация elevated installer
или замена встроенного lock при сборке считаются доверенной границей. Хранилище
намеренно не чистит старые batch-каталоги: политика retention относится к
установщику, который знает состояние журнала восстановления.
