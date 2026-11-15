# Second Step – wheelchair and crutches built from primitives, rendered in RimWorld style
import bpy, math, sys, os
from mathutils import Vector
OUT = sys.argv[sys.argv.index('--')+1] if '--' in sys.argv else '/tmp/ssr'
os.makedirs(OUT, exist_ok=True)

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)

def mat(name, c):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (c[0], c[1], c[2], 1)
    return m

TAG = set()
def put(o, m, col):
    o['tags'] = ','.join(sorted(TAG))
    o.data.materials.append(m)
    for c in o.users_collection: c.objects.unlink(o)
    col.objects.link(o)
    return o

def cyl(col, m, a, b, r, v=16):
    a, b = Vector(a), Vector(b)
    d = b - a
    bpy.ops.mesh.primitive_cylinder_add(vertices=v, radius=r, depth=d.length, location=(a+b)/2)
    o = bpy.context.object
    o.rotation_mode = 'QUATERNION'
    o.rotation_quaternion = d.to_track_quat('Z', 'Y')
    return put(o, m, col)

def box(col, m, loc, size, rot=(0,0,0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc, rotation=rot)
    o = bpy.context.object
    o.scale = size
    return put(o, m, col)

def torus(col, m, loc, R, r, axis='X'):
    rot = (0, math.pi/2, 0) if axis == 'X' else (math.pi/2, 0, 0)
    bpy.ops.mesh.primitive_torus_add(major_radius=R, minor_radius=r, location=loc, rotation=rot, major_segments=48, minor_segments=10)
    return put(bpy.context.object, m, col)

def coll(name):
    c = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(c)
    return c

FRAME = (0.86, 0.86, 0.86); TIRE = (0.13, 0.13, 0.14); SEAT = (0.55, 0.55, 0.57); PAD = (0.25, 0.25, 0.26)

def wheelchair(col):
    global TAG
    fr, ti, se = mat('frame', FRAME), mat('tire', TIRE), mat('seat', SEAT)
    R = 0.28
    for s in (-1, 1):
        x = s*0.40
        near = {'nearE'} if s < 0 else set()
        TAG = set(near)
        torus(col, ti, (x, -0.02, R+0.02), R, 0.03)
        torus(col, fr, (x+s*0.045, -0.02, R+0.02), R-0.04, 0.012)          # push rim
        cyl(col, fr, (x-0.03, -0.02, R+0.02), (x+0.03, -0.02, R+0.02), 0.04)  # hub
        for k in range(6):
            a = k*math.pi/6
            cyl(col, fr, (x, -0.02+math.cos(a)*(R-0.02), R+0.02+math.sin(a)*(R-0.02)),
                (x, -0.02-math.cos(a)*(R-0.02), R+0.02-math.sin(a)*(R-0.02)), 0.006, 6)
        TAG = set()
        cyl(col, fr, (s*0.32, -0.20, 0.28), (s*0.32, -0.22, 0.80), 0.022)
        cyl(col, fr, (s*0.32, -0.22, 0.80), (s*0.32, -0.32, 0.82), 0.024)
        cyl(col, fr, (s*0.32, -0.20, 0.42), (s*0.32, 0.22, 0.42), 0.02)
        TAG = {'frontS'} | near
        cyl(col, fr, (s*0.32, 0.22, 0.42), (s*0.26, 0.36, 0.10), 0.02)
        TAG = set()
        cyl(col, fr, (s*0.32, -0.20, 0.28), (s*0.30, 0.24, 0.14), 0.016)
        TAG = {'frontS'} | near
        box(col, mat('pad', PAD), (s*0.33, 0.0, 0.58), (0.06, 0.34, 0.035))   # armrest
        cyl(col, fr, (s*0.33, 0.12, 0.42), (s*0.33, 0.12, 0.57), 0.016)
        TAG = set(near)
        cyl(col, fr, (s*0.33, -0.15, 0.42), (s*0.33, -0.15, 0.57), 0.016)
        TAG = {'frontS'} | near
        cyl(col, fr, (s*0.30, 0.26, 0.14), (s*0.30, 0.28, 0.09), 0.014)
        torus(col, ti, (s*0.30, 0.30, 0.065), 0.05, 0.018)
        box(col, se, (s*0.14, 0.40, 0.09), (0.20, 0.12, 0.02))                 # foot plate
        TAG = set()
    box(col, se, (0, 0.01, 0.44), (0.62, 0.42, 0.05))                          # seat
    box(col, se, (0, -0.215, 0.62), (0.62, 0.035, 0.36))                       # backrest
    TAG = set()

def crutch(col, loc=(0,0,0), tilt=0.0, yaw=0.0, name='c', size=1.0):
    fr, pd = mat('frame', FRAME), mat('pad', PAD)
    parts = []
    # two rods from grip region up to the pad, one rod down to the tip
    H = 0.95
    parts.append(cyl(col, fr, (-0.035, 0, 0.42), (-0.035, 0, H-0.03), 0.014))
    parts.append(cyl(col, fr, (0.035, 0, 0.42), (0.035, 0, H-0.03), 0.014))
    parts.append(cyl(col, fr, (-0.035, 0, 0.42), (0, 0, 0.30), 0.014))
    parts.append(cyl(col, fr, (0.035, 0, 0.42), (0, 0, 0.30), 0.014))
    parts.append(cyl(col, fr, (0, 0, 0.30), (0, 0, 0.02), 0.016))
    parts.append(cyl(col, pd, (0, 0, 0.04), (0, 0, -0.01), 0.024))            # rubber tip
    parts.append(box(col, pd, (0, 0, H), (0.13, 0.05, 0.04)))                  # armpit pad
    parts.append(cyl(col, pd, (-0.04, 0, 0.62), (0.04, 0, 0.62), 0.018))      # hand grip
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts: p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    o = bpy.context.object
    bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
    o.rotation_mode = 'XYZ'
    o.scale = (size, size, size)
    o.rotation_euler = (0, tilt, yaw)
    o.location = loc
    o.name = name
    return o

def scene_setup(res=512):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.render.resolution_x = sc.render.resolution_y = res
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = 'PNG'
    sc.render.image_settings.color_mode = 'RGBA'
    sh = sc.display.shading
    sh.light = 'STUDIO'
    sh.color_type = 'MATERIAL'
    sh.show_cavity = True
    sh.cavity_type = 'WORLD'
    sh.show_object_outline = True
    sh.object_outline_color = (0.08, 0.07, 0.06)
    sh.show_shadows = False
    sh.show_specular_highlight = False
    sc.view_settings.view_transform = 'Standard'
    cam = bpy.data.cameras.new('cam'); cam.type = 'ORTHO'
    co = bpy.data.objects.new('cam', cam); sc.collection.objects.link(co); sc.camera = co
    return co

def shoot(co, scale, pos, target, path):
    co.data.ortho_scale = scale
    co.location = pos
    d = Vector(target) - Vector(pos)
    co.rotation_mode = 'QUATERNION'
    co.rotation_quaternion = d.to_track_quat('-Z', 'Y')
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

def only(col):
    for c in bpy.context.scene.collection.children:
        c.hide_render = (c != col)

reset()
cam = scene_setup()
W = coll('wheelchair'); wheelchair(W)
for o in W.objects:
    o.location = o.location * 0.88
    o.scale = o.scale * 0.88
CS = coll('crutches_front')
crutch(CS, (-0.25, 0.04, 0), tilt=-0.07, size=0.6); crutch(CS, (0.25, 0.04, 0), tilt=0.07, size=0.6)
CE = coll('crutch_side'); crutch(CE, (0, 0.10, 0), tilt=0.0, yaw=math.pi/2, size=0.6).rotation_euler = (-0.12, 0, math.pi/2)
CL = coll('crutch_lean')
crutch(CL, (0.34, 0.10, 0), tilt=0.22, size=0.6)
CLE = coll('crutch_lean_side'); crutch(CLE, (0, -0.22, 0), size=0.6).rotation_euler = (0.25, 0, math.pi/2)
CI = coll('crutches_item')
a = crutch(CI, (-0.1, 0, 0.05), name='i1'); a.rotation_euler = (math.pi/2, 0, 0.75); a.location = (-0.12, 0.0, 0.05)
b = crutch(CI, (0.12, 0, 0.05), name='i2'); b.rotation_euler = (math.pi/2, 0, 0.45); b.location = (0.14, -0.05, 0.05)

# worn views: 1.5 units across = the pawn body texture; floor near the bottom edge
S = 1.5; zc = 0.52; el = 0.18
views = {
    'south': ((0, 10, zc + 10*el), (0, 0, zc)),
    'north': ((0, -10, zc + 10*el), (0, 0, zc)),
    'east':  ((10, 0, zc + 10*el), (0, 0, zc)),
}
def only_tag(col, tag):
    only(col)
    for o in col.objects:
        o.hide_render = tag not in o.get('tags', '').split(',')
def all_tags(col):
    for o in col.objects: o.hide_render = False
for name, (p, t) in views.items():
    all_tags(W); only(W); shoot(cam, S, p, t, f'{OUT}/Wheelchair_{name}.png')
    if name != 'north':
        only_tag(W, 'frontS' if name == 'south' else 'nearE'); shoot(cam, S, p, t, f'{OUT}/WheelchairFront_{name}.png')
    all_tags(W)
    only(CLE if name == 'east' else CL); shoot(cam, S, p, t, f'{OUT}/CrutchesLean_{name}.png')
    only(CE if name == 'east' else CS); shoot(cam, S, p, t, f'{OUT}/Crutches_{name}.png')
only(W)
# item icons: from above, a bit from the front
only(W); shoot(cam, 1.5, (6, 7, 8), (0, 0, 0.4), f'{OUT}/Wheelchair.png')
bpy.context.view_layer.update()
pts = [o.matrix_world @ Vector(c) for o in CI.objects for c in o.bound_box]
mid = sum(pts, Vector()) / len(pts)
only(CI); shoot(cam, 1.2, (mid.x, mid.y+0.01, 10), (mid.x, mid.y, 0), f'{OUT}/Crutches.png')
print('rendered')
