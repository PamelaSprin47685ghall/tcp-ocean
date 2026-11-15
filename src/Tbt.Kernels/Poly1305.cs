using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tbt.Crypto.Kernels;

/// <summary>Poly1305 (RFC 8439 §2.5), 44/44/42-bit limbs. Zero allocation per Update/Finish.</summary>
public sealed class Poly1305
{
    private const ulong M44 = 0xfffffffffffUL;
    private const ulong M42 = 0x3ffffffffffUL;

    private ulong _r0, _r1, _r2;
    private ulong _h0, _h1, _h2;
    private ulong _pad0, _pad1;

    public void Init(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("Poly1305 key is 32 bytes", nameof(key));
        ulong t0 = ReadU64(key);
        ulong t1 = ReadU64(key.Slice(8));
        _r0 = t0 & 0xffc0fffffffUL;
        _r1 = ((t0 >> 44) | (t1 << 20)) & 0xfffffc0ffffUL;
        _r2 = (t1 >> 24) & 0x00ffffffc0fUL;
        _h0 = 0;
        _h1 = 0;
        _h2 = 0;
        _pad0 = ReadU64(key.Slice(16));
        _pad1 = ReadU64(key.Slice(24));
    }

    public void Update(ReadOnlySpan<byte> data, bool padded)
    {
        int i = 0;
        int last = data.Length - 16;
        while (i <= last)
        {
            Block(ReadU64(data.Slice(i)), ReadU64(data.Slice(i + 8)), 1UL << 40);
            i += 16;
        }
        if (i < data.Length)
        {
            ReadOnlySpan<byte> d = data.Slice(i);
            ulong t0 = 0, t1 = 0;
            for (int j = 0; j < d.Length; j++)
            {
                if (j < 8) t0 |= (ulong)d[j] << (8 * j);
                else t1 |= (ulong)d[j] << (8 * (j - 8));
            }
            if (padded) Block(t0, t1, 1UL << 40);
            else
            {
                int n = d.Length;
                if (n < 8) t0 |= 1UL << (8 * n);
                else t1 |= 1UL << (8 * (n - 8));
                Block(t0, t1, 0);
            }
        }
    }

    public void UpdateWords(ulong w0, ulong w1) => Block(w0, w1, 1UL << 40);

    public void Finish(Span<byte> tag)
    {
        ulong c = _h1 >> 44;
        _h1 &= M44;
        _h2 += c;
        c = _h2 >> 42;
        _h2 &= M42;
        _h0 += c * 5UL;
        c = _h0 >> 44;
        _h0 &= M44;
        _h1 += c;
        c = _h1 >> 44;
        _h1 &= M44;
        _h2 += c;
        c = _h2 >> 42;
        _h2 &= M42;
        _h0 += c * 5UL;
        c = _h0 >> 44;
        _h0 &= M44;
        _h1 += c;

        ulong g0 = _h0 + 5UL;
        c = g0 >> 44;
        g0 &= M44;
        ulong g1 = _h1 + c;
        c = g1 >> 44;
        g1 &= M44;
        ulong g2 = _h2 + c - (1UL << 42);
        ulong keep = (g2 >> 63) - 1UL;
        g0 &= keep;
        g1 &= keep;
        g2 &= keep;
        ulong drop = ~keep;
        _h0 = (_h0 & drop) | g0;
        _h1 = (_h1 & drop) | g1;
        _h2 = (_h2 & drop) | g2;
        _h0 += _pad0 & M44;
        c = _h0 >> 44;
        _h0 &= M44;
        _h1 += (((_pad0 >> 44) | (_pad1 << 20)) & M44) + c;
        c = _h1 >> 44;
        _h1 &= M44;
        _h2 = (_h2 + ((_pad1 >> 24) & M42) + c) & M42;
        BinaryPrimitives.WriteUInt64LittleEndian(tag, _h0 | (_h1 << 44));
        BinaryPrimitives.WriteUInt64LittleEndian(tag.Slice(8), (_h1 >> 20) | (_h2 << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Block(ulong t0, ulong t1, ulong hibit)
    {
        ulong h0 = _h0 + (t0 & M44);
        ulong h1 = _h1 + (((t0 >> 44) | (t1 << 20)) & M44);
        ulong h2 = _h2 + ((t1 >> 24) & M42 | hibit);
        ulong s1 = _r1 * 20UL;
        ulong s2 = _r2 * 20UL;
        ulong d0h = 0, l0 = 0, d1h = 0, l1 = 0, d2h = 0, l2 = 0;
        Madd(ref d0h, ref l0, h0, _r0);
        Madd(ref d0h, ref l0, h1, s2);
        Madd(ref d0h, ref l0, h2, s1);
        Madd(ref d1h, ref l1, h0, _r1);
        Madd(ref d1h, ref l1, h1, _r0);
        Madd(ref d1h, ref l1, h2, s2);
        Madd(ref d2h, ref l2, h0, _r2);
        Madd(ref d2h, ref l2, h1, _r1);
        Madd(ref d2h, ref l2, h2, _r0);
        ulong c = (l0 >> 44) | (d0h << 20);
        h0 = l0 & M44;
        ulong l1c = l1 + c;
        d1h += l1c < c ? 1UL : 0UL;
        c = (l1c >> 44) | (d1h << 20);
        h1 = l1c & M44;
        ulong l2c = l2 + c;
        d2h += l2c < c ? 1UL : 0UL;
        c = (l2c >> 42) | (d2h << 22);
        h2 = l2c & M42;
        h0 += c * 5UL;
        c = h0 >> 44;
        _h0 = h0 & M44;
        _h1 = h1 + c;
        _h2 = h2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Madd(ref ulong hi, ref ulong lo, ulong a, ulong b)
    {
        ulong high = Math.BigMul(a, b, out ulong low);
        ulong l = lo + low;
        hi += high + (l < low ? 1UL : 0UL);
        lo = l;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(ReadOnlySpan<byte> s)
    {
        ulong v = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(s));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }
}
