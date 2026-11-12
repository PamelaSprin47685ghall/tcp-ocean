#!/usr/bin/env python3
"""Independent reference for the main handshake, key schedule and one sealed data frame.

Written from spec 6.3-6.5 with the Python `cryptography` package; it shares no code with
the F# implementation. Run: python3 make_handshake_vectors.py > handshake-vectors.json
Padding is deterministic here for testing only; production padding comes from the CSPRNG.
"""
import hashlib, hmac, json, struct
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms

def mac(k, d): return hmac.new(k, d, hashlib.sha256).digest()
def extract(salt, ikm): return mac(salt, ikm)
def expand(prk, info, n):
    out, t, i = b'', b'', 1
    while len(out) < n:
        t = mac(prk, t + info + bytes([i])); out += t; i += 1
    return out[:n]
def derive32(salt, ikm, info): return expand(extract(salt, ikm), info, 32)

Z12 = bytes(12)
psk = bytes(range(0x40, 0x60))
cn = bytes(range(0x00, 0x20))
sn = bytes(range(0x80, 0xA0))
client_id = bytes(range(0xC0, 0xD0))
session_id = bytes(range(0x10, 0x20))

# CLIENT_INIT body, little-endian integers.
body = struct.pack('<BBH16sQHH', 2, 0x02, 0x0705, client_id, 1_800_000_000_000, 1448, 256)
body += bytes(32) + bytes((7 * i) % 256 for i in range(944))
assert len(body) == 1008
hint = mac(psk, b'hint' + cn)[:8]
kc = derive32(cn, psk, b'tbtun v2 init c')
ct = ChaCha20Poly1305(kc).encrypt(Z12, body, b'')
client_init = cn + hint + bytes(16) + ct
assert len(client_init) == 1080

# SERVER_INIT body.
sbody = struct.pack('<BBH16sHHHHQ', 0, 0x02, 0x0705, session_id, 1344, 0, 256, 4096, 1_800_000_000_000)
sbody += bytes(32) + bytes(16) + bytes((11 * i) % 256 for i in range(948))
assert len(sbody) == 1032
ks = derive32(cn + sn, psk, b'tbtun v2 init s')
server_init = sn + ChaCha20Poly1305(ks).encrypt(Z12, sbody, b'')
assert len(server_init) == 1080

S, L, suite = 1344, 1415, 0x02
prk0 = extract(cn + sn, psk)
th = hashlib.sha256(client_init + server_init).digest()
ctx = b'tbtun v2' + session_id + hashlib.sha256(client_id).digest()[:8] + bytes([suite]) + struct.pack('>H', L) + th
be32 = lambda g: struct.pack('>I', g)
k_up0 = expand(prk0, ctx + b'up key' + be32(0), 32)
k_down0 = expand(prk0, ctx + b'down key' + be32(0), 32)
hp_up = expand(prk0, ctx + b'up hp', 32)
hp_down = expand(prk0, ctx + b'down hp', 32)
g_up = struct.unpack('<Q', expand(prk0, ctx + b'up graph', 8))[0]
g_down = struct.unpack('<Q', expand(prk0, ctx + b'down graph', 8))[0]
k_path = expand(prk0, ctx + b'path', 32)
prk1 = expand(prk0, ctx + b'next' + be32(1), 32)
k_up1 = expand(prk1, ctx + b'up key' + be32(1), 32)

# One sealed upstream data frame.
counter, path, direction = 0x0102030405060708, 7, 1
chunk = b'580\r\n'
meta = bytes([1, 0, 3, 16]) + bytes(36)
symbol = bytes((i * 13 + 5) % 256 for i in range(S))
nonce = bytes([direction]) + struct.pack('>H', path) + b'\x00' + struct.pack('>Q', counter)
aad = chunk + struct.pack('>Q', counter) + struct.pack('>H', path) + bytes([direction]) + struct.pack('>H', L)
sealed = ChaCha20Poly1305(k_up0).encrypt(nonce, meta + symbol, aad)
sample = sealed[:16]
ks_stream = Cipher(algorithms.ChaCha20(hp_up, sample[:4] + sample[4:16]), mode=None).encryptor().update(bytes(8))
masked = bytes(a ^ b for a, b in zip(struct.pack('>Q', counter), ks_stream))
frame = chunk + masked + sealed + b'\r\n'
assert len(frame) == L

print(json.dumps({
    'psk_hex': psk.hex(), 'client_nonce_hex': cn.hex(), 'server_nonce_hex': sn.hex(),
    'client_id_hex': client_id.hex(), 'session_id_hex': session_id.hex(),
    'client_init_hex': client_init.hex(), 'server_init_hex': server_init.hex(),
    'th_hex': th.hex(), 'k_up0_hex': k_up0.hex(), 'k_down0_hex': k_down0.hex(),
    'hp_up_hex': hp_up.hex(), 'hp_down_hex': hp_down.hex(),
    'graph_seed_up': f'{g_up:#018x}', 'graph_seed_down': f'{g_down:#018x}',
    'k_path_hex': k_path.hex(), 'k_up1_hex': k_up1.hex(),
    'frame_counter': f'{counter:#018x}', 'frame_path': path,
    'frame_meta_hex': meta.hex(), 'frame_hex': frame.hex(),
}, indent=1))
