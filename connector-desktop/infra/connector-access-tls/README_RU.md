# Продление TLS-сертификата Platform Connector Access

`renew-platform-connector-certificate.ps1` — post-renewal hook win-acme. Он принимает PFX, уже экспортированный store plugin, проверяет serverAuth, private key, срок и оба DNS SAN, атомарно устанавливает его в `ConnectorAccess:ServerCertificatePath`, перезапускает только `PlatformServerApp` и ждёт HTTPS `/api/platform/health` на Windows loopback:24443. TLS использует сертификатный target hostname `connector-access.structura-most.ru` с обычной проверкой Windows/.NET цепочки и SAN; дополнительно сравнивается thumbprint с активированным PFX. Hook не меняет DNS или hosts.

Hook устанавливается в persistent runtime как `C:\Platform\runtime\connector-access\renew-platform-connector-certificate.ps1`. Путь store plugin — staging-файл в том же каталоге: `C:\Platform\runtime\connector-access\renewed-server.pfx`. Активный путь должен совпадать с `ConnectorAccess:ServerCertificatePath`; каталог и его предки до `C:\Platform` заранее создаёт администратор, права на запись в них должны быть только у SYSTEM, Administrators, Platform runtime principal или identity renewal task. ACL staging настраивается до копирования private bytes. Активный PFX и backup дают Platform principal только чтение; hook identity имеет FullControl для замены и очистки backup. Если hook и непривилегированный Platform principal совпадают, операция завершается fail-closed: настроить отдельную renewal identity. Для win-acme PFX store задайте тот же пароль через защищённый secret vault (`PfxFile.DefaultPassword`), а для hook доступен только machine environment variable `PLATFORM_CONNECTOR_SERVER_PFX_PASSWORD`. Пароль не передаётся hook в argv и не выводится. В конфигурации win-acme передавать hook только `{StorePath}`; не передавать `{CachePassword}`/`{1}`.

Пример параметров installation script после настройки `pfxfile` store:

```text
--installation script --script C:\Platform\runtime\connector-access\renew-platform-connector-certificate.ps1 --scriptparameters "-SourcePfxPath {StorePath} -ActivePfxPath C:\Platform\runtime\connector-access\server.pfx"
```

До включения renewal администратор должен согласовать `ServerCertificatePath`, staging path, одинаковый PFX password в win-acme store и машинной переменной, а также ACL. Hook fail-closed проверяет Scheduled Task `PlatformServerApp`: ровно одно действие должно указывать на `C:\Platform\runtime\runtime_launch.ps1`. Процесс останавливается только при точном совпадении runtime `C:\Platform\runtime\current\Platform.Server.dll` + `C:\Platform\dotnet\dotnet.exe` либо `Platform.Server.exe` в этом каталоге и неизменной идентичности PID/start time/path/command line.

В win-acme актуально документированы `PfxFile.DefaultPassword` с secret-vault reference и параметры PFX store / installation script: [settings.json](https://www.win-acme.com/reference/settings), [PFX file store](https://www.win-acme.com/reference/plugins/store/pfxfile), [script installation](https://www.win-acme.com/reference/plugins/installation/script). Не помещайте пароль в `--scriptparameters`.

На время операции используется общий release lock `C:\Platform\deploy\platform-server.deploy.lock`, поэтому TLS hook и verified release не меняют runtime параллельно. Старый PFX сохраняется как `.previous` до успешного HTTPS smoke. При неуспехе hook останавливает только процесс, запущенный им самим, возвращает предыдущий PFX и проверяет его через тот же listener. Если identity процесса или rollback подтвердить нельзя, hook возвращает ошибку и сохраняет backup для ручного разбора.

Hook требует Windows PowerShell 5.1 Desktop и .NET Framework 4.8+. Он использует совместимые Begin/End async API; новые .NET runtime не нужны. Проверка fixture без сетевого доступа и без остановки реального процесса:

```powershell
pwsh -NoProfile -File .\test-renew-platform-connector-certificate.ps1
```

Скрипт проверяет DER DNS SAN декодирование, блокировку незащищённого parent ACL, права runtime на staging, общую deadline для медленного async ответа, а также реальные PFX state/atomic replace/rollback при injected active ACL validation, restart и health failures через fixture task/process adapters. Реальные Scheduled Task identity и HTTPS listener этим тестом не проверяются.
