using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tbt.Crypto.Kernels;

/// <summary>
/// Poly1305 (RFC 8439 §2.5), poly1305-donna-64 style: 3×(44/44/42)-bit limbs
/// and UInt128 / Math.BigMul products. Init precomputes r², r³, r⁴ so Update
/// can absorb 4 (then 2) blocks at a time with independent multiplies. No
/// secret-dependent branch or heap allocation on Update/Finish; the message
/// length is public.
/// </summary>
public sealed class Poly1305
{
    private const ulong M44 = 0xfffffffffffUL;
    private const ulong M42 = 0x3ffffffffffUL;

    // r^k as (r0,r1,r2) and s1=r1*20, s2=r2*20 for k = 1,2,3,4.
    private ulong _r10, _r11, _r12, _s11, _s12;
    private ulong _r20, _r21, _r22, _s21, _s22;
    private ulong _r30, _r31, _r32, _s31, _s32;
    private ulong _r40, _r41, _r42, _s41, _s42;
    private ulong _h0, _h1, _h2;
    private ulong _pad0, _pad1;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Init(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("Poly1305 key is 32 bytes", nameof(key));
        ulong t0 = ReadU64(key);
        ulong t1 = ReadU64(key.Slice(8));
        _r10 = t0 & 0xffc0fffffffUL;
        _r11 = ((t0 >> 44) | (t1 << 20)) & 0xfffffc0ffffUL;
        _r12 = (t1 >> 24) & 0x00ffffffc0fUL;
        _s11 = _r11 * 20UL;
        _s12 = _r12 * 20UL;
        Mul(out _r20, out _r21, out _r22, _r10, _r11, _r12, _r10, _r11, _r12, _s11, _s12);
        CarryFull(ref _r20, ref _r21, ref _r22);
        _s21 = _r21 * 20UL;
        _s22 = _r22 * 20UL;
        Mul(out _r30, out _r31, out _r32, _r20, _r21, _r22, _r10, _r11, _r12, _s11, _s12);
        CarryFull(ref _r30, ref _r31, ref _r32);
        _s31 = _r31 * 20UL;
        _s32 = _r32 * 20UL;
        Mul(out _r40, out _r41, out _r42, _r20, _r21, _r22, _r20, _r21, _r22, _s21, _s22);
        CarryFull(ref _r40, ref _r41, ref _r42);
        _s41 = _r41 * 20UL;
        _s42 = _r42 * 20UL;
        _h0 = 0;
        _h1 = 0;
        _h2 = 0;
        _pad0 = ReadU64(key.Slice(16));
        _pad1 = ReadU64(key.Slice(24));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Update(ReadOnlySpan<byte> data, bool padded)
    {
        int i = 0;
        int len = data.Length;
        ref byte p = ref MemoryMarshal.GetReference(data);

        while (len - i >= 64)
        {
            AddMsg(out ulong a0, out ulong a1, out ulong a2, _h0, _h1, _h2, ref p, i);
            LoadMsg(out ulong b0, out ulong b1, out ulong b2, ref p, i + 16);
            LoadMsg(out ulong c0, out ulong c1, out ulong c2, ref p, i + 32);
            LoadMsg(out ulong d0, out ulong d1, out ulong d2, ref p, i + 48);
            UInt128 x0 = 0, x1 = 0, x2 = 0;
            Acc(ref x0, ref x1, ref x2, a0, a1, a2, _r40, _r41, _r42, _s41, _s42);
            Acc(ref x0, ref x1, ref x2, b0, b1, b2, _r30, _r31, _r32, _s31, _s32);
            Acc(ref x0, ref x1, ref x2, c0, c1, c2, _r20, _r21, _r22, _s21, _s22);
            Acc(ref x0, ref x1, ref x2, d0, d1, d2, _r10, _r11, _r12, _s11, _s12);
            Reduce(x0, x1, x2, out _h0, out _h1, out _h2);
            i += 64;
        }
        if (len - i >= 32)
        {
            AddMsg(out ulong a0, out ulong a1, out ulong a2, _h0, _h1, _h2, ref p, i);
            LoadMsg(out ulong b0, out ulong b1, out ulong b2, ref p, i + 16);
            UInt128 x0 = 0, x1 = 0, x2 = 0;
            Acc(ref x0, ref x1, ref x2, a0, a1, a2, _r20, _r21, _r22, _s21, _s22);
            Acc(ref x0, ref x1, ref x2, b0, b1, b2, _r10, _r11, _r12, _s11, _s12);
            Reduce(x0, x1, x2, out _h0, out _h1, out _h2);
            i += 32;
        }
        if (len - i >= 16)
        {
            AddMsg(out ulong a0, out ulong a1, out ulong a2, _h0, _h1, _h2, ref p, i);
            Mul(out _h0, out _h1, out _h2, a0, a1, a2, _r10, _r11, _r12, _s11, _s12);
            i += 16;
        }
        if (i < len)
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Finish(Span<byte> tag)
    {
        ulong h0 = _h0, h1 = _h1, h2 = _h2;
        ulong c = h1 >> 44;
        h1 &= M44;
        h2 += c;
        c = h2 >> 42;
        h2 &= M42;
        h0 += c * 5UL;
        c = h0 >> 44;
        h0 &= M44;
        h1 += c;
        c = h1 >> 44;
        h1 &= M44;
        h2 += c;
        c = h2 >> 42;
        h2 &= M42;
        h0 += c * 5UL;
        c = h0 >> 44;
        h0 &= M44;
        h1 += c;

        // h + (-p), then select h or h-p without a branch.
        ulong g0 = h0 + 5UL;
        c = g0 >> 44;
        g0 &= M44;
        ulong g1 = h1 + c;
        c = g1 >> 44;
        g1 &= M44;
        ulong g2 = h2 + c - (1UL << 42);
        ulong keep = (g2 >> 63) - 1UL;
        g0 &= keep;
        g1 &= keep;
        g2 &= keep;
        ulong drop = ~keep;
        h0 = (h0 & drop) | g0;
        h1 = (h1 & drop) | g1;
        h2 = (h2 & drop) | g2;
        h0 += _pad0 & M44;
        c = h0 >> 44;
        h0 &= M44;
        h1 += (((_pad0 >> 44) | (_pad1 << 20)) & M44) + c;
        c = h1 >> 44;
        h1 &= M44;
        h2 = (h2 + ((_pad1 >> 24) & M42) + c) & M42;
        BinaryPrimitives.WriteUInt64LittleEndian(tag, h0 | (h1 << 44));
        BinaryPrimitives.WriteUInt64LittleEndian(tag.Slice(8), (h1 >> 20) | (h2 << 24));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Block(ulong t0, ulong t1, ulong hibit)
    {
        ulong a0 = _h0 + (t0 & M44);
        ulong a1 = _h1 + (((t0 >> 44) | (t1 << 20)) & M44);
        ulong a2 = _h2 + ((t1 >> 24) & M42 | hibit);
        Mul(out _h0, out _h1, out _h2, a0, a1, a2, _r10, _r11, _r12, _s11, _s12);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadMsg(out ulong m0, out ulong m1, out ulong m2, ref byte p, int off)
    {
        ulong t0 = ReadU64(ref Unsafe.Add(ref p, off));
        ulong t1 = ReadU64(ref Unsafe.Add(ref p, off + 8));
        m0 = t0 & M44;
        m1 = ((t0 >> 44) | (t1 << 20)) & M44;
        m2 = (t1 >> 24) & M42 | (1UL << 40);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddMsg(out ulong a0, out ulong a1, out ulong a2,
        ulong h0, ulong h1, ulong h2, ref byte p, int off)
    {
        LoadMsg(out a0, out a1, out a2, ref p, off);
        a0 += h0;
        a1 += h1;
        a2 += h2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Acc(ref UInt128 d0, ref UInt128 d1, ref UInt128 d2,
        ulong a0, ulong a1, ulong a2, ulong r0, ulong r1, ulong r2, ulong s1, ulong s2)
    {
        d0 += (UInt128)a0 * r0 + (UInt128)a1 * s2 + (UInt128)a2 * s1;
        d1 += (UInt128)a0 * r1 + (UInt128)a1 * r0 + (UInt128)a2 * s2;
        d2 += (UInt128)a0 * r2 + (UInt128)a1 * r1 + (UInt128)a2 * r0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Mul(out ulong h0, out ulong h1, out ulong h2,
        ulong a0, ulong a1, ulong a2, ulong r0, ulong r1, ulong r2, ulong s1, ulong s2)
    {
        UInt128 d0 = (UInt128)a0 * r0 + (UInt128)a1 * s2 + (UInt128)a2 * s1;
        UInt128 d1 = (UInt128)a0 * r1 + (UInt128)a1 * r0 + (UInt128)a2 * s2;
        UInt128 d2 = (UInt128)a0 * r2 + (UInt128)a1 * r1 + (UInt128)a2 * r0;
        Reduce(d0, d1, d2, out h0, out h1, out h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Reduce(UInt128 d0, UInt128 d1, UInt128 d2, out ulong h0, out ulong h1, out ulong h2)
    {
        ulong c = (ulong)(d0 >> 44);
        h0 = (ulong)d0 & M44;
        d1 += c;
        c = (ulong)(d1 >> 44);
        h1 = (ulong)d1 & M44;
        d2 += c;
        c = (ulong)(d2 >> 42);
        h2 = (ulong)d2 & M42;
        h0 += c * 5UL;
        c = h0 >> 44;
        h0 &= M44;
        h1 += c;
    }

    /// <summary>Fully propagate carries so precomputed r^k limbs stay in range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CarryFull(ref ulong h0, ref ulong h1, ref ulong h2)
    {
        ulong c = h1 >> 44;
        h1 &= M44;
        h2 += c;
        c = h2 >> 42;
        h2 &= M42;
        h0 += c * 5UL;
        c = h0 >> 44;
        h0 &= M44;
        h1 += c;
        c = h1 >> 44;
        h1 &= M44;
        h2 += c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(ReadOnlySpan<byte> s)
    {
        ulong v = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(s));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadU64(ref byte p)
    {
        ulong v = Unsafe.ReadUnaligned<ulong>(ref p);
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }
}
