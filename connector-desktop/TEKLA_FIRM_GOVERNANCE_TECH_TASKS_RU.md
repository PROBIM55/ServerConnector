# TEKLA_FIRM_GOVERNANCE_TECH_TASKS_RU

Технический backlog для реализации требований из:

- `connector-desktop/TEKLA_FIRM_GOVERNANCE_REQUIREMENTS_RU.md`

## 1) Сервер: модель данных и роли

### 1.1 DB: роль `admin_firm`
- Добавить хранение роли `admin_firm` (таблица ролей/флаг в существующей модели админов).
- Добавить миграцию для существующей БД без потери данных.
- Добавить индекс/уникальность по сущности пользователя/админа.

### 1.2 API: управление ролью
- `GET /admin/firm-admins` - список пользователей с ролью `admin_firm`.
- `POST /admin/firm-admins/grant` - назначить роль.
- `POST /admin/firm-admins/revoke` - снять роль.
- Все endpoint'ы защищены `admin_system`.

### 1.3 Авторизация роли
- Добавить серверную проверку `require_firm_admin_access`.
- Все операции публикации стандарта Tekla доступны только с `admin_firm`.

## 2) Сервер: операции стандарта XS_FIRM

### 2.1 Endpoint публикации стандарта
- `POST /admin/tekla/firm/publish`:
  - принимает описание публикации (comment, version, revision/repo_ref);
  - валидирует наличие роли `admin_firm`;
  - запускает pipeline commit/push (через серверный worker/service);
  - возвращает `publish_id` и статус.

### 2.2 Endpoint статуса публикации
- `GET /admin/tekla/firm/publish/{publish_id}` - статус операции.
- `GET /admin/tekla/firm/publish/history` - журнал публикаций.

### 2.3 Endpoint роллаута
- `GET /admin/tekla/rollout/status`:
  - total clients
  - updated
  - pending
  - errors
  - by revision

## 3) Сервер: ACL на SMB папку эталона

### 3.1 Политика ACL
- Эталон: `\\62.113.36.107\BIM_Models\Tekla\XS_FIRM`.
- Реализовать процедуру применения ACL:
  - `admin_firm` -> RW
  - остальные -> RO

### 3.2 Автоматизация
- Добавить серверный job/endpoint для применения ACL при изменении роли.
- Добавить в аудит запись о применении ACL.

## 4) Desktop Connector: режим admin_firm

### 4.1 Получение роли
- На bootstrap/heartbeat получать флаг `is_firm_admin`.
- Хранить локально в settings/state.

### 4.2 UI
- Добавить вкладку/секцию `Управление XS_FIRM`.
- Показывать секцию только если `is_firm_admin=true`.

### 4.3 Функции секции
- `Проверить изменения` (git status эквивалент).
- `Подготовить публикацию` (version/comment/repo_ref).
- `Опубликовать` (вызов серверного publish endpoint).
- `История публикаций` (таблица последних publish операций).

## 5) Admin UI (web)

### 5.1 Управление role-based доступом
- Экран/блок назначения `admin_firm`.
- Список действующих `admin_firm`.
- Кнопки `Назначить` / `Снять`.

### 5.2 Контроль публикаций
- Блок текущей опубликованной версии.
- Блок истории публикаций.
- Блок rollout-статуса клиентов.

## 6) Аудит и трассировка

Добавить события аудита:
- `firm_admin_granted`
- `firm_admin_revoked`
- `tekla_firm_publish_started`
- `tekla_firm_publish_succeeded`
- `tekla_firm_publish_failed`
- `tekla_firm_acl_applied`

Для каждого события хранить:
- actor
- target user/device
- version/revision
- details/error
- timestamp

## 7) Безопасность

- Запретить publish endpoint без роли `admin_firm`.
- Валидация входных параметров (version/revision/repo_ref/comment).
- Защита от параллельных публикаций (lock/mutex).
- Обязательный timeout и error handling для git-операций.

## 8) Тесты

### 8.1 Unit
- Проверка role guard (`admin_system` vs `admin_firm`).
- Проверка валидации publish payload.

### 8.2 Integration
- Grant/revoke роли и проверка ACL pipeline.
- Publish success/failure path.
- Rollout status endpoint корректно считает метрики.

### 8.3 E2E
- Пользователь без `admin_firm` не видит publish UI.
- Пользователь с `admin_firm` публикует версию через UI.
- Клиенты получают новую revision.

## 9) Definition of Done (MVP)

- Роль `admin_firm` назначается и снимается через admin UI.
- ACL на `XS_FIRM` применяются согласно роли.
- Desktop показывает секцию publish только `admin_firm`.
- Publish через Connector UI работает и попадает в аудит.
- Rollout статус виден в admin UI.

## 10) Этапность внедрения

### Sprint A (Core)
- DB роль + API grant/revoke + server guards.

### Sprint B (Publish)
- Publish endpoints + audit + lock.

### Sprint C (Desktop/UI)
- Desktop admin_firm section + web admin role screens.

### Sprint D (Ops)
- ACL automation + rollout dashboards + E2E.
