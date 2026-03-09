# WebBridge.Utility

`WebBridge.Utility` — локальная headless Windows-утилита, которая публикуется в один `exe`, поднимает loopback HTTP/WebSocket API и выполняет команды для Excel, KOMPAS-3D и системных API по конфигурации.

Автор: `Гороховицкий Егор Русланович`

## Что Это

Утилита предназначена для сценария:

1. На машине пользователя запускается локальная утилита.
2. Веб-страница подключается к утилите по `127.0.0.1`.
3. Страница регистрирует UI-сессию и получает WebSocket.
4. Страница вызывает management API и command API.
5. При необходимости страница загружает в утилиту новый runtime-config через `POST /config/load`.

Утилита не ищет окна браузера, процессы или вкладки. Истинный критерий “UI существует”:

- `presence`-сессия
- или полноценная `interactive`-сессия через `WebSocket + hello + heartbeat`

## Что Есть В Репозитории

```text
src/WebBridge.Utility
configs/config.development.sample.json
configs/config.production.sample.json
tools/e2e
artifacts/publish/utility/win-x64/WebBridge.Utility.exe
```

Главный проект один:

- [WebBridge.Utility.csproj](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/WebBridge.Utility.csproj)

## Сборка И Публикация

Сборка:

```powershell
dotnet build WebBridge.Utility.sln
```

Публикация в один `exe`:

```powershell
dotnet publish src\WebBridge.Utility\WebBridge.Utility.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\artifacts\publish\utility\win-x64
```

Готовый файл:

- [WebBridge.Utility.exe](/c:/__MY_PROJECTS__/git/web-bridge-utility/artifacts/publish/utility/win-x64/WebBridge.Utility.exe)

## Иконка И Метаданные Релиза

Утилита собрана с чёрно-белой flat-иконкой:

- [utility-icon.ico](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Assets/utility-icon.ico)
- [utility-icon.svg](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Assets/utility-icon.svg)

В `exe` записаны release-метаданные:

| Поле | Значение |
|---|---|
| Product | `WebBridge.Utility` |
| Author | `Гороховицкий Егор Русланович` |
| Description | локальная headless-утилита для веб-страницы, Excel, KOMPAS-3D и системных API |

При запуске из командной строки утилита выводит banner с названием, версией, автором и активным `config`.

## Запуск

Обычный запуск требует один обязательный аргумент:

```powershell
.\WebBridge.Utility.exe --config .\configs\config.production.sample.json
```

### Аргументы

| Аргумент | Назначение | Обязателен |
|---|---|---|
| `--config`, `-c` | путь к JSON-конфигу | да, для обычного запуска |
| `--shutdown` | отправить уже работающему экземпляру команду завершения | нет |
| `--print-effective-config` | напечатать текущий effective config с редактированным token | нет |
| `--healthcheck-only` | только проверить `/health` и завершиться | нет |
| `--version` | показать версию и banner | нет |

### Что Печатается В Консоль

Если утилита запущена из терминала, она печатает:

- название продукта
- версию
- автора
- описание
- активный режим
- путь к `config`

Если агент запускается двойным кликом, консольное окно не создаётся.

## Конфиги

В репозитории два стартовых конфига.

### Development

- [config.development.sample.json](/c:/__MY_PROJECTS__/git/web-bridge-utility/configs/config.development.sample.json)

Это полный пример:

- `Versions`
- встроенный `Catalog`
- полный блок `Adapters`
- embedded `Profiles`
- embedded `Manifest`

Нужен для разработки, отладки и демонстрации полного invoke DSL.

### Production Bootstrap

- [config.production.sample.json](/c:/__MY_PROJECTS__/git/web-bridge-utility/configs/config.production.sample.json)

Это намеренно минимальный bootstrap-config.

Он содержит только:

- метаданные продукта
- `Runtime`
- `Server`
- `Ui`
- `Lifecycle`
- security-политику
- session-настройки
- логирование
- storage для локальных артефактов
- пустой `Catalog`
- пустой `Adapters`

Практический смысл:

1. Агент поднимается.
2. Связывается с UI.
3. UI при необходимости загружает полноценный runtime-config через `POST /config/load`.

## Структура Config

Канонический формат конфига теперь единственный: вложенный. Плоские ключи вроде `ListenUrl`, `UiUrl`, `OpenUi`, `Shutdown`, `LogLevel`, `Profiles`, `ComAdapters` и `SystemAdapter` больше не поддерживаются. Актуальная схема начинается с `ConfigSchemaVersion = 2` внутри блока `Versions`.

| Блок | Назначение |
|---|---|
| `Versions` | версия утилиты, версия текущего конфига и версия схемы |
| `Metadata` | имя продукта, автор, описание, URL репозитория |
| `Runtime` | среда (`EnvironmentName`), dev-режим, запрет автооткрытия браузера |
| `Server` | локальный loopback URL утилиты (`ListenUrl`) |
| `Ui` | адрес UI, режим автооткрытия и ожидание первой сессии |
| `Lifecycle` | idle shutdown-policy и таймаут простоя |
| `Logging` | уровень логов, debug-режим и путь к log-файлу |
| `Storage` | где хранить profiles, cache и diagnostics |
| `Catalog` | URL удалённого manifest, embedded manifest и embedded profiles |
| `Adapters` | descriptors для COM-адаптеров и policy для `system` |
| `Security` | `PairingToken`, allowlist origin, loopback-policy |
| `Session` | heartbeat, presence, sweep |

Обычно руками правят только `Runtime`, `Server`, `Ui`, `Lifecycle`, `Logging`, `Security` и `Session`.

Редко приходится менять `Versions`, `Catalog` и `Adapters`: это схема/каталог команд/описание адаптеров, а не базовые настройки запуска.

## API

### HTTP

| Метод | Путь | Назначение |
|---|---|---|
| `GET` | `/health` | health check |
| `GET` | `/info` | runtime info и метаданные продукта |
| `GET` | `/config/effective` | effective config |
| `GET` | `/config/version` | версия активного config |
| `POST` | `/config/load` | загрузить новый runtime-config |
| `POST` | `/config/reload` | перечитать config с диска |
| `POST` | `/session/register` | зарегистрировать UI-сессию |
| `POST` | `/session/closing` | закрыть UI-сессию |
| `GET` | `/sessions` | состояние presence и interactive сессий |
| `POST` | `/commands/execute` | выполнить одну команду |
| `POST` | `/commands/execute-batch` | выполнить пакет команд |
| `GET` | `/manifest/status` | статус manifest |
| `POST` | `/manifest/refresh` | обновить manifest |
| `POST` | `/utility/open-ui` | открыть UI |
| `POST` | `/utility/shutdown` | остановить агент |

### WebSocket

Путь:

- `/ws/session`

Сообщения от UI:

- `hello`
- `heartbeat`

Сообщения от агента:

- `hello`
- `heartbeat`
- `command-result`
- `command-batch-result`
- `shutdown-warning`
- `error`
- `log/event`

## Как Подключить Свой UI

Минимальный сценарий для своей веб-страницы:

1. Узнать `ListenUrl` и `PairingToken`.
2. Проверить `GET /health`.
3. Выполнить `POST /session/register`.
4. Открыть `WebSocket` на `/ws/session?sessionId=...&token=...`.
5. Отправить `hello`.
6. Поддерживать `heartbeat`.
7. Вызывать `/commands/execute` или `/commands/execute-batch`.
8. При закрытии страницы вызвать `/session/closing`.

### Что Нужен Передавать В HTTP

Обязательные headers для browser-facing endpoint’ов:

| Header | Назначение |
|---|---|
| `Origin` | должен входить в allowlist |
| `X-KWB-Pairing-Token` | pairing token |
| `Content-Type: application/json` | для `POST` |

### Пример Register Session

```json
POST /session/register
{
  "clientName": "MyUi",
  "clientVersion": "1.0.0",
  "uiVersion": "1.0.0"
}
```

### Пример Execute Command

```json
POST /commands/execute
{
  "profileId": "runtime",
  "commandId": "excel.range.set-value",
  "arguments": {
    "handleId": "h-123",
    "value": "Hello"
  },
  "reportVerbosity": "compact",
  "sharedContextId": "ui-live",
  "timeoutMilliseconds": 15000
}
```

### Пример Execute Batch

```json
POST /commands/execute-batch
{
  "sharedContextId": "ui-live",
  "reportVerbosity": "compact",
  "stopOnError": true,
  "commands": [
    {
      "profileId": "runtime",
      "commandId": "excel.workbook.add-worksheet",
      "arguments": {
        "handleId": "wb-1"
      }
    },
    {
      "profileId": "runtime",
      "commandId": "excel.range.set-value",
      "arguments": {
        "handleId": "range-1",
        "value": "Hello"
      }
    }
  ]
}
```

### Как UI Может Подгружать Новый Config

Если bootstrap-config минимальный, UI может сам отправить полный config:

```json
POST /config/load
{
  "settings": {
    "versions": {
      "configVersion": "runtime-2026-03-09",
      "configSchemaVersion": 2
    },
    "runtime": {
      "environmentName": "Production"
    },
    "ui": {
      "url": "https://example.test/app/",
      "openMode": "Never"
    },
    "catalog": {
      "profiles": [ ... ]
    },
    "adapters": {
      "com": [ ... ],
      "system": { ... }
    }
  },
  "persist": false
}
```

Это основной production-сценарий для thin bootstrap-config.

## Presence И Interactive

| Состояние | Что означает |
|---|---|
| `presence` | UI уже загрузился и зарегистрировался, но ещё не перешёл в полноценную interactive-сессию |
| `interactive` | UI уже держит `WebSocket`, отправил `hello` и поддерживает heartbeat |

Если включён `Session.SuppressAutoOpenOnPresenceSessions`, утилита в `OpenUi=Auto` не будет открывать новый UI, если уже есть `presence`-сессия.

## Что Умеет Утилита

| Область | Что уже умеет |
|---|---|
| UI lifecycle | register, presence, interactive heartbeat, reconnect, idle shutdown |
| Runtime management | `config/version`, `config/load`, `config/reload`, `utility/open-ui`, `utility/shutdown` |
| Command engine | `Root`, `Chain`, `get`, `set`, `call`, `index`, `new`, `ReturnPath`, `StoreAs`, `ByRef`, `CaptureAs` |
| Batch | `POST /commands/execute-batch` с shared context |
| Excel | общий `ComInvokeRuntime`, `application`, `handle`, shared context, compact/full reports |
| KOMPAS | общий `ComInvokeRuntime`, `application`, `handle`, shared context, compact/full reports |
| System | generic `type:System.*` invoke и controlled wrappers `process`, `command`, `http`, `registry`, `zip`, `hash`, `drive` |
| Security | loopback-only, origin allowlist, pairing token |
| Diagnostics | structured logs, detailed command report, compact/full mode |

## Чего Утилита Не Обещает

| Ограничение | Комментарий |
|---|---|
| Не гарантирует любой COM edge-case | возможны `ref/out`, vendor-specific и marshalling-сценарии, которые потребуют отдельной настройки |
| Не ищет вкладки и окна браузера | UI определяется только по session-state |
| Не является удалённым unrestricted shell для всего .NET | `system` ограничен policy и wrapper-слоем |
| Не решает поведение браузера при открытии `UiUrl` | утилита только просит ОС открыть URL |
| Не хранит секреты в UI | pairing token и runtime-config должны контролироваться локально |

## Рекомендуемый Production-Сценарий

| Шаг | Что делать |
|---|---|
| 1 | Запустить утилиту с [config.production.sample.json](/c:/__MY_PROJECTS__/git/web-bridge-utility/configs/config.production.sample.json) |
| 2 | Открыть страницу UI по `UiUrl` |
| 3 | UI подключается к агенту и регистрирует сессию |
| 4 | UI запрашивает `/info` и `/config/version` |
| 5 | UI, если нужно, отправляет полный runtime-config через `/config/load` |
| 6 | После этого UI использует `/commands/execute` и `/commands/execute-batch` |

## Тестирование

Быстрая локальная проверка:

```powershell
dotnet test WebBridge.Utility.sln
```

Для black-box E2E используется внешний browser-driven harness:

```powershell
python tools\e2e\run_e2e.py --soak-seconds 20 --chaos-seconds 30 --latency-iterations 20
python tools\e2e\run_e2e.py
```

Harness проверяет:

- устойчивость соединения
- management API
- Excel
- KOMPAS
- System
- chaos
- batch-vs-sequential latency

## Ключевые Файлы

- [Program.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Program.cs)
- [UtilityCli.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/UtilityCli.cs)
- [UtilityApp.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/UtilityApp.cs)
- [ProtocolModels.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Protocol/ProtocolModels.cs)
- [ReflectiveInvoke.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Core/ReflectiveInvoke.cs)
- [ComInvokeRuntime.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Adapters/Com/ComInvokeRuntime.cs)
- [SystemRuntime.cs](/c:/__MY_PROJECTS__/git/web-bridge-utility/src/WebBridge.Utility/Adapters/System/SystemRuntime.cs)
- [config.development.sample.json](/c:/__MY_PROJECTS__/git/web-bridge-utility/configs/config.development.sample.json)
- [config.production.sample.json](/c:/__MY_PROJECTS__/git/web-bridge-utility/configs/config.production.sample.json)

