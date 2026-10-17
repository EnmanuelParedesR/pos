"""TK-296: assembles the site's product video in Blender's sequencer and encodes it with Blender's own FFmpeg.

    blender -b --factory-startup --python design/video/montaje.py -- design/video/capturas src/Cloud/panel/public/media

Reads the script (src/Cloud/panel/src/pages/site/productVideo.json: shots, captions, descriptions), the takes of ProductVideoTests
(<capturas>/<take>/*.jpg named by milliseconds, with marks.json) and the overlays (<capturas>/overlays/*.png). Writes
<name>.mp4 (H.264), <name>.webm (VP9), <name>-portada.webp and <name>.es.vtt (the descriptions track) into the output folder.
No audio. See design/video/README.md.
"""
import json
import os
import sys

import bpy

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CAPTURES = os.path.abspath(args[0] if len(args) > 0 else os.path.join(ROOT, "design", "video", "capturas"))
OUT = os.path.abspath(args[1] if len(args) > 1 else os.path.join(ROOT, "src", "Cloud", "panel", "public", "media"))
SCRIPT = json.load(open(os.path.join(ROOT, "src", "Cloud", "panel", "src", "pages", "site", "productVideo.json"), encoding="utf-8"))

FPS, W, H = SCRIPT["fps"], SCRIPT["width"], SCRIPT["height"]
FADE = 8          # frames of each cross dissolve
TEXT_FADE = 6     # frames for a caption to fade in or out
ZOOM_IN = 24      # frames of the slow push-in
MAX_SPEED = 2.5   # a take plays at most this fast to fit its shot; shorter takes hold their last frame
OVERLAYS = os.path.join(CAPTURES, "overlays")


def frames_of(take):
    """(ms, file) of a take, sorted, and its marks in seconds."""
    folder = os.path.join(CAPTURES, take)
    files = sorted((int(f[:-4]), f) for f in os.listdir(folder) if f.endswith(".jpg"))
    marks = json.load(open(os.path.join(folder, "marks.json"), encoding="utf-8"))
    return folder, files, marks


def resample(files, start_s, end_s, count):
    """`count` output frames over [start_s, end_s] of the take: each the newest captured frame at that moment."""
    span = end_s - start_s
    speed = min(MAX_SPEED, max(1.0, span / (count / FPS)))
    picked, i = [], 0
    for f in range(count):
        t = (start_s + f / FPS * speed) * 1000
        while i + 1 < len(files) and files[i + 1][0] <= t:
            i += 1
        picked.append(files[i][1])
    return picked


def image_strip(seq, name, folder, names, channel, start):
    strip = seq.strips.new_image(name=name, filepath=os.path.join(folder, names[0]), channel=channel, frame_start=start, fit_method="FIT")
    for n in names[1:]:
        strip.elements.append(n)
    return strip


def still(seq, name, path, channel, start, length, fit="FIT"):
    strip = seq.strips.new_image(name=name, filepath=path, channel=channel, frame_start=start, fit_method=fit)
    strip.frame_final_duration = length
    strip.blend_type = "ALPHA_OVER"
    return strip


def zoom(strip, start, length, z):
    """A slow push-in toward (x, y) (0–1 from the top left) that keeps that point where it is."""
    s, x, y = z["scale"], z["x"], z["y"]
    t = strip.transform
    t.scale_x = t.scale_y = 1.0
    t.offset_x = t.offset_y = 0.0
    for prop in ("scale_x", "scale_y", "offset_x", "offset_y"):
        t.keyframe_insert(prop, frame=start + 6)
    t.scale_x = t.scale_y = s
    t.offset_x = (1 - s) * (x - 0.5) * W
    t.offset_y = (1 - s) * (0.5 - y) * H
    for prop in ("scale_x", "scale_y", "offset_x", "offset_y"):
        t.keyframe_insert(prop, frame=start + 6 + ZOOM_IN)


def fade(strip, start, length, fade_in=True, fade_out=True):
    strip.blend_alpha = 0.0 if fade_in else 1.0
    strip.keyframe_insert("blend_alpha", frame=start)
    strip.blend_alpha = 1.0
    strip.keyframe_insert("blend_alpha", frame=start + TEXT_FADE)
    if fade_out:
        strip.keyframe_insert("blend_alpha", frame=start + length - TEXT_FADE)
        strip.blend_alpha = 0.0
        strip.keyframe_insert("blend_alpha", frame=start + length)


def setup(scene):
    scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = W, H, 100
    scene.render.fps, scene.render.fps_base = FPS, 1
    scene.view_settings.view_transform = "Standard"  # the UI's own colors, no film look
    scene.view_settings.look = "None"
    scene.sequence_editor_create()


def phone(seq, name, channel, at, folder, names):
    """The panel's frames inside the phone, over the brand background, as one meta strip of the timeline.
    (Not a scene strip: rendering one re-evaluates the main timeline's keyframes at the other scene's frame, and the captions vanish.)"""
    meta = seq.strips.new_meta(name=name, channel=channel, frame_start=at)
    n = len(names)
    still(meta, name + "-fondo", os.path.join(OVERLAYS, "phone-bg.png"), 1, at, n)
    screen = meta.strips.new_image(name=name + "-pantalla", filepath=os.path.join(folder, names[0]), channel=2, frame_start=at, fit_method="ORIGINAL")
    for f in names[1:]:
        screen.elements.append(f)
    screen.blend_type = "ALPHA_OVER"
    r = SCRIPT["phoneScreen"]
    first = bpy.data.images.load(os.path.join(folder, names[0]))
    ih = first.size[1]
    bpy.data.images.remove(first)
    screen.transform.scale_x = screen.transform.scale_y = r["h"] / ih
    screen.transform.offset_x = r["x"] + r["w"] / 2 - W / 2
    screen.transform.offset_y = H / 2 - (r["y"] + r["h"] / 2)
    still(meta, name + "-marco", os.path.join(OVERLAYS, "phone.png"), 3, at, n)
    meta.frame_final_duration = n  # a new meta strip does not grow to its contents by itself
    return meta


def build():
    scene = bpy.context.scene
    setup(scene)
    seq = scene.sequence_editor
    shots = SCRIPT["shots"]
    at, tops, cues = 1, [], []
    for i, shot in enumerate(shots):
        length = round(shot["seconds"] * FPS)
        tail = FADE if i + 1 < len(shots) else 0  # each shot runs into the next one's first frames for the dissolve
        channel = 1 if i % 2 == 0 else 4
        if shot.get("card"):
            top = still(seq, shot["id"], os.path.join(OVERLAYS, shot["id"] + ".png"), channel, at, length + tail)
        else:
            folder, files, marks = frames_of(shot["capture"])
            if shot.get("still"):
                base = still(seq, shot["id"], os.path.join(folder, files[0][1]), channel, at, length + tail)
                top = seq.strips.new_effect(name=shot["id"] + "-desenfoque", type="GAUSSIAN_BLUR", channel=channel + 1,
                                            frame_start=at, frame_end=at + length + tail, input1=base)
                top.size_x = top.size_y = 14
            else:
                keys = sorted(marks.items(), key=lambda kv: kv[1])
                start = marks[shot["id"]]
                end = next((v for k, v in keys if v > start), marks["end"])
                names = resample(files, start, max(end, start + 0.1), length + tail)
                if shot.get("phone"):
                    top = phone(seq, shot["id"], channel, at, folder, names)
                else:
                    top = image_strip(seq, shot["id"], folder, names, channel, at)
                    if "zoom" in shot:
                        zoom(top, at, length, shot["zoom"])
        if tops:
            prev = tops[-1]
            seq.strips.new_effect(name="fundido-" + shot["id"], type="CROSS", channel=7, frame_start=at, frame_end=at + FADE, input1=prev, input2=top)
        tops.append(top)

        overlay = os.path.join(OVERLAYS, shot["id"] + ".png")
        if not shot.get("card"):
            caption = still(seq, "texto-" + shot["id"], overlay, 8, at, length)
            fade(caption, at, length, fade_in=i > 0)
        cues.append((at - 1, at - 1 + length, shot))
        at += length

    total = at - 1
    sample = still(seq, "datos-de-ejemplo", os.path.join(OVERLAYS, "sample.png"), 9, 1, total - round(shots[-1]["seconds"] * FPS))
    sample.blend_alpha = 1.0
    scene.frame_start, scene.frame_end = 1, total
    return scene, cues


def timestamp(frame):
    s = frame / FPS
    return f"{int(s // 3600):02d}:{int(s % 3600 // 60):02d}:{s % 60:06.3f}"


def write_vtt(cues):
    lines = ["WEBVTT", "", "NOTE TK-296: generado por design/video/montaje.py desde productVideo.json; no editar a mano.", ""]
    for start, end, shot in cues:
        lines += [shot["id"], f"{timestamp(start)} --> {timestamp(end)}", shot["description"], ""]
    with open(os.path.join(OUT, SCRIPT["name"] + ".es.vtt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def encode(scene, container, codec, path, quality):
    r = scene.render
    r.image_settings.file_format = "FFMPEG"
    r.ffmpeg.format = container
    r.ffmpeg.codec = codec
    r.ffmpeg.constant_rate_factor = quality
    r.ffmpeg.ffmpeg_preset = "BEST"
    r.ffmpeg.gopsize = 60
    r.ffmpeg.audio_codec = "NONE"
    r.ffmpeg.use_autosplit = False
    r.filepath = path
    r.use_file_extension = False
    bpy.ops.render.render(animation=True, scene=scene.name)


def poster(scene, frame, path):
    r = scene.render
    r.image_settings.file_format = "WEBP"
    r.image_settings.quality = 72
    r.image_settings.color_mode = "RGB"
    scene.frame_set(frame)
    r.filepath = path
    bpy.ops.render.render(write_still=True, scene=scene.name)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    scene, cues = build()
    write_vtt(cues)
    name = os.path.join(OUT, SCRIPT["name"])
    review = os.environ.get("ABASTALIA_VIDEO_REVIEW")  # e.g. "2,5,11": PNG stills at those seconds, to look at, and nothing else
    if review:
        for s in review.split(","):
            scene.render.image_settings.file_format = "PNG"
            scene.frame_set(round(float(s) * FPS) + 1)
            scene.render.filepath = os.path.join(OUT, f"revision-{float(s):05.1f}s.png")
            bpy.ops.render.render(write_still=True, scene=scene.name)
        sys.exit(0)
    poster(scene, round(1.5 * FPS), name + "-portada.webp")
    # Blender's quality steps (HIGH, MEDIUM, LOW, VERYLOW). H.264 at MEDIUM ≈ 2.2 MB (≤ 3 MB); VP9 comes out bigger at the same step
    # and Blender's VP9 ignores a target bitrate, so VERYLOW ≈ 2.4 MB (≤ 2.5 MB).
    if os.environ.get("ABASTALIA_VIDEO_ONLY") != "webm":
        encode(scene, "MPEG4", "H264", name + ".mp4", os.environ.get("ABASTALIA_VIDEO_MP4", "MEDIUM"))
    if os.environ.get("ABASTALIA_VIDEO_ONLY") != "mp4":
        encode(scene, "WEBM", "WEBM", name + ".webm", os.environ.get("ABASTALIA_VIDEO_WEBM", "VERYLOW"))
    for path in (name + ".mp4", name + ".webm", name + "-portada.webp"):
        if os.path.exists(path):
            print(f"{os.path.basename(path)}: {os.path.getsize(path) / 1024:.0f} KB")
