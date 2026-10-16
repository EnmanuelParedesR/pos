"""Abastalia site renders (TK-274): stylized, low-detail scenes on a transparent background.

Run headless from the repo root (Blender 4.x):
    blender -b --factory-startup -P design/blender/escenas.py -- <out_dir> [scene ...]
Scenes: butcher, grocery, deli, counter. Output: <out_dir>/<scene>.webp (960x600, alpha). Cycles (the shadow catcher needs it), 48 samples + denoise.
Shapes are primitives on purpose: soft, friendly, readable at 300 px, and cheap to keep consistent.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

BRAND = "#0B5CAD"
INK = "#14181D"
PAPER = "#F7F7F5"


def rgb(hex_color, a=1.0):
    h = hex_color.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    return (*lin, a)


def material(name, color, rough=0.55, metal=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = rgb(color)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Metallic"].default_value = metal
    return m


def bevel(obj, width=0.03, segments=4):
    mod = obj.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    for p in getattr(obj.data, "polygons", []):
        p.use_smooth = True
    return obj


def box(name, size, loc, mat, rot=(0, 0, 0), r=0.03):
    bpy.ops.mesh.primitive_cube_add(location=loc, rotation=rot)
    o = bpy.context.object
    o.name = name
    o.scale = (size[0] / 2, size[1] / 2, size[2] / 2)
    bpy.ops.object.transform_apply(scale=True)
    o.data.materials.append(mat)
    return bevel(o, r)


def cyl(name, radius, depth, loc, mat, rot=(0, 0, 0), verts=48, r=0.02):
    bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=loc, rotation=rot, vertices=verts)
    o = bpy.context.object
    o.name = name
    o.data.materials.append(mat)
    return bevel(o, r)


def blob(name, scale, loc, mat, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, location=loc, rotation=rot)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(scale=True)
    o.data.materials.append(mat)
    bpy.ops.object.shade_smooth()
    return o


def text(body, loc, size, mat, rot=(math.radians(90), 0, 0)):
    bpy.ops.object.text_add(location=loc, rotation=rot)
    o = bpy.context.object
    o.data.body = body
    o.data.size = size
    o.data.align_x = "CENTER"
    o.data.align_y = "CENTER"
    o.data.extrude = 0.002
    o.data.materials.append(mat)
    return o


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"  # the shadow catcher (transparent floor with real shadows) only works in Cycles
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.render.resolution_x, scene.render.resolution_y = 960, 600
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "WEBP"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.quality = 82
    scene.view_settings.view_transform = "Standard"  # keeps the brand blue true (AgX washes it out)
    scene.view_settings.exposure = -0.35
    world = bpy.data.worlds.new("world")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = rgb("#FFFFFF")
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.35
    return scene


def lights_and_camera(target=(0, 0, 0.25), distance=4.2, height=2.4, angle=-35, lens=50):
    tgt = Vector(target)
    for name, energy, loc, size in (("key", 450, (-2.5, -3.0, 4.0), 3.0), ("fill", 160, (3.5, -2.0, 2.5), 4.0), ("rim", 220, (0.5, 3.5, 3.0), 2.0)):
        bpy.ops.object.light_add(type="AREA", location=loc)
        light = bpy.context.object
        light.name = name
        light.data.energy = energy
        light.data.size = size
        light.rotation_euler = (tgt - light.location).to_track_quat("-Z", "Y").to_euler()
    # Shadow catcher so objects sit on something without a visible floor.
    bpy.ops.mesh.primitive_plane_add(size=20, location=(0, 0, 0))
    floor = bpy.context.object
    floor.is_shadow_catcher = True
    floor.data.materials.append(material("floor", "#FFFFFF", rough=1))
    a = math.radians(angle)
    bpy.ops.object.camera_add(location=(tgt.x + distance * math.sin(a), tgt.y - distance * math.cos(a), height))
    cam = bpy.context.object
    cam.data.lens = lens
    cam.rotation_euler = (tgt - cam.location).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = cam


def scale_device(x, y, display="1.250"):
    """A small counter scale in brand blue with a stainless pan and a lit display."""
    blue, steel, screen, glow = material("brand", BRAND, 0.4), material("steel", "#C9CED4", 0.25, 0.9), material("screen", INK, 0.3), material("glow", "#7FD1FF", 0.2)
    glow.node_tree.nodes["Principled BSDF"].inputs["Emission Color"].default_value = rgb("#7FD1FF")
    glow.node_tree.nodes["Principled BSDF"].inputs["Emission Strength"].default_value = 2.0
    box("scale_base", (0.9, 0.7, 0.18), (x, y, 0.09), blue, r=0.05)
    cyl("scale_pan", 0.34, 0.03, (x, y + 0.04, 0.2), steel, r=0.01)
    box("scale_head", (0.5, 0.12, 0.22), (x, y - 0.42, 0.2), blue, rot=(math.radians(-15), 0, 0), r=0.03)
    box("scale_screen", (0.4, 0.02, 0.12), (x, y - 0.49, 0.22), screen, rot=(math.radians(-15), 0, 0), r=0.01)
    text(display, (x, y - 0.505, 0.22), 0.08, glow, rot=(math.radians(75), 0, 0))


def butcher():
    wood, meat, fat, steel, handle = material("wood", "#C8925A", 0.7), material("meat", "#C4473F", 0.5), material("fat", "#F3E6D8", 0.6), material("blade", "#D8DCE0", 0.2, 1.0), material("handle", INK, 0.5)
    box("board", (1.8, 1.1, 0.1), (-0.35, 0.1, 0.05), wood, r=0.04)
    steak = blob("steak", (0.52, 0.36, 0.14), (-0.55, 0.15, 0.2), meat)
    rim = blob("fat", (0.58, 0.41, 0.1), (-0.53, 0.17, 0.17), fat)
    blob("bone", (0.09, 0.09, 0.08), (-0.4, 0.06, 0.3), fat)
    box("cleaver_blade", (0.5, 0.02, 0.32), (0.25, 0.25, 0.2), steel, rot=(math.radians(90), 0, math.radians(20)), r=0.005)
    cyl("cleaver_handle", 0.035, 0.32, (0.58, 0.37, 0.12), handle, rot=(0, math.radians(90), math.radians(20)))
    scale_device(1.15, -0.1)
    lights_and_camera(target=(0.2, 0, 0.2), distance=4.3, height=2.5)


def grocery():
    shelf = material("shelf", BRAND, 0.45)
    colors = ["#E4572E", "#F3A712", "#29BF12", "#FFFFFF", "#4F6D7A", "#F3E6D8"]
    box("shelf_back", (2.4, 0.08, 1.7), (0, 0.45, 0.85), material("shelf_back", "#C9DAEE", 0.8), r=0.02)
    for z in (0.04, 0.84):
        box(f"board_{z}", (2.4, 0.8, 0.08), (0, 0.1, z), shelf, r=0.02)
    for side in (-1.2, 1.2):
        box(f"side_{side}", (0.08, 0.8, 1.7), (side, 0.1, 0.85), shelf, r=0.02)
    x = -0.95
    for i, c in enumerate(colors):  # cans on the bottom shelf
        cyl(f"can_{i}", 0.12, 0.36, (x + i * 0.36, 0.1, 0.26), material(f"can_{i}", c, 0.35, 0.3))
    x = -0.9
    for i, (w, h, c) in enumerate([(0.36, 0.52, "#F3A712"), (0.3, 0.6, "#E4572E"), (0.42, 0.4, "#FFFFFF"), (0.3, 0.5, "#29BF12"), (0.36, 0.46, "#4F6D7A")]):
        box(f"pack_{i}", (w, 0.3, h), (x, 0.1, 0.88 + h / 2), material(f"pack_{i}", c, 0.6))
        x += w + 0.08
    box("rice_bag", (0.42, 0.26, 0.56), (0.62, -0.5, 0.3), material("bag", "#E2C894", 0.85), rot=(0, 0, math.radians(-12)), r=0.1)
    box("rice_label", (0.3, 0.01, 0.16), (0.6, -0.64, 0.32), material("brand", BRAND, 0.4), rot=(0, 0, math.radians(-12)), r=0.02)
    lights_and_camera(target=(0, -0.05, 0.75), distance=5.0, height=2.0, angle=-25)


def deli():
    wood, cheese, rind, salami, slice_, ham = material("wood", "#C8925A", 0.7), material("cheese", "#F6C445", 0.45), material("rind", "#E0A82E", 0.6), material("salami", "#9E2B32", 0.5), material("slice", "#C4473F", 0.5), material("ham", "#E8A39B", 0.5)
    cyl("board", 0.95, 0.1, (0, 0.05, 0.05), wood, r=0.04)
    bpy.ops.mesh.primitive_cone_add(vertices=3, radius1=0.5, depth=0.35, location=(-0.35, 0.2, 0.28), rotation=(0, 0, math.radians(30)))
    wedge = bpy.context.object
    wedge.data.materials.append(cheese)
    bevel(wedge, 0.04)
    cyl("salami", 0.13, 0.75, (0.35, 0.3, 0.23), salami, rot=(0, math.radians(90), math.radians(-25)))
    for i in range(4):
        cyl(f"salami_slice_{i}", 0.13, 0.025, (0.35 + i * 0.16, -0.25 - i * 0.05, 0.12 + i * 0.012), slice_, rot=(math.radians(8), 0, 0))
    for i in range(3):
        blob(f"ham_{i}", (0.28, 0.2, 0.03), (-0.3 + i * 0.05, -0.35 + i * 0.04, 0.13 + i * 0.03), ham, rot=(0, 0, math.radians(20 * i)))
    lights_and_camera(target=(0, 0, 0.15), distance=3.0, height=2.2)


def counter():
    """The general scene for the home: scale, receipt printer and card terminal on a counter."""
    top, printer, paper, term = material("counter", "#D9CDBB", 0.6), material("printer", INK, 0.45), material("paper", "#FFFFFF", 0.8), material("terminal", "#2B323A", 0.4)
    box("counter_top", (2.7, 1.3, 0.12), (0, 0, 0.06), top, r=0.04)
    scale_device(-0.9, 0.05, "2.350")
    box("printer", (0.62, 0.62, 0.42), (0.2, 0.15, 0.33), printer, r=0.08)
    box("receipt", (0.42, 0.01, 0.55), (0.2, -0.2, 0.62), paper, rot=(math.radians(-12), 0, 0), r=0.005)
    for i in range(5):
        box(f"line_{i}", (0.3 - (i % 2) * 0.08, 0.012, 0.02), (0.2, -0.215, 0.8 - i * 0.07), material("ink_line", "#B9C0C8", 0.6), rot=(math.radians(-12), 0, 0), r=0.002)
    box("terminal", (0.4, 0.62, 0.12), (0.9, -0.1, 0.19), term, rot=(math.radians(-20), 0, math.radians(-10)), r=0.05)
    box("terminal_screen", (0.3, 0.25, 0.01), (0.9, -0.17, 0.27), material("brand", BRAND, 0.3), rot=(math.radians(-20), 0, math.radians(-10)), r=0.005)
    lights_and_camera(target=(0, 0, 0.3), distance=4.0, height=2.2, angle=-30)


SCENES = {"butcher": butcher, "grocery": grocery, "deli": deli, "counter": counter}

if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = os.path.abspath(args[0] if args else "design/blender/out")
    os.makedirs(out, exist_ok=True)
    for name in args[1:] or list(SCENES):
        scene = reset()
        SCENES[name]()
        scene.render.filepath = os.path.join(out, f"{name}.webp")
        bpy.ops.render.render(write_still=True)
        print(f"rendered {scene.render.filepath}")
