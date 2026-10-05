from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any

import ifcopenshell
import ifcopenshell.util.element

from .events import EventSink, emit, null_sink
from .io_utils import write_json_atomic

GEOMETRY_ATTRIBUTES = {"Representation"}
VOLATILE_PSET_KEYS = {"id"}


def _is_entity(value: Any) -> bool:
    return hasattr(value, "is_a") and callable(value.is_a)


class Canonicalizer:
    def __init__(self) -> None:
        self._cache: dict[int, Any] = {}
        self._active: set[int] = set()

    def value(self, value: Any) -> Any:
        if _is_entity(value):
            return self.entity(value)
        if isinstance(value, (tuple, list)):
            return [self.value(item) for item in value]
        if isinstance(value, dict):
            return {
                str(key): self.value(item)
                for key, item in sorted(value.items(), key=lambda pair: str(pair[0]))
                if str(key) not in VOLATILE_PSET_KEYS
            }
        if isinstance(value, (str, int, float, bool)) or value is None:
            return value
        return str(value)

    def entity(self, entity: Any) -> Any:
        global_id = getattr(entity, "GlobalId", None)
        if global_id:
            return {"$root": str(global_id), "type": str(entity.is_a())}

        step_id = int(entity.id()) if entity.id() else 0
        if not step_id:
            return {
                "type": str(entity.is_a()),
                "value": [self.value(item) for item in entity],
            }
        if step_id in self._cache:
            return self._cache[step_id]
        if step_id in self._active:
            return {"$cycle": str(entity.is_a())}

        self._active.add(step_id)
        try:
            info = entity.get_info(include_identifier=False, recursive=False)
            payload = {
                "type": str(entity.is_a()),
                "attributes": {
                    str(key): self.value(value)
                    for key, value in sorted(info.items())
                    if key != "type"
                },
            }
            self._cache[step_id] = payload
            return payload
        finally:
            self._active.discard(step_id)


def _root_payload(root: Any) -> dict[str, Any]:
    canonicalizer = Canonicalizer()
    info = root.get_info(include_identifier=False, recursive=False)
    attributes = {
        str(key): canonicalizer.value(value)
        for key, value in sorted(info.items())
        if key not in {"type", *GEOMETRY_ATTRIBUTES}
    }

    psets: dict[str, Any] = {}
    if root.is_a("IfcObject") or root.is_a("IfcTypeObject"):
        raw_psets = ifcopenshell.util.element.get_psets(
            root,
            should_inherit=False,
            verbose=False,
        )
        psets = canonicalizer.value(raw_psets)

    material: Any = None
    if root.is_a("IfcObject") or root.is_a("IfcTypeObject"):
        material = canonicalizer.value(
            ifcopenshell.util.element.get_material(
                root,
                should_skip_usage=False,
                should_inherit=False,
            )
        )

    return {
        "global_id": str(root.GlobalId),
        "type": str(root.is_a()),
        "attributes": attributes,
        "psets": psets,
        "material": material,
    }


def _payload_digest(payload: Any) -> str:
    raw = json.dumps(
        payload,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(raw).hexdigest()


def _layer_payload(layer: Any) -> dict[str, Any]:
    return {
        "type": str(layer.is_a()),
        "name": getattr(layer, "Name", None),
        "description": getattr(layer, "Description", None),
        "identifier": getattr(layer, "Identifier", None),
    }


def _styled_item_payload(
    item: Any, canonicalizer: Canonicalizer
) -> list[dict[str, Any]]:
    styled_items = getattr(item, "StyledByItem", None) or ()
    return sorted(
        (
            {
                "name": getattr(styled, "Name", None),
                "styles": canonicalizer.value(getattr(styled, "Styles", ()) or ()),
            }
            for styled in styled_items
        ),
        key=_payload_digest,
    )


def _representation_payload(
    representation: Any,
    canonicalizer: Canonicalizer,
    active_maps: set[int],
    *,
    appearance_only: bool = False,
) -> dict[str, Any]:
    items: list[dict[str, Any]] = []
    for item in getattr(representation, "Items", ()) or ():
        if item.is_a("IfcMappedItem"):
            source = item.MappingSource
            source_id = int(source.id()) if source.id() else 0
            if source_id and source_id in active_maps:
                source_payload: Any = {"cycle": "IfcRepresentationMap"}
            else:
                if source_id:
                    active_maps.add(source_id)
                try:
                    source_payload = {
                        "mapping_origin": (
                            None
                            if appearance_only
                            else canonicalizer.value(source.MappingOrigin)
                        ),
                        "representation": _representation_payload(
                            source.MappedRepresentation,
                            canonicalizer,
                            active_maps,
                            appearance_only=appearance_only,
                        ),
                    }
                finally:
                    if source_id:
                        active_maps.discard(source_id)
            items.append(
                {
                    "type": "IfcMappedItem",
                    "mapping_target": (
                        None
                        if appearance_only
                        else canonicalizer.value(item.MappingTarget)
                    ),
                    "source": source_payload,
                    "styles": _styled_item_payload(item, canonicalizer),
                }
            )
            continue

        items.append(
            {
                "type": (
                    "IfcGeometricRepresentationItem"
                    if appearance_only
                    else str(item.is_a())
                ),
                "styles": _styled_item_payload(item, canonicalizer),
                "layers": sorted(
                    (
                        _layer_payload(layer)
                        for layer in (getattr(item, "LayerAssignments", None) or ())
                    ),
                    key=_payload_digest,
                ),
            }
        )

    context = getattr(representation, "ContextOfItems", None)
    return {
        "identifier": getattr(representation, "RepresentationIdentifier", None),
        "representation_type": getattr(
            representation,
            "RepresentationType",
            None,
        )
        if not appearance_only
        else "GEOMETRY",
        "context": {
            "type": str(context.is_a()) if context is not None else None,
            "identifier": getattr(context, "ContextIdentifier", None),
            "context_type": getattr(context, "ContextType", None),
        },
        "layers": sorted(
            (
                _layer_payload(layer)
                for layer in (getattr(representation, "LayerAssignments", None) or ())
            ),
            key=_payload_digest,
        ),
        "items": items,
    }


def build_representation_manifest(
    model: Any,
    *,
    sink: EventSink = null_sink,
) -> dict[str, Any]:
    products = sorted(
        (
            product
            for product in model.by_type("IfcProduct")
            if getattr(product, "Representation", None) is not None
        ),
        key=lambda product: (str(product.GlobalId or ""), str(product.is_a())),
    )
    rows: list[dict[str, Any]] = []
    total = len(products)
    for index, product in enumerate(products, start=1):
        canonicalizer = Canonicalizer()
        payload = {
            "global_id": str(product.GlobalId),
            "type": str(product.is_a()),
            "representations": [
                _representation_payload(
                    representation,
                    canonicalizer,
                    set(),
                )
                for representation in product.Representation.Representations
            ],
        }
        appearance_payload = {
            "global_id": payload["global_id"],
            "type": payload["type"],
            "representations": [
                _representation_payload(
                    representation,
                    Canonicalizer(),
                    set(),
                    appearance_only=True,
                )
                for representation in product.Representation.Representations
            ],
        }
        rows.append(
            {
                "global_id": payload["global_id"],
                "type": payload["type"],
                "digest": _payload_digest(payload),
                "appearance_digest": _payload_digest(appearance_payload),
            }
        )
        if index % 100 == 0 or index == total:
            emit(
                sink,
                stage="representation-manifest",
                progress=index / max(total, 1),
                message=f"Проверено представлений: {index}/{total}",
            )
    return {
        "version": 1,
        "product_count": total,
        "digest": _payload_digest(
            [(row["global_id"], row["type"], row["digest"]) for row in rows]
        ),
        "products": rows,
    }


def compare_representation_manifests(
    source: dict[str, Any],
    output: dict[str, Any],
    *,
    allow_geometry_representation_changes: bool = False,
) -> dict[str, Any]:
    digest_key = (
        "appearance_digest" if allow_geometry_representation_changes else "digest"
    )
    source_rows = {
        (row["global_id"], row["type"]): row[digest_key] for row in source["products"]
    }
    output_rows = {
        (row["global_id"], row["type"]): row[digest_key] for row in output["products"]
    }
    missing = sorted(set(source_rows) - set(output_rows))
    added = sorted(set(output_rows) - set(source_rows))
    changed = sorted(
        key
        for key in set(source_rows) & set(output_rows)
        if source_rows[key] != output_rows[key]
    )
    return {
        "ok": not missing and not added and not changed,
        "geometry_representation_changes_allowed": (
            allow_geometry_representation_changes
        ),
        "missing_products": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in missing
        ],
        "added_products": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in added
        ],
        "changed_products": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in changed
        ],
    }


def build_semantic_manifest(
    model: Any,
    *,
    sink: EventSink = null_sink,
    output_path: Path | None = None,
) -> dict[str, Any]:
    roots = sorted(
        model.by_type("IfcRoot"),
        key=lambda root: (str(root.GlobalId or ""), str(root.is_a())),
    )
    rows: list[dict[str, Any]] = []
    total = len(roots)
    for index, root in enumerate(roots, start=1):
        payload = _root_payload(root)
        rows.append(
            {
                "global_id": payload["global_id"],
                "type": payload["type"],
                "digest": _payload_digest(payload),
                "payload": payload,
            }
        )
        if index % 100 == 0 or index == total:
            emit(
                sink,
                stage="semantic-manifest",
                progress=index / max(total, 1),
                message=f"Зафиксировано объектов: {index}/{total}",
            )

    manifest = {
        "version": 1,
        "schema": str(model.schema),
        "root_count": total,
        "digest": _payload_digest(
            [(row["global_id"], row["type"], row["digest"]) for row in rows]
        ),
        "roots": rows,
    }
    if output_path is not None:
        write_json_atomic(output_path, manifest)
    return manifest


def compare_semantic_manifests(
    source: dict[str, Any],
    output: dict[str, Any],
) -> dict[str, Any]:
    source_rows = {
        (row["global_id"], row["type"]): row["digest"] for row in source["roots"]
    }
    output_rows = {
        (row["global_id"], row["type"]): row["digest"] for row in output["roots"]
    }
    missing = sorted(set(source_rows) - set(output_rows))
    added = sorted(set(output_rows) - set(source_rows))
    changed = sorted(
        key
        for key in set(source_rows) & set(output_rows)
        if source_rows[key] != output_rows[key]
    )
    return {
        "ok": (
            source.get("schema") == output.get("schema")
            and not missing
            and not added
            and not changed
        ),
        "schema_equal": source.get("schema") == output.get("schema"),
        "missing_roots": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in missing
        ],
        "added_roots": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in added
        ],
        "changed_roots": [
            {"global_id": global_id, "type": type_name}
            for global_id, type_name in changed
        ],
    }
