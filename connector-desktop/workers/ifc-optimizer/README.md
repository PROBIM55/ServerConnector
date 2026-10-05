# Structura IFC Optimizer Engine

Local sidecar for analysis, exact/compact optimization, and independent
validation of plain IFC files.

Development setup:

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\python -m pip install -e ".[dev]"
.\.venv\Scripts\python -m pytest
```

CLI:

```powershell
.\.venv\Scripts\structura-ifc-optimizer analyze model.ifc
.\.venv\Scripts\structura-ifc-optimizer optimize model.ifc model.optimized.ifc
.\.venv\Scripts\structura-ifc-optimizer optimize model.ifc model.compact.ifc --profile compact
```

The source file is always read-only. The optimized file is published only after
semantic, representation-style, entity-count, and world-geometry validation
succeeds. Corrected output requires a full rendered-geometry match by STEP ID,
bounding box, surface area, and volume. Existing representation maps are
reused only as complete geometry-and-style chains; equal geometry with different
colours remains separate. Analytic extrusion conversion is limited to closed
manifold prisms with congruent translated caps, paired side vertices, no
unsupported per-face presentation, and a matching item-level OCCT signature.
Every rejected candidate falls back to its original `IfcFacetedBrep`.
The exact and compact profiles also normalize mirrored faceted-BREP mapping
instances. Each affected product receives an explicit right-handed triangular
BREP reconstructed from its rendered world geometry. The engine preserves world
coordinates and presentation assignments, and rejects the result unless
independent tessellation confirms matching bounds, area, volume, and outward
output orientation.
Mirrored conversion is fail-closed: a map must contain exactly one
`IfcFacetedBrep`, belong to an unambiguous product representation, and have a
closed orientable shell. Unsupported mirrored maps raise
`UNSUPPORTED_MIRRORED_MAP` and are never published as successful output.
Before optimization, provably empty no-op IFC relationships are removed from an
isolated copy because importers such as Speckle cannot traverse null
`RelatedElements`, `RelatedObjects`, or `RelatedProducts` aggregates. The
source file remains read-only, and a relationship with any direct reference is
rejected instead of being removed automatically.
