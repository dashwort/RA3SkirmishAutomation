"""Build an isolated Epic War menu-overlay candidate; never edits an installed game."""
from pathlib import Path
import hashlib
import struct

OLD_HASH = '971C45ACC2794B06D95CA2BA64488FAE49B8E95E52C8BA6C0EEB800EAAD41615'
STOCK_APT_HASH = '0652914AF2C60FE8B1950C2C74F38C8548F1A19988EF746964328DF25A32EBA6'
OLD_ACTION = bytes.fromhex('5A5AB901B26559175917')
STOCK_ACTION = bytes.fromhex('8300E04E0000FC4E0000')

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def build(old_path, patched_apt_path, output_path):
    raw = Path(old_path).read_bytes()
    apt = Path(patched_apt_path).read_bytes()
    if sha(raw) != OLD_HASH or raw[:4] != b'BIG4':
        raise ValueError('Unsupported original navigation package')
    count, index_end = struct.unpack_from('>II', raw, 8)
    if count != 4:
        raise ValueError('Unexpected navigation package entry count')
    entries = []
    pos = 16
    for _ in range(count):
        offset, size = struct.unpack_from('>II', raw, pos)
        pos += 8
        end = raw.index(b'\0', pos, index_end)
        name = raw[pos:end]
        pos = end + 1
        if offset < index_end or offset + size > len(raw):
            raise ValueError('Invalid archive entry bounds')
        entries.append([name, raw[offset:offset + size]])
    tail = raw[pos:index_end]
    by_ext = {name.decode('ascii').lower().rsplit('.', 1)[-1]: data for name, data in entries}
    if set(by_ext) != {'bin', 'imp', 'manifest', 'relo'}:
        raise ValueError('Unexpected package entries')
    binary = by_ext['bin']
    manifest = bytearray(by_ext['manifest'])
    old_apt_size = struct.unpack_from('<I', binary, 12)[0]
    old_padded = (old_apt_size + 3) & ~3
    old_apt = bytearray(binary[16:16 + old_apt_size])
    if old_apt[11626:11636] != OLD_ACTION:
        raise ValueError('Expected unconditional menu action was not found')
    old_apt[11626:11636] = STOCK_ACTION
    if sha(old_apt) != STOCK_APT_HASH:
        raise ValueError('Original APT does not match the pinned EA source')
    if len(apt) <= old_apt_size or b'epicWarAutoNavigated' not in apt or OLD_ACTION in apt:
        raise ValueError('Candidate APT does not contain the expected guarded action')
    # The builder relocates exactly one four-byte root-action pointer and appends the new action.
    differences = [i for i in range(old_apt_size) if apt[i] != old_apt[i]]
    if not differences or max(differences) - min(differences) > 3:
        raise ValueError('Candidate altered more than the root-action pointer')
    old_asset_size = 12 + old_padded
    new_padded = (len(apt) + 3) & ~3
    new_asset_size = 12 + new_padded
    if struct.unpack_from('<I', manifest, 16)[0] != len(binary) - 4 or struct.unpack_from('<I', manifest, 80)[0] != old_asset_size:
        raise ValueError('Unexpected manifest stream sizes')
    new_binary = bytearray(binary[:16])
    struct.pack_into('<I', new_binary, 12, len(apt))
    new_binary += apt + b'\0' * (new_padded - len(apt))
    new_binary += binary[4 + old_asset_size:]
    struct.pack_into('<I', manifest, 16, len(new_binary) - 4)
    struct.pack_into('<I', manifest, 80, new_asset_size)
    by_ext['bin'] = bytes(new_binary)
    by_ext['manifest'] = bytes(manifest)
    payloads = [(name, by_ext[name.decode('ascii').lower().rsplit('.', 1)[-1]]) for name, _ in entries]
    out = bytearray(b'BIG4' + b'\0' * 4 + struct.pack('>II', count, index_end))
    offset = index_end
    for name, data in payloads:
        out += struct.pack('>II', offset, len(data)) + name + b'\0'
        offset += len(data)
    out += tail
    if len(out) != index_end:
        raise ValueError('Package index length changed')
    for _, data in payloads:
        out += data
    struct.pack_into('<I', out, 4, len(out))
    path = Path(output_path)
    with path.open('xb') as f:
        f.write(out)
    return sha(out)
