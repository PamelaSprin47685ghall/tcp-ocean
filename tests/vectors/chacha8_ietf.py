#!/usr/bin/env python3
"""Independent IETF-layout ChaCha8 / ChaCha8-Poly1305 known-answer generator.

Suite 0x03 is RFC 8439 section 2.8 with 8 ChaCha rounds (4 double-rounds)
instead of 20. The 20-round path of this script matches RFC 8439 2.3.2,
which is checked when the file is run.
"""
import struct

def rotl(x, n):
    return ((x << n) | (x >> (32 - n))) & 0xffffffff

def qr(s, a, b, c, d):
    s[a] = (s[a] + s[b]) & 0xffffffff; s[d] = rotl(s[d] ^ s[a], 16)
    s[c] = (s[c] + s[d]) & 0xffffffff; s[b] = rotl(s[b] ^ s[c], 12)
    s[a] = (s[a] + s[b]) & 0xffffffff; s[d] = rotl(s[d] ^ s[a], 8)
    s[c] = (s[c] + s[d]) & 0xffffffff; s[b] = rotl(s[b] ^ s[c], 7)

def chacha_block(key, counter, nonce, double_rounds):
    st = [0x61707865, 0x3320646e, 0x79622d32, 0x6b206574]
    st += list(struct.unpack("<8I", key))
    st += [counter]
    st += list(struct.unpack("<3I", nonce))
    w = st[:]
    for _ in range(double_rounds):
        qr(w, 0, 4, 8, 12); qr(w, 1, 5, 9, 13)
        qr(w, 2, 6, 10, 14); qr(w, 3, 7, 11, 15)
        qr(w, 0, 5, 10, 15); qr(w, 1, 6, 11, 12)
        qr(w, 2, 7, 8, 13); qr(w, 3, 4, 9, 14)
    return b"".join(struct.pack("<I", (w[i] + st[i]) & 0xffffffff) for i in range(16))

def xor_stream(key, counter, nonce, data, double_rounds):
    out = bytearray(data)
    off, ctr = 0, counter
    while off < len(data):
        block = chacha_block(key, ctr, nonce, double_rounds)
        n = min(64, len(data) - off)
        for i in range(n):
            out[off + i] ^= block[i]
        off += n
        ctr += 1
    return bytes(out)

def poly1305(key, msg):
    r = int.from_bytes(key[:16], "little") & 0x0ffffffc0ffffffc0ffffffc0fffffff
    s = int.from_bytes(key[16:], "little")
    h, p = 0, (1 << 130) - 5
    for i in range(0, len(msg), 16):
        block = msg[i:i + 16]
        h = (h + int.from_bytes(block + b"\x01", "little")) * r % p
    return ((h + s) & ((1 << 128) - 1)).to_bytes(16, "little")

def aead_encrypt(key, nonce, plain, aad, double_rounds):
    otk = chacha_block(key, 0, nonce, double_rounds)[:32]
    cipher = xor_stream(key, 1, nonce, plain, double_rounds)
    pad = lambda b: b + b"\x00" * ((16 - len(b) % 16) % 16)
    mac = pad(aad) + pad(cipher) + struct.pack("<QQ", len(aad), len(cipher))
    return cipher, poly1305(otk, mac)

if __name__ == "__main__":
    key = bytes(range(32))
    nonce = bytes.fromhex("000000090000004a00000000")
    b20 = chacha_block(key, 1, nonce, 10).hex()
    assert b20 == (
        "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e"
        "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e"
    )
    print("block8", chacha_block(key, 1, nonce, 4).hex())
