# Developed plate station-frame smoke

Изолированный live-smoke для проверки нативных локальных систем
`Tekla.Structures.Model.PolyBeam`. Проверка создаёт набор временных line-only и
rounded/arc-point `PolyBeam`, считывает `GetPolybeamCoordinateSystems()` и
удаляет все созданные объекты в `finally`.

```powershell
dotnet build -c Release
Set-Location C:\TeklaStructures\2025.0\bin
& G:\00_Projects\13_Платформа\cad\tekla\bridge-desktop\spike\DevelopedPlateStationFrame\bin\Release\net48\DevelopedPlateStationFrame.exe
```

Tekla 2025 должна быть запущена с открытой моделью. Отчёт пишется в
`%TEMP%\DevelopedPlateStationFrame\report.txt`. Capability
`developedPlatePolyBeamStationFrameV1` нельзя включать только по факту успешной
вставки: smoke должен подтвердить порядок станций и отсутствие скрытой смены
знака локального базиса.

## Фактический результат Tekla 2025

На открытой модели Tekla 2025 API возвращает локальные системы для части ранее
созданных `PolyBeam`, но возвращает `systemCount=0` для каждого заново
вставленного smoke-объекта, включая объект с численно повторённым трёхточечным
контуром и `CHAMFER_ARC_POINT`. Получение solid, повторный select по GUID,
`Modify`, `CommitChanges` и ожидание результата не меняют readback.

Поэтому smoke завершается кодом `30` и строкой
`capability=developedPlatePolyBeamStationFrameV1 supported=false`. Это не ошибка
очистки и не разрешение на fallback: production capability обязана оставаться
выключенной. Код `31` зарезервирован для случая, когда системы читаются, но их
знаковое соответствие станциям ещё не доказано. Успешного кода для capability
нет до реализации и проверки детерминированного station-to-frame readback.
