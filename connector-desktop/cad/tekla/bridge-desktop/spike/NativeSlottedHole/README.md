# Native slotted-hole spike

Live Tekla 2025 proof for the strict `create-hole` / `holeType=slotted`
contract.

The spike verifies that:

- a holes-only `BoltArray` is the native representation;
- contract `slotLengthMm` is the total end-to-end length;
- Tekla `SlottedHoleX` stores `slotLengthMm - diameterMm`;
- the slot follows local X of the explicit hole work plane;
- center, axis, slot direction, dimensions and persistent identity survive
  native insert/readback;
- the production executor preserves GUID on modify and restores exact native
  state on modify/create rollback;
- all temporary model objects are deleted before the process exits.

Run with Tekla Structures 2025 and a model open:

```powershell
dotnet run --project cad/tekla/bridge-desktop/spike/NativeSlottedHole/NativeSlottedHole.csproj -c Release
```

The detailed report is written to `%TEMP%\NativeSlottedHole\report.txt`.
