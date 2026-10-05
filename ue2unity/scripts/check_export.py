"""Repeatable binary/schema regression checks for exporter candidates."""
import argparse
import collections
import json
import math
import struct
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('root', type=Path)
parser.add_argument('--out', type=Path)
parser.add_argument('--min-models', type=int, default=0)
args = parser.parse_args()
rows = []
for path in sorted(args.root.rglob('*.gltf')):
    d = json.loads(path.read_text(encoding='utf-8-sig'))
    issues = collections.Counter()
    issues['scene_wrong_type'] += not isinstance(d.get('scene'), int)
    for im in d.get('images', []):
        issues['image_wrong_type'] += not isinstance(im, dict)
        uri = im if isinstance(im, str) else im.get('uri', '')
        issues['missing_image'] += not (path.parent / uri).is_file()
    buffers = [(path.parent / b['uri']).read_bytes() for b in d['buffers']]
    ac = d['accessors']
    views = d['bufferViews']
    def values(i):
        a = ac[i]
        v = views[a['bufferView']]
        fmt = '<' + {5125: 'I', 5126: 'f'}[a['componentType']] * {
            'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}[a['type']]
        return [struct.unpack_from(fmt, buffers[v['buffer']],
                v.get('byteOffset', 0) + a.get('byteOffset', 0) +
                j * v.get('byteStride', struct.calcsize(fmt)))
                for j in range(a['count'])]
    primitives = d['meshes'][0]['primitives']
    seen = set()
    used = set()
    counts = []
    orientation = collections.Counter()
    for pr in primitives:
        used.add(pr.get('material'))
        pos_count = ac[pr['attributes']['POSITION']]['count']
        issues['attribute_count_mismatch'] += any(ac[i]['count'] != pos_count
                                                 for i in pr['attributes'].values())
        ids = [v[0] for v in values(pr['indices'])]
        points = values(pr['attributes']['POSITION'])
        normals = values(pr['attributes']['NORMAL'])
        counts.append(len(ids) // 3)
        issues['index_out_of_range'] += bool(ids and max(ids) >= pos_count)
        issues['nontriangles'] += len(ids) % 3 != 0
        for i in range(0, len(ids) - 2, 3):
            tri = tuple(ids[i:i + 3])
            issues['repeated_triangle_across_primitives'] += tri in seen
            seen.add(tri)
            a, b, c = [points[j] for j in tri]
            u = [b[j]-a[j] for j in range(3)]
            v = [c[j]-a[j] for j in range(3)]
            cross = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
            area = math.sqrt(sum(x*x for x in cross))
            n = [sum(normals[k][j] for k in tri) for j in range(3)]
            dot = sum(cross[j]*n[j] for j in range(3))
            if area < 1e-8:
                orientation['degenerate'] += 1
            elif dot < -area*1e-6:
                orientation['opposed'] += 1
                if all(sum(cross[j]*normals[k][j] for j in range(3)) < -area*1e-6
                       for k in tri):
                    orientation['all_corners_opposed'] += 1
                else:
                    orientation['mixed_corner_directions'] += 1
            elif dot > area*1e-6:
                orientation['aligned'] += 1
            else:
                orientation['orthogonal'] += 1
    if primitives:
        for n in values(primitives[0]['attributes']['NORMAL']):
            length = math.sqrt(sum(x*x for x in n))
            issues['nonunit_normal'] += not math.isfinite(length) or abs(length-1) > .01
        for p in values(primitives[0]['attributes']['POSITION']):
            issues['nonfinite_position'] += not all(math.isfinite(x) for x in p)
    issues['all_corner_normals_opposed'] += orientation.get('all_corners_opposed', 0)
    rows.append({'path': str(path.relative_to(args.root)),
                 'issues': {k: v for k, v in issues.items() if v},
                 'triangles_per_primitive': counts,
                 'orientation': dict(orientation),
                 'unused_materials': len(d.get('materials', [])) - len(used),
                 'alpha_modes': [m.get('alphaMode', 'OPAQUE') for m in d.get('materials', [])]})
    if d.get('extras', {}).get('sourceTriangleCount') is not None:
        if sum(counts) != d['extras']['sourceTriangleCount']:
            rows[-1]['issues']['source_triangle_count_mismatch'] = 1
summary = {'root': str(args.root), 'models': len(rows),
           'files_per_issue': dict(collections.Counter(k for r in rows for k in r['issues'])),
           'issue_occurrences': dict(sum((collections.Counter(r['issues']) for r in rows),
                                         collections.Counter())),
           'unused_materials': sum(r['unused_materials'] for r in rows),
           'orientation': dict(sum((collections.Counter(r['orientation']) for r in rows),
                                    collections.Counter())),
           'alpha_modes': dict(collections.Counter(a for r in rows for a in r['alpha_modes']))}
print(json.dumps(summary, indent=2))
if args.out:
    args.out.write_text(json.dumps({'summary': summary, 'models': rows}, indent=2), encoding='utf-8')
raise SystemExit(bool(summary['files_per_issue']) or len(rows) < args.min_models)
