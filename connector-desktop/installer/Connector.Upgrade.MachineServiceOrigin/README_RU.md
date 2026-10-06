# Проверка происхождения machine pipe

`MachineServiceOriginVerifier.AssertTrustedServer` сейчас намеренно всегда отказывает. Официальный контракт [`GetNamedPipeServerProcessId`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid) требует handle, созданный `CreateNamedPipe`; client владеет handle, открытым как клиентский endpoint. В локальном loopback native-тесте Windows вызов на client handle вернул PID процесса сервера, но это наблюдаемое поведение не соответствует документированному предусловию и не используется как гарантия.

Таким образом, текущий пакет не доказывает связь client pipe с SCM PID и не подходит для доверенного подключения. В нём оставлены только чистые policy-predicates для возможного будущего контракта.

Hash файла по текущему пути сам по себе не доказывает содержимое уже загруженного image section. Путь под `Program Files` сам по себе не подтверждает фактические ACL. Эти проверки нельзя представлять как доказательство загруженного образа или защищённости каталога. `QueryServiceConfigW` также не подтверждал бы фактический запущенный image.

Пакет изолирован и не подключён к `MachinePipeIdentity` или другому клиенту. Для снятия fail-closed ограничения потребуется поддерживаемое доказательство server-origin на client endpoint, включая проверку фактического загруженного image и ACL, с native-тестом на нужных версиях Windows.
