# NetBird provider общего Connector

Проверено по официальной документации NetBird 2026-10-02:

- [Public API и service-user token](https://docs.netbird.io/api),
  авторизация PAT — `Authorization: Token ...`;
- [setup keys](https://docs.netbird.io/api/resources/setup-keys): `one-off`,
  `usage_limit=1`, минимальный `expires_in=86400`;
- [peers](https://docs.netbird.io/api/resources/peers): готовность подтверждается
  `connected`, `last_seen` и назначенным overlay IP, отзыв peer — `DELETE`;
- [groups и policies](https://docs.netbird.io/manage/access-control/manage-network-access):
  встроенная группа `All` включает каждый peer, а стартовая Default policy открывает
  full mesh. Provider не выдает setup key, пока активное правило дает `All` исходящий
  или двунаправленный доступ;
- Windows-клиент использует штатный `netbird up` и
  [`netbird status --check startup` / `--json`](https://docs.netbird.io/get-started/cli).

Серверные компоненты self-hosted NetBird имеют AGPLv3, клиент — BSD-3-Clause;
интеграция вызывает опубликованный API/CLI и не включает или изменяет код NetBird.

## Инварианты

- На устройство создаются отдельные bootstrap group и policy; setup key хранится
  только в защищенном `IDataProtector`-виде и не заменяется при обычном retry.
- До `connected + fresh last_seen + assigned IP` policy отсутствует. Applied receipt
  возвращается только после повторного чтения peer и точной policy.
- Durable revision/action fence записывается до внешнего изменения. Более старый
  Apply после Revoke повторно согласует удаление policy, peer и bootstrap group.
- `/vpn/state` возвращает `ready` только после свежего чтения того же peer, его
  единственной device group, включенной device policy и повторной проверки запрета
  permissive `All`; management URI и назначенные IP берутся из этого чтения.
- Реальный management URL, service-user token, Data Protection key ring и точные
  product/module → destination-group bindings задает host; fixtures этого не заменяют.
