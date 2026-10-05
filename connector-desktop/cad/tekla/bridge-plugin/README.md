# Bridge Plugin — rewrite на наши стандарты

Чистый rewrite декомпилированного `TeklaBridge.exe` + `BridgeGirderPlugin.dll`
+ `BridgeMvpPlugin.dll` на C# с разделением pure-логики и Tekla SDK.

**Статус (2026-05-11):** Phase A → E6 + G готовы. **Phase F (binary parity)
обязателен перед production-деплоем** — нужна живая Tekla 2025.0.

## Архитектура

```
cad/tekla/bridge-plugin/
├── Bridge.Plugin.sln
├── REWRITE_PLAN.md                — план/чек-лист всех фаз
├── README.md                       — этот файл
├── src/
│   ├── Bridge.Plugin.Geometry/    — pure, no Tekla deps (testable)
│   │   ├── Dto/                    — SegLwt, DetailBoundary, …
│   │   ├── Parsing/                — Parsers, Normalize
│   │   ├── Rules/                  — FlangeTransitionRules, SegmentMath
│   │   ├── Cuts/                   — FlangeCutters, CutterPolygon
│   │   ├── Placement/              — BeamFrame, SystemFrame, BeamPlacement, DeckPlacement
│   │   ├── Geometry3d/Vec3.cs      — собственный pure 3D-vec
│   │   ├── StressZone.cs, FlangeRef.cs
│   │   └── Polyfill/IsExternalInit.cs
│   ├── TeklaBridge/                — выход TeklaBridge.exe + библиотечная часть
│   │   ├── BridgeCommands.cs       — Step4/Step5 оркестрация
│   │   ├── Program.cs              — Main entry, command.txt dispatch
│   │   ├── Infrastructure/CommandFileStore.cs
│   │   └── TeklaShim/              — Vec3 ↔ Point, ContourPlate factories
│   ├── BridgeGirderPlugin/         — выход BridgeGirderPlugin.dll, [Plugin] класс
│   └── BridgeMvpPlugin/            — sister-плагин
├── tests/
│   └── Bridge.Plugin.Geometry.Tests/   — xUnit, 148 тестов
├── _decomp/                        — read-only декомпиляция для сверки (не компилируется)
├── deploy/component/               — оригинальные .inp файлы
└── scripts/redeploy_plugin.ps1     — атомарный swap с timestamped backup
```

## Build & test

```powershell
# Локально (Windows + .NET 8 SDK)
cd cad\tekla\bridge-plugin
dotnet build Bridge.Plugin.sln -c Release
dotnet test Bridge.Plugin.sln -c Release
# → 148 passed (pure-логика полностью покрыта)
```

Tekla SDK refs идут через `<HintPath>$(TeklaBin)\…</HintPath>` с дефолтом
`C:\TeklaStructures\2025.0\bin`. Если другая версия — `msbuild /p:TeklaBin=…`.

## Deploy (через Phase G script)

> **Не деплоить до Phase F binary-parity!** Прежний плагин может работать
> корректно на крайних кейсах, что новый ещё не покрывает. Сначала:

1. **Phase F gate:** прогнать 5 канонических сценариев через прежний vs
   новый плагин, сравнить geometry snapshot через
   `Bridge.Desktop /components/read`. См. `REWRITE_PLAN.md §Phase F`.
2. Только после успешного diff:
   ```powershell
   .\scripts\redeploy_plugin.ps1                     # default Tekla 2025.0
   .\scripts\redeploy_plugin.ps1 -DryRun             # сначала dry-run
   .\scripts\redeploy_plugin.ps1 -TeklaVersion 2024.0
   ```
3. Скрипт делает timestamped бэкап `.bak-YYYYMMDD-HHmmss` каждого
   заменяемого файла. Откат — см. комментарий в начале скрипта.
4. Перезапустить Tekla чтобы плагин загрузился из новой DLL.

## Что ещё не сделано

Live-grade gaps, нужны для полной production-парности:

- **E5b — ApplyWebCutsForSegment:** web должна вырезаться вдоль
  бевельных стыков поясов, чтобы геометрически следовать их внутренней
  грани. Сейчас web — прямой прямоугольник, перекрывающий пояса по высоте.
- **E5c — Web ribs:** продольные и поперечные стиффенеры стенки
  (`ApplyWebLongRibs`, `ApplyWebTransRibs`). Сейчас параметры
  `webLongRibs` / `webTransRibs` парсятся, но ничего не создают.
- **E6b — PendingInsertPayload:** chunked USER_FIELD_2..6 cache для
  payload-overrides >240 байт. Нужен для interactive insert flow.
- **DefineInput** в плагине пуст — Tekla не может предложить
  interactive pick точек начала/конца. Programmatic insert через
  bridge-desktop работает без этого.
- **Step1-3** не портированы. Прежний плагин имел их для итеративного
  обновления, наш web UI не использует.

После Phase F можно решать, какие gaps критичны для текущего scope.

## Decompiled reference

Полная декомпиляция прежнего плагина — в `_decomp/`. Используется как
read-only источник истины при портировании. См. `_decomp/README.md`.

## Связанные документы

- `REWRITE_PLAN.md` — полный чек-лист фаз A–H с roadmap'ом.
- `tests/Bridge.Plugin.Geometry.Tests/fixtures/flange-transition-cases.json`
  — 13 эталонных сценариев, общих с будущим TS-портом для web viewport
  (Phase H).
