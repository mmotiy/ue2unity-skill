"""Regress Unity texture semantics, stable GUIDs and importable gzip headers."""

import gzip
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

from PIL import Image


SCRIPTS = Path(__file__).resolve().parent


def read_package(path):
    groups = {}
    with gzip.open(path, 'rb') as decoded:
        with tarfile.open(fileobj=decoded, mode='r|') as archive:
            for member in archive:
                if member.isfile():
                    guid, kind = member.name.split('/')
                    groups.setdefault(guid, {})[kind] = archive.extractfile(member).read()
        while decoded.read(4096):
            pass
    return {item['pathname'].decode(): (guid, item['asset'], item['asset.meta'])
            for guid, item in groups.items()}


def run(script, *args, check=True):
    result = subprocess.run([sys.executable, str(SCRIPTS / script)] + list(map(str, args)),
                            capture_output=True, text=True, check=False)
    if check and result.returncode:
        raise AssertionError(result.stderr)
    return result


class DeliveryTests(unittest.TestCase):

    def test_texture_semantics_override_filename_and_preserve_guid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            export = root / 'Export'
            mesh = export / 'Meshes/Test/sample.gltf'
            mesh.parent.mkdir(parents=True)
            textures = export / 'Textures'
            textures.mkdir()
            names = ['road_MASK_BASECOLOR.png', 'normal_Nl.png', 'unusual_data.png']
            for name in names:
                Image.new('RGBA', (2, 2), (128, 128, 255, 127)).save(textures / name)
            mesh.write_text(json.dumps({
                'asset': {'version': '2.0'},
                'images': [{'uri': '../../Textures/' + name} for name in names],
                'textures': [{'source': index} for index in range(3)],
                'materials': [{'normalTexture': {'index': 1},
                               'pbrMetallicRoughness': {
                                   'baseColorTexture': {'index': 0},
                                   'metallicRoughnessTexture': {'index': 2}}}]}))
            previous = root / 'Previous'
            old_meta = previous / 'Textures' / (names[0] + '.meta')
            old_meta.parent.mkdir(parents=True)
            old_guid = 'a123456789abcdef0123456789abcdef'
            old_meta.write_text('fileFormatVersion: 2\nguid: ' + old_guid + '\n')
            output = root / 'Delivery'
            run('assemble_unity.py', export, output, '--previous-assets', previous)
            assets = output / 'Assets/CityPacks'
            for name, role, srgb in zip(names, ['Color', 'NormalXYZ', 'LinearData'], [1, 0, 0]):
                meta = (assets / 'Textures' / (name + '.meta')).read_text()
                self.assertIn('userData: CityPacks' + role, meta)
                self.assertIn('sRGBTexture: ' + str(srgb), meta)
                self.assertIn('textureType: 0', meta)
                self.assertIn('textureShape: 1', meta)
            self.assertIn('guid: ' + old_guid,
                          (assets / 'Textures' / (names[0] + '.meta')).read_text())
            package = root / 'data.unitypackage'
            run('pack_unitypackage.py', assets, package)
            entries = read_package(package)
            key = 'Assets/CityPacks/Textures/' + names[0]
            self.assertEqual(entries[key][0], old_guid)
            self.assertEqual(entries[key][1], (textures / names[0]).read_bytes())
            self.assertEqual(entries[key][2], (assets / 'Textures' / (names[0] + '.meta')).read_bytes())

    def test_pack_has_no_fname_and_retains_sidecar_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            src = root / 'Source'
            src.mkdir()
            asset = src / 'probe.txt'
            asset.write_bytes(b'Native ImportPackage regression')
            meta = src / 'probe.txt.meta'
            meta.write_bytes(b'fileFormatVersion: 2\r\nguid: a123456789abcdef0123456789abcdef\r\n')
            packages = [root / 'one.unitypackage', root / 'two.unitypackage']
            for package in packages:
                run('pack_unitypackage.py', src, package)
                self.assertFalse(package.read_bytes()[3] & 8, 'Unity rejects gzip FNAME=*.unitypackage')
            first, second = map(read_package, packages)
            self.assertEqual(first, second)
            self.assertEqual(set(first), {'Assets/CityPacks/probe.txt'})
            row = first['Assets/CityPacks/probe.txt']
            self.assertEqual(row[1], asset.read_bytes())
            self.assertEqual(row[2], meta.read_bytes())

    def test_existing_delivery_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / 'Existing'
            output.mkdir()
            marker = output / 'keep.txt'
            marker.write_bytes(b'keep')
            result = run('assemble_unity.py', root / 'Export', output, check=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(marker.read_bytes(), b'keep')

    def test_conflicting_color_and_data_roles_fail_before_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            export = root / 'Export'
            (export / 'Meshes').mkdir(parents=True)
            (export / 'Textures').mkdir()
            Image.new('RGB', (1, 1)).save(export / 'Textures/shared.png')
            (export / 'Meshes/sample.gltf').write_text(json.dumps({
                'images': [{'uri': '../Textures/shared.png'}], 'textures': [{'source': 0}],
                'materials': [{'pbrMetallicRoughness': {
                    'baseColorTexture': {'index': 0}, 'metallicRoughnessTexture': {'index': 0}}}]}))
            output = root / 'Output'
            result = run('assemble_unity.py', export, output, check=False)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('Conflicting texture semantics', result.stderr)
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
