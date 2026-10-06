# Phase 0 Spike — programmatic BridgeGirderPlugin insert/modify

Минимальный console app, доказывающий что можно вставлять и менять
`BridgeGirderPlugin` через Tekla Open API без `runmacro` / `TeklaBridge.exe` /
file-protocol. Ground truth для фаз Bridge.Desktop. См.
[`docs/TEKLA_BRIDGE_DESKTOP_PLAN.md`](../../../../../docs/TEKLA_BRIDGE_DESKTOP_PLAN.md)
phase 0.

Результат spike'а:
- **`docs/tekla/BRIDGE_GIRDER_COMPONENT_CONTRACT.md`** — полный контракт компонента.
- **`BridgeGirderSchemaV1.json`** — машинно-читаемая схема для component
  registry (Phase 1).

## Build

```bash
cd cad/tekla/bridge-desktop/spike/Phase0Spike
dotnet build -c Release
```

Targets net48 / x64. Tekla SDK assemblies через HintPath на
`C:\TeklaStructures\2025.0\bin\` (override через `TeklaBin` MSBuild
property если нужно).

## Run

Spike-у нужен `Phase0Spike.exe.config` с binding redirects на ВСЕ
зависимости Tekla SDK. Одноразово копируем боевой config из deployed
TeklaBridge.exe (110 dependentAssembly entries):

```bash
cp /c/TeklaStructures/2025.0/Environments/common/Extensions/BridgeComponent/TeklaBridge.exe.config \
   bin/Release/net48/Phase0Spike.exe.config
```

Tekla 2025 должна быть **запущена с открытой моделью** (не splash screen).

```bash
pwsh -Command "Set-Location 'C:\TeklaStructures\2025.0\bin'; \
   & '$(pwd)/bin/Release/net48/Phase0Spike.exe'"
```

cwd важен (`C:\TeklaStructures\2025.0\bin`) — Tekla SDK ищет running
Tekla через shared-memory IPC, который инициализируется относительно
working directory.

Exit code 0 — успех. Trace: `%TEMP%\Phase0Spike\phase0-spike-trace.txt`.

## Что доказано

1. **Class**: `Tekla.Structures.Model.Component`.
2. **Number**: `BaseComponent.PLUGIN_OBJECT_NUMBER = -100000` (документированная
   константа для всех `PluginBase`-производных).
3. **Name**: `"BridgeGirderPlugin"` (matches `[Plugin("...")]` атрибут).
4. **Input**: `ComponentInput.AddTwoInputPositions(start, end)` для axis-based.
5. **27 UDA полей** `tb_*` биндятся через `Component.SetAttribute`.
6. **`Modify()` достаточно** для изменения параметров — Delete+Insert не нужен
   (для `tb_h` подтверждено).
7. **GUID handling**: только через `Model.GetGUIDByIdentifier(Identifier)` /
   `Model.GetIdentifierByGUID(string)`, тип GUID — string в Tekla 2025.
8. **108 child-объектов** на дефолтных параметрах (ControlPoints,
   ContourPlates, BooleanParts).

## Что **не** делает spike (это для следующих фаз)

- Не имеет HTTP listener.
- Не имеет component registry / adapter pattern.
- Не имеет idempotency / persistence.
- Не имеет error mapping.
- Не вызывается из Connector.

Spike — это minimal proof. Phase 2+ строит над ним полноценный runtime.
