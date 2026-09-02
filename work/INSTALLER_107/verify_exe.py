"""Verify the payload embedded in the built installer against its own manifest."""
import hashlib, json, os
from PyInstaller.archive.readers import CArchiveReader

EXE = os.path.join('dist', 'Trails in the Sky 1st - Arabic Installer v1.1.0.exe')
SEP = os.sep

r = CArchiveReader(EXE)
man = json.loads(r.extract(SEP.join(['data', 'manifest.json'])).decode('utf-8'))
print('embedded manifest: game %s, mod %s' % (man['game_version'], man['mod_version']))

ok = bad = 0
for p in man['full_pacs']:
    b = r.extract(SEP.join(['data', 'pacs', p['name']]))
    good = len(b) == p['size'] and hashlib.md5(b).hexdigest() == p['md5']
    print('  %-24s %11d B  %s' % (p['name'], len(b), 'OK' if good else 'MISMATCH'))
    ok += good; bad += (not good)

for e in man['image_entries']:
    b = r.extract(SEP.join(['data', 'image_entries', e['file']]))
    good = len(b) == e['size'] and hashlib.md5(b).hexdigest() == e['md5_mod']
    ok += good; bad += (not good)
print('  %-24s %d entries' % ('image_entries', len(man['image_entries'])))

v = r.extract(SEP.join(['data', 'vanilla_entries', '19.bin']))
want = [e for e in man['image_entries'] if e['file'] == '19.bin'][0]['md5_vanilla']
vgood = hashlib.md5(v).hexdigest() == want
print('  %-24s %11d B  %s' % ('vanilla fallback 19.bin', len(v), 'OK' if vgood else 'MISMATCH'))
ok += vgood; bad += (not vgood)

print('embedded payload verified: %d ok, %d mismatched' % (ok, bad))
