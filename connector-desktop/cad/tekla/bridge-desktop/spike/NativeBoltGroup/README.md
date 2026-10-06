# Native bolt-group spike

Live Tekla 2025 proof for the strict `create-bolt-group` contract.

The spike verifies native target/participant references, local frame, bolt
standard and diameter, shop/site mode, optional holes, persistent identity,
and these pattern mappings:

- `single`, `linear`, `grid` through `BoltArray`;
- arbitrary `points` through `BoltXYList`.

The spike also executes the production `TeklaCreateBoltGroupExecutor` and
verifies create/readback, in-place update with the same native GUID, update
rollback, create rollback, and deterministic rejection of unsupported changes.

The live Tekla 2025 readback establishes the following adapter rules:

- `BoltArray` stores `count - 1` gaps. A bootstrap zero supplied for a
  one-position axis is removed by Tekla on insert; world positions are the
  cumulative sum of the stored gaps from the frame origin;
- grid rows are centered around the `FirstPosition`/`SecondPosition` axis, so
  two rows with spacing `85` have local Y coordinates `-42.5` and `42.5`;
- paired `BoltXYList.AddBoltDistX/Y` values preserve arbitrary local points;
- Tekla 2025 exposes no remove/set operation for `BoltXYList`, so changing an
  existing arbitrary point list is rejected to preserve the native GUID;
- `Hole1` and `Hole2` control physical holes in the target and participant;
- assigning `BoltGroup.Length` for a catalog bolt is normalized back to `0` by
  Tekla 2025, so an explicit requested bolt length cannot be claimed as a
  supported round-trip capability.

Run with Tekla Structures 2025 and a model open:

```powershell
dotnet run --project cad/tekla/bridge-desktop/spike/NativeBoltGroup/NativeBoltGroup.csproj -c Release
```

The detailed report is written to `%TEMP%\NativeBoltGroup\report.txt` and all
temporary model objects are removed before exit.
