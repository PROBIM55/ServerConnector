# Native contour corner spike

Проверяет на живой Tekla 2025 нативное изменение обработки конкретной вершины
`ContourPlate` через `ContourPoint.Chamfer`:

- линейная фаска (`CHAMFER_LINE`);
- скругление (`CHAMFER_ROUNDING`);
- снятие обработки (`CHAMFER_NONE`);
- сохранение GUID пластины при последовательных `Modify()`;
- точный readback типа и размеров обработки;
- production `TeklaPlan` executor по адресу `contourId + vertexId` без
  coordinate-nearest fallback;
- транзакционный create/upsert с тем же GUID и восстановление предыдущей
  обработки вершины.

Запускать при открытой модели Tekla:

```powershell
dotnet run --project cad/tekla/bridge-desktop/spike/NativeContourCorner/NativeContourCorner.csproj -c Release
```

Успешный результат заканчивается строкой:

```text
RESULT capability=contourChamfer supported=true line=true round=true guidPreserved=true rollback=true executorUpsert=true executorRollback=true
```
