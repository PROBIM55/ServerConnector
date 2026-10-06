"""Headless AGR publication worker executed by a pinned Blender LTS runtime.

Protocol:
    blender --background --factory-startup --python agr_worker.py -- \
        --manifest <job-root>/publication-build.json

The script only reads files below the manifest directory and only writes into
the configured relative output directory. Progress and the terminal result are
written as single-line JSON records prefixed with AGR_PROGRESS / AGR_RESULT.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import random
import struct
import sys
import traceback
from pathlib import Path
from typing import Any

import bmesh
import bpy
import numpy as np
from mathutils import Matrix


WORKER_VERSION = "0.2.0"
SUPPORTED_BLENDER_VERSION = (5, 2, 0)
FBX_BINARY_MAGIC = b"Kaydara FBX Binary  \x00\x1a\x00"
SUPPORTED_FBX_VERSION = 7400


class AgrWorkerError(RuntimeError):
    pass


def emit(kind: str, payload: dict[str, Any]) -> None:
    print(f"AGR_{kind} {json.dumps(payload, ensure_ascii=False, separators=(',', ':'))}", flush=True)


def progress(phase: str, completed: int, total: int, message: str) -> None:
    emit("PROGRESS", {
        "phase": phase,
        "completed": completed,
        "total": total,
        "message": message,
    })


def require_object(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise AgrWorkerError(f"{label} must be an object")
    return value


def require_list(value: Any, label: str) -> list[Any]:
    if not isinstance(value, list):
        raise AgrWorkerError(f"{label} must be an array")
    return value


def resolve_inside(root: Path, relative_path: str, label: str) -> Path:
    candidate = (root / relative_path).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise AgrWorkerError(f"{label} escapes the job directory") from error
    return candidate


def resolve_from(job_root: Path, base: Path, relative_path: str, label: str) -> Path:
    if Path(relative_path).is_absolute():
        raise AgrWorkerError(f"{label} must be relative to the job directory")
    candidate = (base / relative_path).resolve()
    try:
        candidate.relative_to(job_root)
    except ValueError as error:
        raise AgrWorkerError(f"{label} escapes the job directory") from error
    return candidate


def load_json(path: Path, label: str) -> dict[str, Any]:
    try:
        with path.open("r", encoding="utf-8") as stream:
            return require_object(json.load(stream), label)
    except (OSError, json.JSONDecodeError) as error:
        raise AgrWorkerError(f"Cannot read {label}: {error}") from error


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


class BufferStore:
    def __init__(self, job_root: Path, package_root: Path, definitions: list[Any]) -> None:
        self._job_root = job_root
        self._package_root = package_root
        self._definitions: dict[str, dict[str, Any]] = {}
        self._paths: dict[str, Path] = {}

        for raw in definitions:
            definition = require_object(raw, "buffer")
            buffer_id = str(definition.get("id", "")).strip()
            if not buffer_id or buffer_id in self._definitions:
                raise AgrWorkerError(f"Invalid or duplicate buffer id: {buffer_id!r}")
            self._definitions[buffer_id] = definition

    def get_path(self, buffer_id: str) -> Path:
        cached = self._paths.get(buffer_id)
        if cached is not None:
            return cached

        definition = self._definitions.get(buffer_id)
        if definition is None:
            raise AgrWorkerError(f"Unknown buffer id: {buffer_id}")

        path = resolve_from(
            self._job_root,
            self._package_root,
            str(definition["uri"]),
            f"buffer {buffer_id}",
        )
        try:
            actual_length = path.stat().st_size
        except OSError as error:
            raise AgrWorkerError(f"Cannot read buffer {buffer_id}: {error}") from error

        expected_length = int(definition["byteLength"])
        if actual_length != expected_length:
            raise AgrWorkerError(
                f"Buffer {buffer_id} length mismatch: expected {expected_length}, got {actual_length}"
            )
        expected_hash = str(definition["sha256"]).lower()
        actual_hash = sha256_file(path)
        if actual_hash != expected_hash:
            raise AgrWorkerError(
                f"Buffer {buffer_id} hash mismatch: expected {expected_hash}, got {actual_hash}"
            )

        self._paths[buffer_id] = path
        return path

    def view(self, raw_view: Any, label: str) -> np.ndarray:
        view = require_object(raw_view, label)
        component_type = str(view.get("componentType"))
        dtype_by_type = {"float32": np.dtype("<f4"), "uint32": np.dtype("<u4")}
        dtype = dtype_by_type.get(component_type)
        if dtype is None:
            raise AgrWorkerError(f"Unsupported component type in {label}: {component_type}")

        element_count = int(view.get("elementCount", -1))
        component_count = int(view.get("componentCount", -1))
        byte_offset = int(view.get("byteOffset", -1))
        if element_count < 0 or component_count < 1 or component_count > 4 or byte_offset < 0:
            raise AgrWorkerError(f"Invalid dimensions in {label}")

        scalar_count = element_count * component_count
        byte_length = scalar_count * dtype.itemsize
        buffer_path = self.get_path(str(view.get("bufferId", "")))
        if byte_offset + byte_length > buffer_path.stat().st_size:
            raise AgrWorkerError(f"{label} exceeds its binary buffer")

        values = np.memmap(
            buffer_path,
            dtype=dtype,
            mode="r",
            offset=byte_offset,
            shape=(scalar_count,),
        )
        return values.reshape((element_count, component_count))


def clear_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for collection in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for value in list(collection):
            if value.users == 0:
                collection.remove(value)


def configure_scene(max_threads: int) -> None:
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.length_unit = "METERS"
    scene.unit_settings.scale_length = 1.0
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.threads_mode = "FIXED"
    scene.render.threads = max_threads


def validate_runtime_version() -> None:
    actual = tuple(bpy.app.version)
    if actual != SUPPORTED_BLENDER_VERSION:
        expected = ".".join(str(value) for value in SUPPORTED_BLENDER_VERSION)
        actual_text = ".".join(str(value) for value in actual)
        raise AgrWorkerError(
            f"Unsupported Blender runtime {actual_text}; worker {WORKER_VERSION} requires {expected}"
        )


def set_principled_input(shader: Any, name: str, value: Any) -> None:
    socket = shader.inputs.get(name)
    if socket is not None:
        socket.default_value = value


def create_materials(
    job_root: Path,
    package_root: Path,
    package: dict[str, Any],
) -> dict[str, Any]:
    texture_by_id = {
        str(item["id"]): require_object(item, "texture")
        for item in require_list(package.get("textures"), "textures")
    }
    result: dict[str, Any] = {}

    for raw in require_list(package.get("materials"), "materials"):
        definition = require_object(raw, "material")
        material_id = str(definition.get("id", "")).strip()
        if not material_id or material_id in result:
            raise AgrWorkerError(f"Invalid or duplicate material id: {material_id!r}")

        material = bpy.data.materials.new(str(definition.get("name") or material_id))
        material.use_nodes = True
        nodes = material.node_tree.nodes
        links = material.node_tree.links
        shader = nodes.get("Principled BSDF")
        if shader is None:
            raise AgrWorkerError("Blender Principled BSDF node is unavailable")

        base_color = require_list(definition.get("baseColor"), f"material {material_id}.baseColor")
        if len(base_color) != 4:
            raise AgrWorkerError(f"Material {material_id} baseColor must contain four values")
        color = tuple(float(component) for component in base_color)
        set_principled_input(shader, "Base Color", color)
        set_principled_input(shader, "Metallic", float(definition.get("metallic", 0)))
        set_principled_input(shader, "Roughness", float(definition.get("roughness", 0.5)))
        set_principled_input(shader, "Alpha", color[3])

        opacity_mode = str(definition.get("opacityMode", "opaque"))
        if opacity_mode != "opaque":
            if hasattr(material, "surface_render_method"):
                material.surface_render_method = "DITHERED"
            elif hasattr(material, "blend_method"):
                material.blend_method = "BLEND"

        for texture_id in require_list(definition.get("textureIds"), f"material {material_id}.textureIds"):
            texture = texture_by_id.get(str(texture_id))
            if texture is None:
                raise AgrWorkerError(f"Material {material_id} references unknown texture {texture_id}")
            texture_path = resolve_from(
                job_root,
                package_root,
                str(texture["uri"]),
                f"texture {texture_id}",
            )
            expected_hash = str(texture["sha256"]).lower()
            actual_hash = sha256_file(texture_path)
            if actual_hash != expected_hash:
                raise AgrWorkerError(f"Texture {texture_id} hash mismatch")

            image = bpy.data.images.load(str(texture_path), check_existing=True)
            image.colorspace_settings.name = (
                "sRGB" if texture.get("colorSpace") == "srgb" else "Non-Color"
            )
            image_node = nodes.new("ShaderNodeTexImage")
            image_node.image = image
            image_node.label = str(texture.get("semantic"))
            semantic = str(texture.get("semantic"))
            if semantic == "base-color":
                # Keep the link direct: Blender's FBX exporter recognizes and
                # embeds image textures connected straight to Base Color.
                links.new(image_node.outputs["Color"], shader.inputs["Base Color"])
                if opacity_mode != "opaque" and image_node.outputs.get("Alpha") and shader.inputs.get("Alpha"):
                    links.new(image_node.outputs["Alpha"], shader.inputs["Alpha"])
            elif semantic == "normal":
                normal_node = nodes.new("ShaderNodeNormalMap")
                links.new(image_node.outputs["Color"], normal_node.inputs["Color"])
                links.new(normal_node.outputs["Normal"], shader.inputs["Normal"])
            elif semantic in {"roughness", "metallic"} and shader.inputs.get(semantic.title()):
                links.new(image_node.outputs["Color"], shader.inputs[semantic.title()])

        material["agr_material_id"] = material_id
        material["agr_opacity_mode"] = opacity_mode
        result[material_id] = material

    return result


def normalize_positions(values: np.ndarray, units: str, world_up: str) -> np.ndarray:
    if values.shape[1] != 3:
        raise AgrWorkerError("Position buffer must have three components")
    result = values.astype(np.float64, copy=True)
    if units == "mm":
        result *= 0.001
    elif units != "m":
        raise AgrWorkerError(f"Unsupported package units: {units}")
    if world_up == "y":
        result = result[:, [0, 2, 1]]
        result[:, 1] *= -1.0
    elif world_up != "z":
        raise AgrWorkerError(f"Unsupported package worldUp: {world_up}")
    return result


def recalculate_normals(mesh: Any) -> None:
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        bm.to_mesh(mesh)
    finally:
        bm.free()


def apply_vertex_uv(mesh: Any, values: np.ndarray) -> None:
    if values.shape != (len(mesh.vertices), 2):
        raise AgrWorkerError("uv0 buffer must contain two values per vertex")
    uv_layer = mesh.uv_layers.new(name="UVMap")
    for loop in mesh.loops:
        uv_layer.data[loop.index].uv = values[loop.vertex_index]


def create_mesh_objects(
    package: dict[str, Any],
    buffers: BufferStore,
    materials: dict[str, Any],
    geometry_options: dict[str, Any],
) -> list[Any]:
    objects: list[Any] = []
    units = str(package.get("units"))
    world_up = str(package.get("worldUp"))
    raw_meshes = require_list(package.get("meshes"), "meshes")

    for index, raw in enumerate(raw_meshes):
        definition = require_object(raw, "mesh")
        if definition.get("role") == "exclude":
            continue
        mesh_id = str(definition.get("id", "")).strip()
        progress("mesh-import", index, len(raw_meshes), f"Импортируется {mesh_id}")

        positions = normalize_positions(
            buffers.view(definition.get("positions"), f"mesh {mesh_id}.positions"),
            units,
            world_up,
        )
        indices = buffers.view(definition.get("indices"), f"mesh {mesh_id}.indices")
        if indices.shape[1] != 1:
            raise AgrWorkerError(f"Mesh {mesh_id} index buffer must have one component")
        flat_indices = indices[:, 0].astype(np.int64, copy=False)
        if len(flat_indices) % 3 != 0:
            raise AgrWorkerError(f"Mesh {mesh_id} index count is not divisible by three")
        if len(flat_indices) and (flat_indices.min() < 0 or flat_indices.max() >= len(positions)):
            raise AgrWorkerError(f"Mesh {mesh_id} contains an out-of-range index")
        faces = flat_indices.reshape((-1, 3)).tolist()

        mesh = bpy.data.meshes.new(str(definition.get("name") or mesh_id))
        mesh.from_pydata(positions.tolist(), [], faces)
        mesh.validate(clean_customdata=False)
        mesh.update(calc_edges=True)

        primitive_materials: list[str] = []
        for raw_primitive in require_list(definition.get("primitives"), f"mesh {mesh_id}.primitives"):
            primitive = require_object(raw_primitive, "primitive")
            material_id = str(primitive.get("materialId", ""))
            material = materials.get(material_id)
            if material is None:
                raise AgrWorkerError(f"Mesh {mesh_id} references unknown material {material_id}")
            if material_id not in primitive_materials:
                primitive_materials.append(material_id)
                mesh.materials.append(material)
            material_index = primitive_materials.index(material_id)
            first_index = int(primitive.get("firstIndex", -1))
            index_count = int(primitive.get("indexCount", -1))
            if first_index < 0 or index_count < 3 or first_index % 3 or index_count % 3:
                raise AgrWorkerError(f"Mesh {mesh_id} has an invalid primitive range")
            first_polygon = first_index // 3
            polygon_count = index_count // 3
            if first_polygon + polygon_count > len(mesh.polygons):
                raise AgrWorkerError(f"Mesh {mesh_id} primitive exceeds its index buffer")
            for polygon_index in range(first_polygon, first_polygon + polygon_count):
                mesh.polygons[polygon_index].material_index = material_index

        if definition.get("uv0") is not None:
            uv_values = buffers.view(definition.get("uv0"), f"mesh {mesh_id}.uv0")
            apply_vertex_uv(mesh, uv_values)

        if bool(geometry_options.get("recalculateNormals", True)):
            recalculate_normals(mesh)

        obj = bpy.data.objects.new(str(definition.get("name") or mesh_id), mesh)
        bpy.context.scene.collection.objects.link(obj)
        transform = definition.get("transform")
        if transform is not None:
            transform_values = require_list(transform, f"mesh {mesh_id}.transform")
            if len(transform_values) != 16:
                raise AgrWorkerError(f"Mesh {mesh_id} transform must contain 16 values")
            obj.matrix_world = Matrix([transform_values[row * 4:(row + 1) * 4] for row in range(4)])
        if bool(geometry_options.get("applyTransforms", True)):
            mesh.transform(obj.matrix_world)
            obj.matrix_world = Matrix.Identity(4)

        obj["agr_mesh_id"] = mesh_id
        obj["agr_role"] = str(definition.get("role"))
        obj["agr_publication_object_id"] = str(definition.get("publicationObjectId"))
        obj["agr_entity_ids"] = json.dumps(definition.get("entityIds", []), ensure_ascii=False)
        obj["agr_source_module_ids"] = json.dumps(definition.get("sourceModuleIds", []), ensure_ascii=False)
        objects.append(obj)

    progress("mesh-import", len(raw_meshes), len(raw_meshes), "Импорт мешей завершён")
    return objects


def decimate_to_budget(objects: list[Any], target_triangle_count: int | None) -> None:
    if target_triangle_count is None:
        return
    current = sum(len(obj.data.polygons) for obj in objects if obj.type == "MESH")
    if current <= target_triangle_count or current == 0:
        return
    ratio = max(0.001, min(1.0, target_triangle_count / current))
    progress("decimate", 0, len(objects), f"Упрощение {current} → {target_triangle_count} треугольников")
    for index, obj in enumerate(objects):
        if obj.type != "MESH" or len(obj.data.polygons) < 8:
            continue
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        modifier = obj.modifiers.new(name="AGR Decimate", type="DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = ratio
        modifier.use_collapse_triangulate = True
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        obj.select_set(False)
        progress("decimate", index + 1, len(objects), obj.name)


def sanitize_file_name(value: str) -> str:
    allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-"
    result = "".join(character if character in allowed else "_" for character in value)
    result = result.strip("._")
    if not result:
        raise AgrWorkerError("Output filename became empty after sanitization")
    return result[:128]


def inspect_fbx(path: Path) -> dict[str, Any]:
    with path.open("rb") as stream:
        header = stream.read(len(FBX_BINARY_MAGIC) + 4)
    binary = header.startswith(FBX_BINARY_MAGIC)
    version = struct.unpack_from("<I", header, len(FBX_BINARY_MAGIC))[0] if binary else None
    return {
        "fileName": path.name,
        "byteLength": path.stat().st_size,
        "binary": binary,
        "fbxVersion": version,
        "sha256": sha256_file(path),
    }


def export_group(
    output_path: Path,
    objects: list[Any],
    fbx_options: dict[str, Any],
) -> dict[str, Any]:
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    if objects:
        bpy.context.view_layer.objects.active = objects[0]

    result = bpy.ops.export_scene.fbx(
        filepath=str(output_path),
        use_selection=True,
        object_types={"MESH", "LIGHT"},
        use_custom_props=True,
        use_mesh_modifiers=True,
        use_triangles=True,
        use_tspace=True,
        add_leaf_bones=False,
        bake_anim=False,
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward=str(fbx_options["axisForward"]),
        axis_up=str(fbx_options["axisUp"]),
        path_mode="COPY",
        embed_textures=bool(fbx_options["embedTextures"]),
    )
    if result != {"FINISHED"} or not output_path.is_file():
        raise AgrWorkerError(f"Blender FBX export failed for {output_path.name}: {result}")

    inventory = inspect_fbx(output_path)
    if not inventory["binary"]:
        raise AgrWorkerError(f"Blender produced a non-binary FBX: {output_path.name}")
    if inventory["fbxVersion"] != SUPPORTED_FBX_VERSION:
        raise AgrWorkerError(
            f"Blender produced FBX {inventory['fbxVersion']}, expected {SUPPORTED_FBX_VERSION}"
        )
    inventory["triangleCount"] = sum(len(obj.data.polygons) for obj in objects if obj.type == "MESH")
    inventory["objectCount"] = len(objects)
    return inventory


def export_outputs(
    objects: list[Any],
    manifest: dict[str, Any],
    output_directory: Path,
) -> list[dict[str, Any]]:
    output = require_object(manifest.get("output"), "output")
    fbx_options = require_object(manifest.get("fbx"), "fbx")
    base_name = sanitize_file_name(str(output.get("baseName")))
    model_kind = str(manifest.get("modelKind"))
    target = str(manifest.get("target"))

    groups: list[tuple[str, list[Any]]] = []
    if model_kind == "low-poly" and target == "project":
        by_publication_object: dict[tuple[str, str], list[Any]] = {}
        for obj in objects:
            role = str(obj.get("agr_role"))
            if role not in {"oks", "ground"}:
                continue
            key = (role, str(obj.get("agr_publication_object_id")))
            by_publication_object.setdefault(key, []).append(obj)
        for (role, object_id), grouped_objects in sorted(by_publication_object.items()):
            suffix = "Ground" if role == "ground" else sanitize_file_name(object_id)
            groups.append((f"{base_name}_{suffix}", grouped_objects))
    else:
        expected_role = "ground" if target == "ground" else "oks"
        primary = [obj for obj in objects if str(obj.get("agr_role")) in {expected_role, "glass", "collision"}]
        if primary:
            groups.append((base_name, primary))
        lighting = [obj for obj in objects if str(obj.get("agr_role")) == "lighting"]
        if lighting:
            groups.append((f"{base_name}_Lighting", lighting))

    if not groups:
        raise AgrWorkerError("No objects match the requested AGR target")

    inventories: list[dict[str, Any]] = []
    for index, (name, grouped_objects) in enumerate(groups):
        progress("fbx-export", index, len(groups), f"Формируется {name}.fbx")
        inventories.append(export_group(output_directory / f"{name}.fbx", grouped_objects, fbx_options))
    progress("fbx-export", len(groups), len(groups), "Экспорт FBX завершён")
    return inventories


def reopen_and_validate_outputs(
    output_directory: Path,
    inventories: list[dict[str, Any]],
    expected_texture_count: int,
) -> None:
    for index, inventory in enumerate(inventories):
        file_name = str(inventory["fileName"])
        progress("fbx-reopen", index, len(inventories), f"Проверяется {file_name}")
        clear_scene()
        result = bpy.ops.import_scene.fbx(
            filepath=str(output_directory / file_name),
            use_custom_normals=True,
            use_custom_props=True,
            use_custom_props_enum_as_string=True,
        )
        if result != {"FINISHED"}:
            raise AgrWorkerError(f"Blender could not reopen {file_name}: {result}")

        imported_objects = [
            obj for obj in bpy.context.scene.objects if obj.type in {"MESH", "LIGHT"}
        ]
        imported_meshes = [obj for obj in imported_objects if obj.type == "MESH"]
        triangle_count = sum(len(obj.data.polygons) for obj in imported_meshes)
        custom_properties_present = all(
            "agr_mesh_id" in obj and "agr_entity_ids" in obj for obj in imported_meshes
        )
        imported_materials = {
            material
            for obj in imported_meshes
            for material in obj.data.materials
            if material is not None
        }
        texture_images = {
            node.image.name
            for material in imported_materials
            if material.use_nodes and material.node_tree is not None
            for node in material.node_tree.nodes
            if node.type == "TEX_IMAGE" and node.image is not None
        }
        if triangle_count != int(inventory["triangleCount"]):
            raise AgrWorkerError(
                f"Triangle count changed after reopening {file_name}: "
                f"{inventory['triangleCount']} -> {triangle_count}"
            )
        if not custom_properties_present:
            raise AgrWorkerError(f"AGR custom properties were lost in {file_name}")
        if expected_texture_count > 0 and len(texture_images) < expected_texture_count:
            raise AgrWorkerError(
                f"Embedded textures were lost in {file_name}: "
                f"expected {expected_texture_count}, reopened {len(texture_images)}"
            )

        inventory["reopenValidation"] = {
            "ok": True,
            "objectCount": len(imported_objects),
            "triangleCount": triangle_count,
            "customPropertiesPresent": custom_properties_present,
            "textureImageCount": len(texture_images),
        }
    progress("fbx-reopen", len(inventories), len(inventories), "Проверка FBX завершена")


def write_result_manifest(
    output_directory: Path,
    manifest: dict[str, Any],
    inventories: list[dict[str, Any]],
) -> Path:
    result_path = output_directory / "agr-build-result.json"
    result = {
        "schemaVersion": 1,
        "ok": True,
        "jobId": manifest["jobId"],
        "profileId": manifest["profileId"],
        "snapshotHash": manifest["snapshotHash"],
        "workerVersion": WORKER_VERSION,
        "blenderVersion": bpy.app.version_string,
        "files": inventories,
    }
    result_path.write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return result_path


def preflight() -> int:
    exporter_available = hasattr(bpy.ops.export_scene, "fbx")
    importer_available = hasattr(bpy.ops.import_scene, "fbx")
    cycles_cpu_available = False
    previous_engine = bpy.context.scene.render.engine
    try:
        bpy.context.scene.render.engine = "CYCLES"
        cycles_cpu_available = bpy.context.scene.render.engine == "CYCLES"
    except Exception:
        cycles_cpu_available = False
    finally:
        bpy.context.scene.render.engine = previous_engine
    version_supported = tuple(bpy.app.version) == SUPPORTED_BLENDER_VERSION
    result = {
        "ok": (
            version_supported
            and bool(bpy.app.background)
            and exporter_available
            and importer_available
            and cycles_cpu_available
        ),
        "workerVersion": WORKER_VERSION,
        "blenderVersion": bpy.app.version_string,
        "blenderVersionTuple": list(bpy.app.version),
        "requiredBlenderVersionTuple": list(SUPPORTED_BLENDER_VERSION),
        "versionSupported": version_supported,
        "background": bool(bpy.app.background),
        "fbxExporterAvailable": exporter_available,
        "fbxImporterAvailable": importer_available,
        "expectedFbxVersion": SUPPORTED_FBX_VERSION,
        "cyclesCpuAvailable": cycles_cpu_available,
    }
    emit("RESULT", result)
    return 0 if result["ok"] else 2


def run_manifest(manifest_path: Path) -> dict[str, Any]:
    validate_runtime_version()
    manifest_path = manifest_path.resolve()
    job_root = manifest_path.parent
    manifest = load_json(manifest_path, "publication build manifest")
    if manifest.get("schemaVersion") != 1:
        raise AgrWorkerError("Unsupported publication build manifest version")
    if manifest.get("snapshotHash") is None:
        raise AgrWorkerError("Build manifest has no snapshotHash")

    package_path = resolve_inside(job_root, str(manifest.get("meshPackage")), "meshPackage")
    package = load_json(package_path, "publication mesh package")
    if package.get("schemaVersion") != 1:
        raise AgrWorkerError("Unsupported publication mesh package version")
    if package.get("snapshotHash") != manifest.get("snapshotHash"):
        raise AgrWorkerError("Snapshot hash differs between manifest and mesh package")

    output = require_object(manifest.get("output"), "output")
    output_directory = resolve_inside(job_root, str(output.get("directory")), "output.directory")
    output_directory.mkdir(parents=True, exist_ok=True)
    resource_limits = require_object(manifest.get("resourceLimits"), "resourceLimits")
    max_threads = max(1, min(64, int(resource_limits.get("maxThreads", 1))))

    random_seed = int(manifest.get("randomSeed", 0))
    random.seed(random_seed)
    np.random.seed(random_seed & 0xFFFFFFFF)
    clear_scene()
    configure_scene(max_threads)

    package_root = package_path.parent
    buffers = BufferStore(
        job_root,
        package_root,
        require_list(package.get("buffers"), "buffers"),
    )
    materials = create_materials(job_root, package_root, package)
    geometry_options = require_object(manifest.get("geometry"), "geometry")
    texturing_options = require_object(manifest.get("texturing"), "texturing")
    fbx_options = require_object(manifest.get("fbx"), "fbx")
    unsupported: list[str] = []
    if manifest.get("modelKind") == "high-poly" and manifest.get("target") == "project":
        unsupported.append("target=project for high-poly")
    if bool(output.get("writeGeoJson", False)):
        unsupported.append("output.writeGeoJson")
    if not bool(geometry_options.get("triangulate", False)):
        unsupported.append("geometry.triangulate=false")
    if geometry_options.get("mergeBy", "none") != "none":
        unsupported.append("geometry.mergeBy")
    if float(geometry_options.get("weldToleranceMeters", 0)) != 0:
        unsupported.append("geometry.weldToleranceMeters")
    if texturing_options.get("mode", "preserve") != "preserve":
        unsupported.append("texturing.mode")
    if (
        fbx_options.get("binary") is not True
        or fbx_options.get("formatVersion") != "7.4"
        or fbx_options.get("compatibilityVersion") != "2014"
        or float(fbx_options.get("metersPerUnit", 0)) != 1.0
    ):
        unsupported.append("fbx format contract")
    if unsupported:
        raise AgrWorkerError(
            "Worker 0.1 does not silently approximate unsupported operations: "
            + ", ".join(unsupported)
        )
    objects = create_mesh_objects(package, buffers, materials, geometry_options)
    target_triangle_count = geometry_options.get("targetTriangleCount")
    decimate_to_budget(objects, int(target_triangle_count) if target_triangle_count is not None else None)
    inventories = export_outputs(objects, manifest, output_directory)
    expected_texture_count = len(require_list(package.get("textures"), "textures")) \
        if bool(fbx_options.get("embedTextures", False)) else 0
    reopen_and_validate_outputs(output_directory, inventories, expected_texture_count)
    result_path = write_result_manifest(output_directory, manifest, inventories)
    return {
        "ok": True,
        "jobId": manifest["jobId"],
        "workerVersion": WORKER_VERSION,
        "blenderVersion": bpy.app.version_string,
        "resultManifest": str(result_path.relative_to(job_root)).replace("\\", "/"),
        "files": inventories,
    }


def parse_args() -> argparse.Namespace:
    script_args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Platform AGR Blender worker")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--preflight", action="store_true")
    mode.add_argument("--manifest", type=Path)
    return parser.parse_args(script_args)


def main() -> int:
    args = parse_args()
    if args.preflight:
        return preflight()
    result = run_manifest(args.manifest)
    emit("RESULT", result)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SystemExit:
        raise
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit("RESULT", {
            "ok": False,
            "workerVersion": WORKER_VERSION,
            "error": type(error).__name__,
            "message": str(error),
        })
        raise SystemExit(1)
