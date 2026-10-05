# Регистрация устройств общего коннектора

Код регистрации и Windows-клиента находится в этом репозитории. Аккаунты,
компании и роли остаются в существующей Платформе: отдельной базы пользователей
коннектор не создаёт. Текущий статус интеграции ведётся только в
[общем чек-листе](../connector-desktop/docs/UNIFIED_CONNECTOR_CHECKLIST_RU.md).

## Состав

- `Connector.Access.Contracts` — версия протокола, идентичность и профиль прав.
- `Connector.Access` — одноразовые токены, проверка CSR, реестр сертификатов,
  отзыв и постоянная очередь применения прав.
- `Connector.Access.AspNetCore` — HTTP endpoints и фоновый исполнитель в
  существующем серверном процессе Платформы.
- `Connector.Access.Client` — Windows-клиент: CSR, DPAPI CurrentUser,
  сохранение незавершённой регистрации и TLS-восстановление.
- `migrations/postgres` и `migrations/sqlite` — последовательные миграции.
  Библиотека не выполняет DDL при запуске.

## Подключение к существующему серверу

Сервер предоставляет четыре реализации: `IPlatformAccessDirectory` читает
актуального пользователя, компанию, членство и полномочия из существующего
хранилища; `IPlatformConnectorAdminIdentity` использует текущую web-сессию;
`IDeviceAccessDbConnectionFactory` открывает соединение с серверной базой;
`IX509DeviceCertificateIssuer` подписывает CSR настроенным issuer.
Корневой ключ и bearer-токены внешних сервисов не передаются приложению.

```csharp
services.AddConnectorAccessCore();
services.AddConnectorAccessHttp();
services.AddConnectorAccessProviderDispatch();
// После middleware текущей Платформы:
app.UseRateLimiter();
app.MapConnectorAccess();
```

TLS listener использует `ClientCertificateMode.AllowCertificate` и
`ConnectorAccessTlsCertificatePolicy.Validate`. Политика допускает сертификат
настроенного issuer либо ограниченное по времени self-issued доказательство
ключа. Это разрешает только TLS handshake. Endpoint повторно проверяет реестр,
отзыв и текущие права; bootstrap-сертификат не является идентичностью устройства.
Проверка не заменяется чтением `X-Client-Cert`. При TLS termination нужно отдельно
реализовать и принять доверенную передачу идентичности; обычный forwarded header
в этом модуле не поддерживается.

## Вход и восстановление

Администратор выдаёт короткоживущий одноразовый токен для существующего
пользователя и компании. Windows-клиент сохраняет `requestId`, CSR и защищённый
DPAPI ключ **до** первого POST; сам токен не сохраняется. Сервер атомарно
поглощает токен и привязывает исходный CSR к одному устройству.

- `POST /api/platform/connector/access/v1/enroll` — первый вход.
- `POST /api/platform/connector/access/v1/enrollments/{requestId}/resume` —
  потерянный ответ или повтор выдачи сертификата; TLS доказывает владение
  исходным ключом. Новый токен и новое устройство не требуются.
- `GET /api/platform/connector/access/v1/enrollments/{requestId}` — получение
  сохранённого ответа с уже выданным сертификатом.
- `GET /api/platform/connector/access/v1/profile` — подтверждённые права.
- `POST /api/platform/admin/connector/access/v1/enrollment-tokens` и
  `POST /api/platform/admin/connector/access/v1/devices/{deviceId}/revoke` —
  операции текущего администратора Платформы.

Клиент проверяет HTTPS штатной PKI-политикой, а выданный сертификат — по
заранее заданному SHA-256 issuer, подписи, EKU, сроку и исходному ключу.
Исправленный токен можно повторно отправить с тем же CSR/requestId только
после явного ответа сервера, что восстановление недоступно. Сетевой обрыв
не считается таким ответом. Повреждённое или чужое состояние не перезаписывается.

## Применение и отзыв прав

Профиль закрыт, пока все обязательные providers `api`, `vpn`, `smb` не подтвердят
актуальную revision. Provider возвращает receipt с command ID, revision и
действием. Обязателен внешний монотонный fence: старое Apply не может вернуть
доступ после более нового Revoke. Библиотека проверяет свежесть команды перед
RPC и сериализует пару device/provider; атомарность RPC с локальной БД не обещает.
Недоступный provider повторяется с backoff и не блокирует остальные команды.
Отзыв немедленно закрывает реестр/API и сохраняет внешние revoke-команды.

Реальные adapters Платформы, VPN и SMB ещё должны быть подключены и проверены
в их рабочем контуре. Локальные SQLite/HTTPS-тесты проверяют протокол и отказы;
они не означают выполненный переход production на новую схему.
