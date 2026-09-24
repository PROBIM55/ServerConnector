# Фикстура тестов Connector.AgrConversion: маленькая часть АГР SM_TestPart_001 (меньше 300 КБ).
#
# Как сделана (воспроизводимо, Blender 5.2 без окна):
#   "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" -b --factory-startup --python-exit-code 3 ^
#       --python make_fixture.py -- <папка Fixtures>
# - FBX пишет экспорт FBX Blender с осями и единицами пакета АГР (axis_forward='Y', axis_up='Z',
#   apply_scale_options='FBX_SCALE_UNITS' — метры в геометрии, единицы в заголовке; с умолчанием FBX_SCALE_NONE
#   Blender 5.2 кладёт x100 в трансформы объектов, и assimp даёт сантиметры):
#   в заголовке UpAxis=2, FrontAxis=1/-1, CoordAxis=0, UnitScaleFactor=100 — как у FBX пакета АВТОМАГИСТРАЛЬ
#   (Blender 4.3). Читатель сверяет эти поля и корневую матрицу assimp и предупреждает при расхождении.
# - PNG пишутся чистым Python (zlib) — точные значения пикселей, без управления цветом Blender.
# - geojson — json; превью imageBase64 — PNG 3x2.
#
# Содержимое (координаты — оси АГР = мировые координаты Blender, метры):
#   SM_TestPart_001_Main  (смещён на +0,5 м по X): материал M_TestPart_001_Main_1, 6 треугольников
#       тайл 1001: 2 тр.; тайл 1002: 2 тр. (первая вершина каждого — на правой границе тайла, u=2,0: тайл по
#       первой вершине дал бы 1003); тайл 1011: 2 тр. (v в [1; 2])
#   SM_TestPart_001_Glass (повёрнут на 90° вокруг Z): материал M_TestPart_001_MainGlass_1 — в паспорте Glasses, 1 тр.
#   UCX_SM_TestPart_001_Main_00: куб 12 тр. в стороне (x, y = 100..101) — коллизия, в модель не входит
#   SM_TestPart_001_Light.fbx: точечный источник — свет, пропускается
#   Габарит модели: min (1; 2; 0,5), max (4; 7; 1,5).
#   Текстуры набора T_TestPart_001_*_1:
#     1001: Diffuse/ERM/Normal 32x32 с шумом (настоящие; в R карты ERM нули)
#     1002: Diffuse 16x16 с разбросом R ровно 10/255 (не заглушка), ERM 8x8 (255; 100/102; 0) — разброс 2/255,
#           заглушка с emissive в R; Normal 4x4 (128; 128; 255)
#     1011: Diffuse 8x8 RGBA (200; 50; 25), альфа 0 в верхней половине (MASK, средняя 0,5); ERM (0; 128; 0);
#           Normal — 16-битный PNG (32896; 32896; 65535) = (128; 128; 255)
#     1005: Diffuse 4x4 (1; 2; 3) — ни одной грани, «unused»
import base64
import json
import math
import os
import random
import shutil
import struct
import sys
import zlib

import bpy

PART = "SM_TestPart_001"
STEM = "TestPart_001"


def png_bytes(w, h, channels, bitdepth, pixel):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            px = pixel(x, y)
            for c in range(channels):
                if bitdepth == 16:
                    raw += struct.pack(">H", px[c])
                else:
                    raw.append(px[c])
    colortype = {1: 0, 3: 2, 4: 6}[channels]

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, bitdepth, colortype, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def write_png(folder, name, w, h, channels, bitdepth, pixel):
    with open(os.path.join(folder, name), "wb") as f:
        f.write(png_bytes(w, h, channels, bitdepth, pixel))


def add_mesh(name, verts, tris, uvs, material, location=(0.0, 0.0, 0.0), rotation=(0.0, 0.0, 0.0)):
    me = bpy.data.meshes.new(name + "_mesh")
    me.from_pydata(verts, [], tris)
    me.update()
    if material is not None:
        me.materials.append(material)
    if uvs is not None:
        layer = me.uv_layers.new(name="UVMap")
        for poly in me.polygons:
            for k, li in enumerate(poly.loop_indices):
                layer.data[li].uv = uvs[poly.index][k]
    ob = bpy.data.objects.new(name, me)
    ob.location = location
    ob.rotation_euler = rotation
    bpy.context.scene.collection.objects.link(ob)
    return ob


def main():
    out_root = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
    folder = os.path.join(out_root, PART)
    if os.path.isdir(folder):
        shutil.rmtree(folder)
    os.makedirs(folder)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    m_main = bpy.data.materials.new("M_%s_Main_1" % STEM)
    m_glass = bpy.data.materials.new("M_%s_MainGlass_1" % STEM)

    world = [
        (1, 2, 0.5), (2, 2, 0.5), (2, 3, 0.5), (1, 3, 0.5),      # 0..3  тайл 1001
        (3, 4, 1.0), (4, 4, 1.0), (4, 5, 1.5), (3, 5, 1.5),      # 4..7  тайл 1002
        (1, 6, 0.8), (2, 6, 0.8), (2, 7, 0.8), (1, 7, 0.8),      # 8..11 тайл 1011
    ]
    main_loc = (0.5, 0.0, 0.0)
    local = [(x - main_loc[0], y - main_loc[1], z - main_loc[2]) for (x, y, z) in world]
    tris = [(0, 1, 2), (0, 2, 3), (5, 6, 4), (6, 7, 4), (8, 9, 10), (8, 10, 11)]
    uvs = [
        [(0.1, 0.1), (0.9, 0.1), (0.9, 0.9)],
        [(0.1, 0.1), (0.9, 0.9), (0.1, 0.9)],
        [(2.0, 0.1), (2.0, 0.9), (1.2, 0.1)],
        [(2.0, 0.9), (1.2, 0.9), (1.2, 0.1)],
        [(0.2, 1.2), (0.8, 1.2), (0.8, 1.8)],
        [(0.2, 1.2), (0.8, 1.8), (0.2, 1.8)],
    ]
    main = add_mesh(PART + "_Main", local, tris, uvs, m_main, location=main_loc)

    # стекло: мир (3; 6; 1,2), (4; 6; 1,2), (4; 7; 1,2) при повороте 90° вокруг Z: мир = (-ly, lx, lz)
    glass_local = [(6.0, -3.0, 1.2), (6.0, -4.0, 1.2), (7.0, -4.0, 1.2)]
    glass = add_mesh(PART + "_Glass", glass_local, [(0, 1, 2)], [[(0.3, 0.3), (0.6, 0.3), (0.6, 0.6)]], m_glass,
                     rotation=(0.0, 0.0, math.radians(90.0)))

    c = [(100 + dx, 100 + dy, dz) for dx in (0, 1) for dy in (0, 1) for dz in (0, 1)]
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    cube_tris = [t for q in quads for t in ((q[0], q[1], q[2]), (q[0], q[2], q[3]))]
    ucx = add_mesh("UCX_" + PART + "_Main_00", c, cube_tris, None, None)

    bpy.ops.object.select_all(action="DESELECT")
    for ob in (main, glass, ucx):
        ob.select_set(True)
    bpy.ops.export_scene.fbx(filepath=os.path.join(folder, PART + ".fbx"), use_selection=True,
                             object_types={"MESH"}, axis_forward="Y", axis_up="Z", apply_scale_options="FBX_SCALE_UNITS",
                             mesh_smooth_type="FACE",
                             add_leaf_bones=False, bake_anim=False, embed_textures=False)

    light_data = bpy.data.lights.new("L_" + STEM, type="POINT")
    light = bpy.data.objects.new(PART + "_Light", light_data)
    light.location = (2.0, 4.0, 5.0)
    bpy.context.scene.collection.objects.link(light)
    bpy.ops.object.select_all(action="DESELECT")
    light.select_set(True)
    bpy.ops.export_scene.fbx(filepath=os.path.join(folder, PART + "_Light.fbx"), use_selection=True,
                             object_types={"LIGHT"}, axis_forward="Y", axis_up="Z", apply_scale_options="FBX_SCALE_UNITS",
                             add_leaf_bones=False,
                             bake_anim=False)

    rnd = random.Random(20260924)
    t = "T_%s_%%s_1.%%d.png" % STEM
    write_png(folder, t % ("Diffuse", 1001), 32, 32, 3, 8,
              lambda x, y: (40 + x * 4 + rnd.randint(0, 20), 60 + y * 3 + rnd.randint(0, 20), 90 + rnd.randint(0, 40)))
    write_png(folder, t % ("ERM", 1001), 32, 32, 3, 8, lambda x, y: (0, 100 + rnd.randint(0, 100), 0))
    write_png(folder, t % ("Normal", 1001), 32, 32, 3, 8,
              lambda x, y: (128 + rnd.randint(-20, 20), 128 + rnd.randint(-20, 20), 255))
    write_png(folder, t % ("Diffuse", 1002), 16, 16, 3, 8, lambda x, y: (100 + x % 11, 110, 120))
    write_png(folder, t % ("ERM", 1002), 8, 8, 3, 8, lambda x, y: (255, 100 if (x + y) % 2 == 0 else 102, 0))
    write_png(folder, t % ("Normal", 1002), 4, 4, 3, 8, lambda x, y: (128, 128, 255))
    write_png(folder, t % ("Diffuse", 1011), 8, 8, 4, 8, lambda x, y: (200, 50, 25, 0 if y < 4 else 255))
    write_png(folder, t % ("ERM", 1011), 4, 4, 3, 8, lambda x, y: (0, 128, 0))
    write_png(folder, t % ("Normal", 1011), 4, 4, 3, 16, lambda x, y: (32896, 32896, 65535))
    write_png(folder, t % ("Diffuse", 1005), 4, 4, 3, 8, lambda x, y: (1, 2, 3))

    preview = base64.b64encode(png_bytes(3, 2, 3, 8, lambda x, y: (10 * x, 20 * y, 30))).decode("ascii")
    geo = {
        "type": "FeatureCollection",
        "name": PART,
        "features": [{
            "type": "Feature",
            "properties": {"name": PART, "h_relief": 145.25, "h_otn": 3.5, "imageBase64": preview},
            "geometry": {"type": "Point", "coordinates": [12345.678, 23456.789]},
            "Glasses": [{"M_%s_MainGlass_1" % STEM: {
                "color_RGB": {"Red": 175, "Green": 186, "Blue": 187}, "transparency": 1, "refraction": 1.5,
                "roughness": 0.859, "metallicity": 0.82}}],
        }],
    }
    with open(os.path.join(folder, PART + ".geojson"), "w", encoding="utf-8") as f:
        json.dump(geo, f, ensure_ascii=False, indent=1)

    total = 0
    for name in sorted(os.listdir(folder)):
        size = os.path.getsize(os.path.join(folder, name))
        total += size
        print("FIXTURE %-40s %7d" % (name, size))
    print("FIXTURE total %d bytes, blender %s" % (total, bpy.app.version_string))


main()
