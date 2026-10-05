# Machine pipe identity

Пакет создаёт Windows named pipe с именем из постоянного префикса, GUID операции и хеша проверенного account SID. `CreateNamedPipeW` выставляет `PIPE_REJECT_REMOTE_CLIENTS`, `FILE_FLAG_FIRST_PIPE_INSTANCE` и `nMaxInstances=1`. Защищённая DACL разрешает только SYSTEM, Builtin Administrators и SID инициатора; широкие principals не добавляются.

`AssertConnectedClient` предназначен для сценария, где клиентом pipe является
именно исходный пользователь: после принятия подключения и чтения первого байта
он сверяет его SID, уровень impersonation и anonymous/network/guest признаки;
`RevertToSelf` выполняется в `finally`. Для одноразового повышенного helper
при UAC с другой админской учётной записью этот SID намеренно отличается.
Его нельзя сравнивать с SID инициатора через `AssertConnectedClient`: сервер
сверяет PID с удерживаемым handle запущенного helper, повышенный токен и доверие
к образу отдельным контрактом до разбора машинной команды.

`ConnectTrustedClient` намеренно всегда завершает вызов отказом. Локальный pipe name, ACL и членство пользователя в Administrators не доказывают издателя server process. До использования клиента нужен отдельный проверенный publisher/service identity или authenticated bootstrap binding. Loopback-тест не доказывает поведение remote/network impersonation; оно остаётся открытым для проверки на Windows с подходящими учётными записями.
