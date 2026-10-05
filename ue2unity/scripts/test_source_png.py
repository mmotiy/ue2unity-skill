"""Exercise source-PNG boundaries using synthetic images, without market assets."""

import argparse
import io
from pathlib import Path
import subprocess
import tempfile

from PIL import Image


def run(binary, arguments, expected=0):
    result = subprocess.run(['dotnet', str(binary)] + arguments, check=False,
                            capture_output=True, text=True)
    if result.returncode != expected:
        raise AssertionError(f'Unexpected exit {result.returncode}: {result.stderr}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('binary', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        image = Image.new('RGBA', (4, 1))
        pixels = [(10, 20, 30, 0), (40, 50, 60, 1), (70, 80, 90, 127),
                  (100, 110, 120, 255)]
        image.putdata(pixels)
        source = root / 'source.png'
        image.save(source)
        original = source.read_bytes()
        for index, fmt in enumerate(['TSF_BGRA8', 'TSF_RGBA8', 'TSF_G8', 'TSF_RGBA16', '']):
            output = root / f'format{index}.png'
            run(args.binary, ['--source-png-test', str(source), fmt, str(output)])
            expected = [(b, g, r, a) for r, g, b, a in pixels] if fmt == 'TSF_BGRA8' else pixels
            with Image.open(output) as decoded:
                assert decoded.convert('RGBA').tobytes() == bytes(
                    channel for pixel in expected for channel in pixel), fmt
            if fmt != 'TSF_BGRA8':
                assert output.read_bytes() == original, fmt
        small = io.BytesIO()
        Image.new('RGBA', (1, 1), (1, 2, 3, 255)).save(small, format='PNG')
        cases = [('thumbnail_then_source', small.getvalue() + b'pad' + original, 4, 1, 0),
                 ('wrong_dimensions', small.getvalue(), 4, 1, 2),
                 ('truncated', original[:-8], 4, 1, 2),
                 ('oversized_chunk', b'\x89PNG\r\n\x1a\n' + b'\x7f\xff\xff\xffIHDR', 4, 1, 2)]
        for name, payload, width, height, expected_exit in cases:
            raw = root / (name + '.bin')
            raw.write_bytes(payload)
            selected = root / (name + '.png')
            run(args.binary, ['--source-png-select', str(raw), str(width), str(height),
                              str(selected)], expected_exit)
            if expected_exit == 0:
                assert selected.read_bytes() == original
        # The selector checks dimensions/chunk bounds, not CRC. Delivery must
        # separately verify PNG integrity rather than overclaim this boundary.
        corrupt = root / 'invalid_crc.png'
        corrupt.write_bytes(original[:30] + bytes([original[30] ^ 1]) + original[31:])
        try:
            with Image.open(corrupt) as decoded:
                decoded.verify()
        except (OSError, SyntaxError):
            pass
        else:
            raise AssertionError('Full PNG verification accepted invalid CRC')
    print('PASS: five source formats, four selection gates, independent CRC check')


if __name__ == '__main__':
    main()
