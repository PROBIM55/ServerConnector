# Общий защищенный транспорт Connector

Библиотека управляет уже установленным штатным клиентом NetBird через
[`netbird up`, `status --check startup` и `status --json`](https://docs.netbird.io/get-started/cli).
Она не устанавливает VPN, не меняет другие VPN и не реализует WireGuard.

`ProtectedServiceNetworkGate` возвращает `ProtectedServiceRoute` с единственным
допустимым `HttpClient`. Его `SocketsHttpHandler`:

- не следует redirects;
- разрешает только канонический HTTPS origin выбранного сервиса;
- резолвит имя один раз и принимает только server-configured private/CGNAT/ULA
  адреса или CIDR;
- перед `connect` привязывает сокет к назначенному NetBird IPv4/IPv6 той же семьи;
- при DNS, bind, route или probe ошибке закрывает запрос без обычного сетевого fallback.

TLS SNI и проверка сертификата используют исходное каноническое имя URI, а не
разрешенный IP. Вызывающий код отправляет защищенные запросы только через
`ProtectedServiceRoute.Client` и освобождает route после операции.

`CommonConnectorConnectionCoordinator` принимает один объект, одновременно
реализующий `IConnectorEnrollmentClient` и `IConnectorDeviceAccessClient`. Поэтому
enrollment, VPN bootstrap/state и профиль используют одно выданное устройству mTLS
удостоверение. `ReconnectAsync` доверяет существующему NetBird `Ready` только после
совпадения server-issued device/revision, management URI и полного набора overlay
адресов. `OpenProtectedServiceAsync` повторно проверяет state и applied profile перед
каждым созданием ограниченного route. UI/host сам регистрирует конкретный клиент,
NetBird CLI и список сервисов; legacy token или прямой HTTP fallback библиотека не ищет.
`DisconnectAsync` очищает только локальную авторизацию координатора и не останавливает
общий NetBird daemon, которым могут пользоваться другие приложения.
