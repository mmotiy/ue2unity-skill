"""Assemble glTF assets with explicit texture semantics and stable sidecars."""

import argparse
import collections
import json
from pathlib import Path
import re
import shutil
import uuid


def texture_roles(export):
    """Infer image roles from actual glTF bindings before filename fallback."""
    roles = collections.defaultdict(set)
    for path in (export / 'Meshes').rglob('*.gltf'):
        document = json.loads(path.read_text(encoding='utf-8'))

        def record(reference, role):
            texture = document['textures'][reference['index']]
            uri = document['images'][texture['source']]['uri']
            source = (path.parent / uri).resolve()
            if not source.is_relative_to(export / 'Textures') or not source.is_file():
                raise ValueError(f'Invalid image dependency: {path}: {uri}')
            roles[source].add(role)

        for material in document.get('materials', []):
            for key, role in [('normalTexture', 'NormalXYZ'),
                              ('emissiveTexture', 'Color'),
                              ('occlusionTexture', 'LinearData')]:
                if key in material:
                    record(material[key], role)
            pbr = material.get('pbrMetallicRoughness', {})
            for key, role in [('baseColorTexture', 'Color'),
                              ('metallicRoughnessTexture', 'LinearData')]:
                if key in pbr:
                    record(pbr[key], role)
    for path, values in roles.items():
        if len(values) != 1:
            raise ValueError(f'Conflicting texture semantics; create distinct aliases: {path}: {values}')
    return {path: next(iter(values)) for path, values in roles.items()}


def fallback_role(path, old_meta):
    """Preserve explicit nonstandard metadata; use names only as a fallback."""
    match = re.search(r'^  userData: CityPacks(Color|NormalXYZ|LinearData)\r?$',
                      old_meta, re.MULTILINE)
    if match:
        return match.group(1)
    name = path.stem.lower()
    if name.endswith(('_n', '_normal', '_normals')):
        return 'NormalXYZ'
    if (name.endswith(('_m', '_m2', '_mr', '_ao', '_occlusion', '_colormask'))
            or '_mask' in name or '_orm' in name):
        return 'LinearData'
    return 'Color'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('export', type=Path)
    parser.add_argument('out', type=Path)
    parser.add_argument('--previous-assets', type=Path)
    parser.add_argument('--pack-name', default='CityPacks')
    args = parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9_-]+', args.pack_name):
        raise ValueError('pack-name must be one safe directory name')
    export = args.export.resolve()
    output = args.out.resolve()
    if output == export or output.is_relative_to(export) or export.is_relative_to(output):
        raise ValueError('Export and output directories must be separate')
    if output.exists() and any(output.iterdir()):
        raise ValueError('Assemble into an empty directory; preserve existing delivery')
    roles = texture_roles(export)
    skill = Path(__file__).resolve().parent.parent
    assets = output / 'Assets' / args.pack_name
    counts = collections.Counter()
    files = []
    for category, extensions in [('Meshes', {'.gltf', '.bin'}), ('Textures', {'.png'})]:
        for source in sorted((export / category).rglob('*')):
            if source.is_file() and source.suffix in extensions:
                target = assets / source.relative_to(export)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
                files.append((source, target))
                counts[source.suffix] += 1
    editor = assets / 'Editor'
    editor.mkdir(parents=True, exist_ok=True)
    postprocessor = editor / 'TextureImportPostprocessor.cs'
    postprocessor.write_text((skill / 'unity/TextureImportPostprocessor.cs').read_text(encoding='utf-8').replace(
        'CityPacks/Textures/', args.pack_name + '/Textures/'), encoding='utf-8')
    readme = assets / 'README_Unity6.md'
    readme.write_text((skill / 'unity/README_Unity6.template.md').read_text(encoding='utf-8').replace(
        'CityPacks', args.pack_name), encoding='utf-8')
    files.extend([(None, postprocessor), (None, readme)])
    templates = {role: (skill / 'unity/TextureMetaTemplates' / (kind + '.png.meta.template')).read_text(encoding='utf-8')
                 for role, kind in [('Color', 'Color'), ('NormalXYZ', 'Normal'),
                                    ('LinearData', 'LinearData')]}
    preserved = 0
    role_counts = collections.Counter()
    for source, target in files:
        old_meta = ''
        if args.previous_assets:
            previous = args.previous_assets / target.relative_to(assets)
            sidecar = previous.with_name(previous.name + '.meta')
            if sidecar.is_file():
                old_meta = sidecar.read_text(encoding='utf-8-sig')
        elif source:
            sidecar = source.with_name(source.name + '.meta')
            if sidecar.is_file():
                old_meta = sidecar.read_text(encoding='utf-8-sig')
        match = re.search(r'^guid: ([0-9a-f]{32})\r?$', old_meta, re.MULTILINE)
        if old_meta and not match:
            raise ValueError(f'Invalid previous GUID metadata: {target}')
        guid = match.group(1) if match else uuid.uuid5(
            uuid.NAMESPACE_URL, target.relative_to(output).as_posix()).hex
        preserved += bool(match)
        if target.suffix == '.png':
            role = roles.get(source, fallback_role(source, old_meta))
            meta = re.sub(r'^guid: [0-9a-f]{32}$', 'guid: ' + guid,
                          templates[role], flags=re.MULTILINE)
            meta = re.sub(r'^  textureType: 1$', '  textureType: 0', meta, flags=re.MULTILINE)
            meta = re.sub(r'^  userData:.*$', '  userData: CityPacks' + role, meta,
                          flags=re.MULTILINE)
            meta = re.sub(r'maxTextureSize: (8192|2048)', 'maxTextureSize: 4096', meta)
            role_counts[role] += 1
        else:
            meta = f'fileFormatVersion: 2\nguid: {guid}\n'
        target.with_name(target.name + '.meta').write_text(meta, encoding='utf-8')
    report = output / 'Report'
    report.mkdir(exist_ok=True)
    for name in ['materials.json', 'export_errors.log']:
        if (export / name).is_file():
            shutil.copy2(export / name, report / name)
    result = {'counts': dict(counts), 'texture_roles': dict(role_counts),
              'preserved_guids': preserved, 'export': str(export), 'assets': str(assets)}
    (report / 'Assembly.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
