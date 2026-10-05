# Native EdgeChamfer spike

Проверяет на живой Tekla 2025, что `EdgeChamfer`:

- адресует точное физическое ребро `ContourPlate` двумя его конечными точками;
- различает оба ребра одной семантической кромки по физическим сторонам толщины;
- поддерживает только `CHAMFER_LINE`; `CHAMFER_ROUNDING` отвергается Tekla API;
- сохраняет собственный GUID при `Modify()`;
- удаляется как отдельный нативный объект без изменения identity детали-владельца.
- проходит тем же сценарием через production `TeklaApplyEdgeTreatmentExecutor`:
  strict topology address, ownership UDA, create/upsert с неизменным GUID,
  точный readback и rollback предыдущего состояния.

Запуск:

```powershell
dotnet run --project cad/tekla/bridge-desktop/spike/NativeEdgeChamfer/NativeEdgeChamfer.csproj -p:UseLocalTeklaReferences=true
```

Отчёт записывается в `%TEMP%\NativeEdgeChamfer\report.txt`.
