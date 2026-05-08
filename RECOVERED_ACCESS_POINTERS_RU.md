# Восстановленный контекст по доступам, секретам и местам хранения

Этот документ фиксирует, какие доступы есть в проекте, для чего они нужны и где искать реальные значения.

Важно:

- реальные пароли, токены, ключи и секреты не записываются сюда в открытом виде;
- этот файл нужен именно как карта private-memory;
- фактические значения брать из локальных закрытых файлов и runtime-конфигов.

## 1. Главные private-источники

Реальные значения обычно находятся в одном из мест:

- `CREDENTIALS.md`
- `.secrets/*`
- `C:\Connector\server\config.json`
- GitHub Actions repository secrets
- локальная авторизация `gh auth`

## 2. Какие доступы существуют

### 2.1 VPS / Windows Server

Что это:

- SSH/админ-доступ к прод-серверу;
- иногда также RDP/Windows admin access.

Где используется:

- деплой;
- рестарты;
- чтение логов;
- диагностика сетевых и runtime-проблем;
- расширение диска/обслуживание Windows.

Где искать:

- `CREDENTIALS.md`
- `.secrets/opwork_vps_key`

### 2.2 Admin-доступ к Connector

Что это:

- `admin_username`
- `admin_password`
- `admin_api_key`

Где используется:

- вход в `/admin/login`;
- админские API;
- refresh update manifest cache;
- операции с токенами и аудитом.

Где искать:

- `C:\Connector\server\config.json`
- локальные приватные записи в `CREDENTIALS.md`

### 2.3 GitHub / Releases

Что это:

- доступ на push в `Scccoco/ServerConnector`;
- доступ к GitHub Releases;
- доступ к GitHub Actions secrets.

Где используется:

- релизы desktop MSI;
- tag-based workflow;
- post-release refresh server update manifest.

Где искать:

- `gh auth status`
- GitHub repo settings/secrets
- локальные developer credentials

### 2.4 SMB / share provisioning

Что это:

- SMB share `\\62.113.36.107\BIM_Models`;
- SMB-пользователи, которые server provisioning создает под device/token.

Где используется:

- доступ клиентов к BIM_Models;
- работа с папками проектов;
- Tekla runtime source и другие материалы.

Где искать настройки:

- `C:\Connector\server\config.json`
- `connector/server/smb_user_manager.ps1`

### 2.5 Tekla publish repo settings

Что это:

- repo worktree;
- subdir внутри repo;
- repo URL;
- repo ref;
- git executable;
- git author name/email;
- file size limits/timeouts.

Где используется:

- server-side publish pipeline для папки фирмы.

Где искать:

- `C:\Connector\server\config.json`

## 3. Что особенно важно для handoff

Новый агент должен понимать не только сами секреты, но и их назначение:

- какой секрет нужен для SSH;
- какой для admin API;
- какой для GitHub release;
- какой runtime-config управляет Tekla publish.

Если передавать контекст вручную, нужно передавать именно эту карту:

- название секрета;
- назначение;
- файл/место хранения;
- кто владелец;
- где используется в pipeline.

## 4. Что нельзя делать

- нельзя коммитить реальные пароли и ключи в `.md` репозитория;
- нельзя дублировать содержимое runtime `config.json` в git-доки, если там есть секреты;
- нельзя складывать приватные SSH-ключи и admin API keys в рабочие handoff файлы.

## 5. Что передавать безопасно

Можно и нужно передавать:

- redacted pointers;
- описание назначения секрета;
- какие скрипты и API используют конкретный доступ;
- что проверять, если доступ перестал работать.

## 6. Практический минимум для нового агента

Чтобы новый агент реально начал работать, ему нужно знать:

1. где лежит SSH key;
2. где лежит server `config.json`;
3. где взять admin login/password/api key;
4. есть ли рабочий `gh auth` на текущей машине;
5. кто владелец GitHub secrets для release workflow;
6. какие server-side параметры отвечают за Tekla publish.

## 7. Дополнительное текущее замечание

По текущему состоянию git-репозитория во время восстановления контекста уже всплывала проблема с git metadata/index corruption. Это отдельный риск и его стоит отдельно перепроверить перед следующими release/deploy-операциями.
