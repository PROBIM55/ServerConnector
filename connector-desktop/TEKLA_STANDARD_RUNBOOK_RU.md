# Tekla Standard - runbook диагностики

## 1. Быстрый triage

Проверить:

1. endpoint manifest: `GET /updates/tekla/firm/latest.json`
2. состояние API: `GET /health`
3. в admin UI блок `Стандарт Tekla (клиенты)`
4. на клиенте вкладку `Стандарт Tekla` и локальный лог

## 2. Типовые симптомы и действия

### Симптом: `manifest не получен`

- Проверить доступность `tekla_firm_manifest_url`.
- Проверить JSON в `server/updates/tekla_firm_latest.json`.
- Проверить, что required поля заполнены.

### Симптом: `Git недоступен`

- Проверить bundled git в каталоге приложения (`tools/git`).
- Проверить fallback на системный `git`.
- Проверить права запуска процесса git у пользователя.

### Симптом: `Ожидает установки после закрытия Tekla`

- Убедиться, что Tekla действительно закрыта.
- Выполнить `Проверить обновление` или дождаться следующего цикла.

### Симптом: клиент не обновляется

- Проверить `installed_revision` и `target_revision` в admin UI.
- Проверить `last_error` у устройства.
- Проверить локальный лог на клиенте.

## 3. Проверка client state на сервере

Endpoint:

- `GET /admin/tekla/clients`

Смотреть поля:

- `installed_revision`
- `target_revision`
- `pending_after_close`
- `tekla_running`
- `last_error`

## 4. Публикация исправляющей версии

1. Подготовить корректную ревизию в репозитории стандарта.
2. Опубликовать через admin UI или `POST /admin/tekla/manifest`.
3. Проверить аудит (`tekla_manifest_updated`).
4. Дождаться обновления клиентов.

## 5. Откат

1. В manifest выставить предыдущую стабильную ревизию (`repo_ref`).
2. Опубликовать manifest.
3. Контролировать снижение ошибок и выравнивание ревизий.
