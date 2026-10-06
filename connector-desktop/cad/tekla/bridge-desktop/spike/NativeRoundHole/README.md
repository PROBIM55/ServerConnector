# Native round-hole spike

Проверяет на живой модели Tekla 2025 нативное представление `HoleFeature` через
`BoltArray` в hole-only режиме:

- стабильную целевую `Part`;
- центр и произвольную пространственную ось отверстия;
- круглый диаметр без допуска;
- сквозное и глухое отверстие;
- persistent GUID и readback всех перечисленных параметров;
- очистку временных объектов независимо от результата.

Запускать при открытой Tekla 2025:

```powershell
dotnet run --project cad/tekla/bridge-desktop/spike/NativeRoundHole/NativeRoundHole.csproj
```

Capability `roundHole` можно включать в production registry только при
`RESULT capability=roundHole supported=true`. Пазы и болтовые группы этим spike
не проверяются.
