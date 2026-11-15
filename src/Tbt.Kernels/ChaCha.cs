using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tbt.Crypto.Kernels;

/// <summary>
/// ChaCha block and key stream (RFC 8439 §§2.3–2.4) with a variable double-round
/// count (10 = ChaCha20, 4 = ChaCha8). Zero allocation. AVX2 runs 8 blocks at a
/// time; otherwise Vector128 runs 4, then 1; a scalar core covers the tail.
/// </summary>
public static class ChaCha
{
    public static void Block(int doubleRounds, uint[] key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> output)
        => BlockCore(doubleRounds, mix: false, key, counter, nonce, output);

    public static void XorStream(int doubleRounds, uint[] key, uint counter, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (doubleRounds < 1 || key.Length != 8 || nonce.Length != 12 || dst.Length != src.Length)
            throw new ArgumentException("bad key, nonce or length");
        int at = 0;
        uint ctr = counter;
        uint n0 = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        uint n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
        uint n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
        if (Avx2.IsSupported && BitConverter.IsLittleEndian)
        {
            while (src.Length - at >= 512)
            {
                OctaXor(doubleRounds, key, ctr, n0, n1, n2, src, dst, at);
                at += 512;
                ctr += 8;
            }
        }
        if (Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            if (Sse2.IsSupported)
            {
                while (src.Length - at >= 256)
                {
                    QuadXor(doubleRounds, key, ctr, n0, n1, n2, src, dst, at);
                    at += 256;
                    ctr += 4;
                }
            }
            while (src.Length - at >= 64)
            {
                SingleXor(doubleRounds, key, ctr, n0, n1, n2, src, dst, at);
                at += 64;
                ctr += 1;
            }
        }
        if (at < src.Length) src.Slice(at).CopyTo(dst.Slice(at));
        while (at < src.Length)
        {
            int n = Math.Min(64, src.Length - at);
            BlockCore(doubleRounds, mix: true, key, ctr, nonce, dst.Slice(at, n));
            at += n;
            ctr += 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Qr(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d = BitOperations.RotateLeft(d ^ a, 16);
        c += d; b = BitOperations.RotateLeft(b ^ c, 12);
        a += b; d = BitOperations.RotateLeft(d ^ a, 8);
        c += d; b = BitOperations.RotateLeft(b ^ c, 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Rotl128(Vector128<uint> v, int n)
        => Vector128.ShiftLeft(v, n) | Vector128.ShiftRightLogical(v, 32 - n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Rotl256(Vector256<uint> v, int n)
        => Vector256.ShiftLeft(v, n) | Vector256.ShiftRightLogical(v, 32 - n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Qr128(ref Vector128<uint> a, ref Vector128<uint> b, ref Vector128<uint> c, ref Vector128<uint> d)
    {
        a += b; d = Rotl128(d ^ a, 16);
        c += d; b = Rotl128(b ^ c, 12);
        a += b; d = Rotl128(d ^ a, 8);
        c += d; b = Rotl128(b ^ c, 7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Qr256(ref Vector256<uint> a, ref Vector256<uint> b, ref Vector256<uint> c, ref Vector256<uint> d)
    {
        a += b; d = Rotl256(d ^ a, 16);
        c += d; b = Rotl256(b ^ c, 12);
        a += b; d = Rotl256(d ^ a, 8);
        c += d; b = Rotl256(b ^ c, 7);
    }

    private static void BlockCore(int doubleRounds, bool mix, uint[] key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> output)
    {
        if (doubleRounds < 1 || key.Length != 8 || nonce.Length != 12 || output.Length > 64)
            throw new ArgumentException("bad key, nonce or output length");
        uint n0 = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        uint n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
        uint n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
        if (Vector128.IsHardwareAccelerated && output.Length == 64)
        {
            SingleWrite(doubleRounds, mix, key, counter, n0, n1, n2, output);
            return;
        }
        uint x0 = 0x61707865u, x1 = 0x3320646eu, x2 = 0x79622d32u, x3 = 0x6b206574u;
        uint x4 = key[0], x5 = key[1], x6 = key[2], x7 = key[3];
        uint x8 = key[4], x9 = key[5], x10 = key[6], x11 = key[7];
        uint x12 = counter, x13 = n0, x14 = n1, x15 = n2;
        for (int i = 0; i < doubleRounds; i++)
        {
            Qr(ref x0, ref x4, ref x8, ref x12);
            Qr(ref x1, ref x5, ref x9, ref x13);
            Qr(ref x2, ref x6, ref x10, ref x14);
            Qr(ref x3, ref x7, ref x11, ref x15);
            Qr(ref x0, ref x5, ref x10, ref x15);
            Qr(ref x1, ref x6, ref x11, ref x12);
            Qr(ref x2, ref x7, ref x8, ref x13);
            Qr(ref x3, ref x4, ref x9, ref x14);
        }
        Put(mix, output, 0, x0 + 0x61707865u);
        Put(mix, output, 1, x1 + 0x3320646eu);
        Put(mix, output, 2, x2 + 0x79622d32u);
        Put(mix, output, 3, x3 + 0x6b206574u);
        Put(mix, output, 4, x4 + key[0]);
        Put(mix, output, 5, x5 + key[1]);
        Put(mix, output, 6, x6 + key[2]);
        Put(mix, output, 7, x7 + key[3]);
        Put(mix, output, 8, x8 + key[4]);
        Put(mix, output, 9, x9 + key[5]);
        Put(mix, output, 10, x10 + key[6]);
        Put(mix, output, 11, x11 + key[7]);
        Put(mix, output, 12, x12 + counter);
        Put(mix, output, 13, x13 + n0);
        Put(mix, output, 14, x14 + n1);
        Put(mix, output, 15, x15 + n2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(bool mix, Span<byte> output, int i, uint w)
    {
        int at = 4 * i;
        if (at >= output.Length) return;
        int n = Math.Min(4, output.Length - at);
        for (int j = 0; j < n; j++)
        {
            byte k = (byte)(w >> (8 * j));
            output[at + j] = mix ? (byte)(output[at + j] ^ k) : k;
        }
    }

    private static void SingleWrite(int doubleRounds, bool mix, uint[] key, uint counter, uint n0, uint n1, uint n2, Span<byte> output)
    {
        Vector128<uint> r0 = Vector128.Create(0x61707865u, 0x3320646eu, 0x79622d32u, 0x6b206574u);
        Vector128<uint> r1 = Vector128.Create(key[0], key[1], key[2], key[3]);
        Vector128<uint> r2 = Vector128.Create(key[4], key[5], key[6], key[7]);
        Vector128<uint> r3 = Vector128.Create(counter, n0, n1, n2);
        Vector128<uint> a = r0, b = r1, c = r2, d = r3;
        Vector128<uint> s16 = Vector128.Create(1u, 2u, 3u, 0u);
        Vector128<uint> s8 = Vector128.Create(2u, 3u, 0u, 1u);
        Vector128<uint> s4 = Vector128.Create(3u, 0u, 1u, 2u);
        for (int i = 0; i < doubleRounds; i++)
        {
            Qr128(ref a, ref b, ref c, ref d);
            b = Vector128.Shuffle(b, s16); c = Vector128.Shuffle(c, s8); d = Vector128.Shuffle(d, s4);
            Qr128(ref a, ref b, ref c, ref d);
            b = Vector128.Shuffle(b, s4); c = Vector128.Shuffle(c, s8); d = Vector128.Shuffle(d, s16);
        }
        a += r0; b += r1; c += r2; d += r3;
        if (mix)
        {
            (Vector128.Create<byte>(output.Slice(0, 16)) ^ a.AsByte()).CopyTo(output);
            (Vector128.Create<byte>(output.Slice(16, 16)) ^ b.AsByte()).CopyTo(output.Slice(16));
            (Vector128.Create<byte>(output.Slice(32, 16)) ^ c.AsByte()).CopyTo(output.Slice(32));
            (Vector128.Create<byte>(output.Slice(48, 16)) ^ d.AsByte()).CopyTo(output.Slice(48));
        }
        else
        {
            a.AsByte().CopyTo(output);
            b.AsByte().CopyTo(output.Slice(16));
            c.AsByte().CopyTo(output.Slice(32));
            d.AsByte().CopyTo(output.Slice(48));
        }
    }

    private static void SingleXor(int doubleRounds, uint[] key, uint ctr, uint n0, uint n1, uint n2,
        ReadOnlySpan<byte> src, Span<byte> dst, int at)
    {
        Vector128<uint> r0 = Vector128.Create(0x61707865u, 0x3320646eu, 0x79622d32u, 0x6b206574u);
        Vector128<uint> r1 = Vector128.Create(key[0], key[1], key[2], key[3]);
        Vector128<uint> r2 = Vector128.Create(key[4], key[5], key[6], key[7]);
        Vector128<uint> r3 = Vector128.Create(ctr, n0, n1, n2);
        Vector128<uint> a = r0, b = r1, c = r2, d = r3;
        Vector128<uint> s16 = Vector128.Create(1u, 2u, 3u, 0u);
        Vector128<uint> s8 = Vector128.Create(2u, 3u, 0u, 1u);
        Vector128<uint> s4 = Vector128.Create(3u, 0u, 1u, 2u);
        for (int i = 0; i < doubleRounds; i++)
        {
            Qr128(ref a, ref b, ref c, ref d);
            b = Vector128.Shuffle(b, s16); c = Vector128.Shuffle(c, s8); d = Vector128.Shuffle(d, s4);
            Qr128(ref a, ref b, ref c, ref d);
            b = Vector128.Shuffle(b, s4); c = Vector128.Shuffle(c, s8); d = Vector128.Shuffle(d, s16);
        }
        ReadOnlySpan<byte> s = src.Slice(at);
        Span<byte> o = dst.Slice(at);
        (Vector128.Create<byte>(s.Slice(0, 16)) ^ (a + r0).AsByte()).CopyTo(o);
        (Vector128.Create<byte>(s.Slice(16, 16)) ^ (b + r1).AsByte()).CopyTo(o.Slice(16));
        (Vector128.Create<byte>(s.Slice(32, 16)) ^ (c + r2).AsByte()).CopyTo(o.Slice(32));
        (Vector128.Create<byte>(s.Slice(48, 16)) ^ (d + r3).AsByte()).CopyTo(o.Slice(48));
    }

    private static void Transpose4(ref Vector128<uint> a, ref Vector128<uint> b, ref Vector128<uint> c, ref Vector128<uint> d)
    {
        Vector128<uint> t0 = Sse2.UnpackLow(a, b);
        Vector128<uint> t1 = Sse2.UnpackHigh(a, b);
        Vector128<uint> t2 = Sse2.UnpackLow(c, d);
        Vector128<uint> t3 = Sse2.UnpackHigh(c, d);
        a = Sse2.UnpackLow(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        b = Sse2.UnpackHigh(t0.AsUInt64(), t2.AsUInt64()).AsUInt32();
        c = Sse2.UnpackLow(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
        d = Sse2.UnpackHigh(t1.AsUInt64(), t3.AsUInt64()).AsUInt32();
    }

    private static void QuadXor(int doubleRounds, uint[] key, uint ctr, uint n0, uint n1, uint n2,
        ReadOnlySpan<byte> src, Span<byte> dst, int at)
    {
        Vector128<uint> c0 = Vector128.Create(0x61707865u), c1 = Vector128.Create(0x3320646eu);
        Vector128<uint> c2 = Vector128.Create(0x79622d32u), c3 = Vector128.Create(0x6b206574u);
        Vector128<uint> k0 = Vector128.Create(key[0]), k1 = Vector128.Create(key[1]);
        Vector128<uint> k2 = Vector128.Create(key[2]), k3 = Vector128.Create(key[3]);
        Vector128<uint> k4 = Vector128.Create(key[4]), k5 = Vector128.Create(key[5]);
        Vector128<uint> k6 = Vector128.Create(key[6]), k7 = Vector128.Create(key[7]);
        Vector128<uint> cc = Vector128.Create(ctr, ctr + 1u, ctr + 2u, ctr + 3u);
        Vector128<uint> m0 = Vector128.Create(n0), m1 = Vector128.Create(n1), m2 = Vector128.Create(n2);
        Vector128<uint> x0 = c0, x1 = c1, x2 = c2, x3 = c3;
        Vector128<uint> x4 = k0, x5 = k1, x6 = k2, x7 = k3;
        Vector128<uint> x8 = k4, x9 = k5, x10 = k6, x11 = k7;
        Vector128<uint> x12 = cc, x13 = m0, x14 = m1, x15 = m2;
        for (int i = 0; i < doubleRounds; i++)
        {
            Qr128(ref x0, ref x4, ref x8, ref x12);
            Qr128(ref x1, ref x5, ref x9, ref x13);
            Qr128(ref x2, ref x6, ref x10, ref x14);
            Qr128(ref x3, ref x7, ref x11, ref x15);
            Qr128(ref x0, ref x5, ref x10, ref x15);
            Qr128(ref x1, ref x6, ref x11, ref x12);
            Qr128(ref x2, ref x7, ref x8, ref x13);
            Qr128(ref x3, ref x4, ref x9, ref x14);
        }
        x0 += c0; x1 += c1; x2 += c2; x3 += c3;
        x4 += k0; x5 += k1; x6 += k2; x7 += k3;
        x8 += k4; x9 += k5; x10 += k6; x11 += k7;
        x12 += cc; x13 += m0; x14 += m1; x15 += m2;
        Transpose4(ref x0, ref x1, ref x2, ref x3);
        Transpose4(ref x4, ref x5, ref x6, ref x7);
        Transpose4(ref x8, ref x9, ref x10, ref x11);
        Transpose4(ref x12, ref x13, ref x14, ref x15);
        Xor128(dst, src, at + 0, x0); Xor128(dst, src, at + 16, x4);
        Xor128(dst, src, at + 32, x8); Xor128(dst, src, at + 48, x12);
        Xor128(dst, src, at + 64, x1); Xor128(dst, src, at + 80, x5);
        Xor128(dst, src, at + 96, x9); Xor128(dst, src, at + 112, x13);
        Xor128(dst, src, at + 128, x2); Xor128(dst, src, at + 144, x6);
        Xor128(dst, src, at + 160, x10); Xor128(dst, src, at + 176, x14);
        Xor128(dst, src, at + 192, x3); Xor128(dst, src, at + 208, x7);
        Xor128(dst, src, at + 224, x11); Xor128(dst, src, at + 240, x15);
    }

    private static void Transpose8(ref Vector256<uint> v0, ref Vector256<uint> v1, ref Vector256<uint> v2, ref Vector256<uint> v3,
        ref Vector256<uint> v4, ref Vector256<uint> v5, ref Vector256<uint> v6, ref Vector256<uint> v7)
    {
        Vector256<uint> a0 = Avx2.UnpackLow(v0, v1), a1 = Avx2.UnpackHigh(v0, v1);
        Vector256<uint> a2 = Avx2.UnpackLow(v2, v3), a3 = Avx2.UnpackHigh(v2, v3);
        Vector256<uint> a4 = Avx2.UnpackLow(v4, v5), a5 = Avx2.UnpackHigh(v4, v5);
        Vector256<uint> a6 = Avx2.UnpackLow(v6, v7), a7 = Avx2.UnpackHigh(v6, v7);
        Vector256<uint> b0 = Avx2.UnpackLow(a0.AsUInt64(), a2.AsUInt64()).AsUInt32();
        Vector256<uint> b1 = Avx2.UnpackHigh(a0.AsUInt64(), a2.AsUInt64()).AsUInt32();
        Vector256<uint> b2 = Avx2.UnpackLow(a1.AsUInt64(), a3.AsUInt64()).AsUInt32();
        Vector256<uint> b3 = Avx2.UnpackHigh(a1.AsUInt64(), a3.AsUInt64()).AsUInt32();
        Vector256<uint> b4 = Avx2.UnpackLow(a4.AsUInt64(), a6.AsUInt64()).AsUInt32();
        Vector256<uint> b5 = Avx2.UnpackHigh(a4.AsUInt64(), a6.AsUInt64()).AsUInt32();
        Vector256<uint> b6 = Avx2.UnpackLow(a5.AsUInt64(), a7.AsUInt64()).AsUInt32();
        Vector256<uint> b7 = Avx2.UnpackHigh(a5.AsUInt64(), a7.AsUInt64()).AsUInt32();
        v0 = Avx2.Permute2x128(b0.AsInt32(), b4.AsInt32(), 0x20).AsUInt32();
        v1 = Avx2.Permute2x128(b1.AsInt32(), b5.AsInt32(), 0x20).AsUInt32();
        v2 = Avx2.Permute2x128(b2.AsInt32(), b6.AsInt32(), 0x20).AsUInt32();
        v3 = Avx2.Permute2x128(b3.AsInt32(), b7.AsInt32(), 0x20).AsUInt32();
        v4 = Avx2.Permute2x128(b0.AsInt32(), b4.AsInt32(), 0x31).AsUInt32();
        v5 = Avx2.Permute2x128(b1.AsInt32(), b5.AsInt32(), 0x31).AsUInt32();
        v6 = Avx2.Permute2x128(b2.AsInt32(), b6.AsInt32(), 0x31).AsUInt32();
        v7 = Avx2.Permute2x128(b3.AsInt32(), b7.AsInt32(), 0x31).AsUInt32();
    }

    private static void OctaXor(int doubleRounds, uint[] key, uint ctr, uint n0, uint n1, uint n2,
        ReadOnlySpan<byte> src, Span<byte> dst, int at)
    {
        Vector256<uint> c0 = Vector256.Create(0x61707865u), c1 = Vector256.Create(0x3320646eu);
        Vector256<uint> c2 = Vector256.Create(0x79622d32u), c3 = Vector256.Create(0x6b206574u);
        Vector256<uint> k0 = Vector256.Create(key[0]), k1 = Vector256.Create(key[1]);
        Vector256<uint> k2 = Vector256.Create(key[2]), k3 = Vector256.Create(key[3]);
        Vector256<uint> k4 = Vector256.Create(key[4]), k5 = Vector256.Create(key[5]);
        Vector256<uint> k6 = Vector256.Create(key[6]), k7 = Vector256.Create(key[7]);
        Vector256<uint> cc = Vector256.Create(ctr, ctr + 1u, ctr + 2u, ctr + 3u, ctr + 4u, ctr + 5u, ctr + 6u, ctr + 7u);
        Vector256<uint> m0 = Vector256.Create(n0), m1 = Vector256.Create(n1), m2 = Vector256.Create(n2);
        Vector256<uint> x0 = c0, x1 = c1, x2 = c2, x3 = c3;
        Vector256<uint> x4 = k0, x5 = k1, x6 = k2, x7 = k3;
        Vector256<uint> x8 = k4, x9 = k5, x10 = k6, x11 = k7;
        Vector256<uint> x12 = cc, x13 = m0, x14 = m1, x15 = m2;
        for (int i = 0; i < doubleRounds; i++)
        {
            Qr256(ref x0, ref x4, ref x8, ref x12);
            Qr256(ref x1, ref x5, ref x9, ref x13);
            Qr256(ref x2, ref x6, ref x10, ref x14);
            Qr256(ref x3, ref x7, ref x11, ref x15);
            Qr256(ref x0, ref x5, ref x10, ref x15);
            Qr256(ref x1, ref x6, ref x11, ref x12);
            Qr256(ref x2, ref x7, ref x8, ref x13);
            Qr256(ref x3, ref x4, ref x9, ref x14);
        }
        x0 += c0; x1 += c1; x2 += c2; x3 += c3;
        x4 += k0; x5 += k1; x6 += k2; x7 += k3;
        x8 += k4; x9 += k5; x10 += k6; x11 += k7;
        x12 += cc; x13 += m0; x14 += m1; x15 += m2;
        Transpose8(ref x0, ref x1, ref x2, ref x3, ref x4, ref x5, ref x6, ref x7);
        Transpose8(ref x8, ref x9, ref x10, ref x11, ref x12, ref x13, ref x14, ref x15);
        Xor256(dst, src, at + 0, x0); Xor256(dst, src, at + 32, x8);
        Xor256(dst, src, at + 64, x1); Xor256(dst, src, at + 96, x9);
        Xor256(dst, src, at + 128, x2); Xor256(dst, src, at + 160, x10);
        Xor256(dst, src, at + 192, x3); Xor256(dst, src, at + 224, x11);
        Xor256(dst, src, at + 256, x4); Xor256(dst, src, at + 288, x12);
        Xor256(dst, src, at + 320, x5); Xor256(dst, src, at + 352, x13);
        Xor256(dst, src, at + 384, x6); Xor256(dst, src, at + 416, x14);
        Xor256(dst, src, at + 448, x7); Xor256(dst, src, at + 480, x15);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Xor128(Span<byte> dst, ReadOnlySpan<byte> src, int off, Vector128<uint> k)
        => (Vector128.Create<byte>(src.Slice(off, 16)) ^ k.AsByte()).CopyTo(dst.Slice(off));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Xor256(Span<byte> dst, ReadOnlySpan<byte> src, int off, Vector256<uint> k)
        => (Vector256.Create<byte>(src.Slice(off, 32)) ^ k.AsByte()).CopyTo(dst.Slice(off));
}
