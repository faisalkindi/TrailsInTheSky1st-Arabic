"""Drive the installer's real code path without the GUI, then verify the result.

Imports installer.py and calls the same Installer.install() the button calls, so this exercises
the shipped logic rather than a reimplementation of it.
"""
import os, sys, json, struct, hashlib, importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
GAME = r"F:\SteamLibrary\steamapps\common\Trails in the Sky 1st Chapter"

spec = importlib.util.spec_from_file_location('inst', os.path.join(HERE, 'installer.py'))
m = importlib.util.module_from_spec(spec)
sys.modules['inst'] = m
spec.loader.exec_module(m)

def md5_file(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for c in iter(lambda: f.read(1 << 20), b''):
            h.update(c)
    return h.hexdigest()

mode = sys.argv[1] if len(sys.argv) > 1 else 'install'
i = m.Installer(GAME, lambda s: print('   ' + s), lambda v: None)
print('status before: %s' % i.status())
getattr(i, mode)()
print('status after : %s' % i.status())

print('\nverifying game files against the manifest:')
man = m.MANIFEST
pacdir = os.path.join(GAME, 'pac', 'steam')
ok = bad = 0
for p in man['full_pacs']:
    got = md5_file(os.path.join(pacdir, p['name']))
    good = got == p['md5']
    print('  %-24s %s' % (p['name'], 'OK' if good else 'MISMATCH (%s)' % got))
    ok += good; bad += (not good)

idx = m.pac_index(os.path.join(pacdir, 'image.pac'))
imgok = imgbad = 0
for e in man['image_entries']:
    loc, sz = idx[e['name']]
    h = m.md5_at(os.path.join(pacdir, 'image.pac'), loc, sz)
    want = e['md5_mod'] if mode == 'install' else e['md5_vanilla']
    if h == want:
        imgok += 1
    else:
        imgbad += 1
        print('  image entry MISMATCH: %s' % e['name'])
print('  image.pac entries: %d/%d as expected' % (imgok, imgok + imgbad))
print('\nRESULT: %d pacs ok, %d bad; %d image entries ok, %d bad' % (ok, bad, imgok, imgbad))
sys.exit(1 if (bad or imgbad) else 0)
