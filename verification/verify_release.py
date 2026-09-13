"""Inspect a published Windows PE without executing it. Python 3 standard library only."""
from pathlib import Path
import argparse
import collections
import hashlib
import json
import re
import struct
import xml.etree.ElementTree as ET


def verify(exe: Path, root: Path, version: str) -> dict:
    data = exe.read_bytes()
    u16 = lambda at: struct.unpack_from('<H', data, at)[0]
    u32 = lambda at: struct.unpack_from('<I', data, at)[0]
    assert data[:2] == b'MZ', 'DOS signature'
    pe = u32(0x3C)
    assert data[pe:pe + 4] == b'PE\0\0', 'PE signature'
    coff = pe + 4
    machine, section_count = u16(coff), u16(coff + 2)
    opt = coff + 20
    assert machine == 0x8664 and u16(opt) == 0x20B, 'AMD64 PE32+'
    subsystem = u16(opt + 68)
    assert subsystem == 2, 'Windows GUI subsystem'
    sections = []
    for i in range(section_count):
        at = opt + u16(coff + 16) + 40 * i
        sections.append((u32(at + 12), u32(at + 8), u32(at + 20), u32(at + 16)))

    def offset(rva):
        for address, virtual_size, raw_at, raw_size in sections:
            if address <= rva < address + max(virtual_size, raw_size):
                return raw_at + rva - address
        raise AssertionError(f'Unmapped RVA {rva:x}')

    resource_at = offset(u32(opt + 112 + 2 * 8))
    resources = []

    def walk(relative=0, path=()):
        at = resource_at + relative
        for i in range(u16(at + 12) + u16(at + 14)):
            entry = at + 16 + 8 * i
            name, value = u32(entry), u32(entry + 4)
            if name & 0x80000000:
                name_at = resource_at + (name & 0x7FFFFFFF)
                length = u16(name_at)
                name = data[name_at + 2:name_at + 2 + length * 2].decode('utf-16le')
            next_path = path + (name,)
            if value & 0x80000000:
                walk(value & 0x7FFFFFFF, next_path)
            else:
                leaf = resource_at + value
                start, size = offset(u32(leaf)), u32(leaf + 4)
                resources.append((next_path, data[start:start + size]))

    walk()
    types = sorted(set(path[0] for path, _ in resources))
    assert {3, 14, 16, 24}.issubset(types), 'Icon, group, version and manifest resources'
    versions = []
    for path, blob in resources:
        if path[0] == 16:
            at = blob.index(struct.pack('<I', 0xFEEF04BD))
            ms, ls = struct.unpack_from('<II', blob, at + 8)
            versions.append(f'{ms >> 16}.{ms & 65535}.{ls >> 16}.{ls & 65535}')
    assert versions and all(v == version + '.0' for v in versions), versions
    manifests = [ET.fromstring(blob) for path, blob in resources if path[0] == 24]
    manifest_versions = [m.find('{urn:schemas-microsoft-com:asm.v1}assemblyIdentity').get('version') for m in manifests]
    assert manifest_versions and all(v == version + '.0' for v in manifest_versions), manifest_versions
    ico = (root / 'src/Desktop/Assets/AppIcon.ico').read_bytes()
    count = struct.unpack_from('<H', ico, 4)[0]
    source_hashes = []
    for i in range(count):
        size, start = struct.unpack_from('<II', ico, 6 + i * 16 + 8)
        source_hashes.append(hashlib.sha256(ico[start:start + size]).hexdigest())
    actual_hashes = [hashlib.sha256(blob).hexdigest() for path, blob in resources if path[0] == 3]
    assert collections.Counter(source_hashes) == collections.Counter(actual_hashes), 'Source icon payloads'
    event_names = {'Click', 'SelectionChanged', 'TextChanged', 'MouseDoubleClick', 'Loaded', 'Closing', 'Closed', 'Checked', 'Unchecked', 'ValueChanged', 'Handler'}
    code = '\n'.join(p.read_text() for p in (root / 'src/Desktop').rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts)
    refs = []
    for xaml in (root / 'src/Desktop').rglob('*.xaml'):
        if 'obj' in xaml.parts or 'bin' in xaml.parts:
            continue
        for node in ET.parse(xaml).iter():
            for name, handler in node.attrib.items():
                if name.rsplit('}', 1)[-1] in event_names:
                    assert re.search(r'\b' + re.escape(handler) + r'\s*\(', code), (xaml, handler)
                    refs.append(handler)
    tests = (root / 'verification/tests.log').read_text()
    match = re.search(r'RESULT: (\d+) passed; (\d+) failed\.', tests)
    assert match and match.group(2) == '0', 'Passing test log'
    assert len(re.findall(r'^PASS ', tests, re.M)) == int(match.group(1)), 'Test log count'
    sdk = json.loads((root / 'global.json').read_text())['sdk']['version']
    runtime = json.loads((root / 'src/Desktop/bin/Release/net10.0-windows/win-x64/RealtimeTranscription.runtimeconfig.json').read_text())['runtimeOptions']['includedFrameworks']
    return dict(version=version, file=exe.name, size_bytes=len(data), sha256=hashlib.sha256(data).hexdigest(),
                machine=f'0x{machine:04x}', subsystem=subsystem, file_version=versions[0], manifest_version=manifest_versions[0],
                resource_types=types, icon_sizes=count, icon_payloads_match_source=True,
                unsigned=u32(opt + 112 + 4 * 8) == 0 and u32(opt + 112 + 4 * 8 + 4) == 0,
                xaml_event_references=len(refs), tests_passed=int(match.group(1)), tests_failed=0,
                sdk=sdk, runtime=next(f['version'] for f in runtime if f['name'] == 'Microsoft.NETCore.App'),
                native_windows_tested=False, real_cloud_tested=False)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('exe', type=Path)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--version', default='1.3.0')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = json.dumps(verify(args.exe, args.root, args.version), ensure_ascii=False, indent=2) + '\n'
    if args.output:
        args.output.write_text(result)
    print(result, end='')
