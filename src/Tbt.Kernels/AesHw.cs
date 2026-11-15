using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using ArmAes = System.Runtime.Intrinsics.Arm.Aes;
using X86Aes = System.Runtime.Intrinsics.X86.Aes;

namespace Tbt.Crypto.Kernels;

/// <summary>
/// Hardware AES primitives only (x86 AES-NI or ARMv8 Crypto). No table S-box,
/// no software AES. Missing hardware is a hard refusal: callers must not
/// advertise or select AEGIS-128L / AES-128-ECB header protection.
///
/// AESRound(in, rk) follows RFC 10032: one AES encryption round,
/// MixColumns(ShiftRows(SubBytes(in))) XOR rk, i.e. AESENC on x86.
/// On ARM that is AESMC(AESE(in, 0)) XOR rk (AESE includes AddRoundKey).
/// </summary>
internal static class AesHw
{
    public static bool IsSupported => X86Aes.IsSupported || ArmAes.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> Round(Vector128<byte> input, Vector128<byte> roundKey)
    {
        if (X86Aes.IsSupported)
            return X86Aes.Encrypt(input, roundKey);
        // AESE(in, 0) = ShiftRows(SubBytes(in)); AESMC applies MixColumns.
        return ArmAes.MixColumns(ArmAes.Encrypt(input, Vector128<byte>.Zero)) ^ roundKey;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> EncryptLast(Vector128<byte> input, Vector128<byte> roundKey)
    {
        if (X86Aes.IsSupported)
            return X86Aes.EncryptLast(input, roundKey);
        // Final AES round has no MixColumns: AESE(in, 0) XOR rk.
        return ArmAes.Encrypt(input, Vector128<byte>.Zero) ^ roundKey;
    }

    /// <summary>
    /// SubBytes on the four column-0-equivalent positions (0, 4, 8, 12), which
    /// ShiftRows leaves in place. Used only for the AES-128 key schedule.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> SubBytesOnColumns(Vector128<byte> arranged)
    {
        if (X86Aes.IsSupported)
            return X86Aes.EncryptLast(arranged, Vector128<byte>.Zero);
        return ArmAes.Encrypt(arranged, Vector128<byte>.Zero);
    }

    // rcon for AES-128 key expansion, rounds 1..10.
    private static ReadOnlySpan<byte> Rcon => [0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36];

    /// <summary>Expand a 16-byte AES-128 key into 11 round keys. Hardware AES required.</summary>
    public static void ExpandAes128(ReadOnlySpan<byte> key, Span<Vector128<byte>> roundKeys)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("AES-128 requires hardware AES (AES-NI or ARMv8 Crypto).");
        if (key.Length != 16)
            throw new ArgumentException("AES-128 key is 16 bytes.", nameof(key));
        if (roundKeys.Length < 11)
            throw new ArgumentException("AES-128 needs 11 round keys.", nameof(roundKeys));

        roundKeys[0] = Vector128.Create(key);
        Span<byte> prev = stackalloc byte[16];
        Span<byte> next = stackalloc byte[16];
        for (int r = 0; r < 10; r++)
        {
            roundKeys[r].CopyTo(prev);
            // RotWord of the last word, then SubWord via the AES S-box (no tables).
            Vector128<byte> arranged = Vector128.Create(
                prev[13], (byte)0, (byte)0, (byte)0,
                prev[14], (byte)0, (byte)0, (byte)0,
                prev[15], (byte)0, (byte)0, (byte)0,
                prev[12], (byte)0, (byte)0, (byte)0);
            Vector128<byte> sub = SubBytesOnColumns(arranged);
            uint temp =
                (uint)(sub.GetElement(0) ^ Rcon[r])
                | ((uint)sub.GetElement(4) << 8)
                | ((uint)sub.GetElement(8) << 16)
                | ((uint)sub.GetElement(12) << 24);
            uint k0 = BinaryPrimitives.ReadUInt32LittleEndian(prev) ^ temp;
            uint k1 = BinaryPrimitives.ReadUInt32LittleEndian(prev.Slice(4)) ^ k0;
            uint k2 = BinaryPrimitives.ReadUInt32LittleEndian(prev.Slice(8)) ^ k1;
            uint k3 = BinaryPrimitives.ReadUInt32LittleEndian(prev.Slice(12)) ^ k2;
            BinaryPrimitives.WriteUInt32LittleEndian(next, k0);
            BinaryPrimitives.WriteUInt32LittleEndian(next.Slice(4), k1);
            BinaryPrimitives.WriteUInt32LittleEndian(next.Slice(8), k2);
            BinaryPrimitives.WriteUInt32LittleEndian(next.Slice(12), k3);
            roundKeys[r + 1] = Vector128.Create<byte>(next);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> EncryptAes128(ReadOnlySpan<Vector128<byte>> rk, Vector128<byte> block)
    {
        Vector128<byte> s = block ^ rk[0];
        s = Round(s, rk[1]);
        s = Round(s, rk[2]);
        s = Round(s, rk[3]);
        s = Round(s, rk[4]);
        s = Round(s, rk[5]);
        s = Round(s, rk[6]);
        s = Round(s, rk[7]);
        s = Round(s, rk[8]);
        s = Round(s, rk[9]);
        return EncryptLast(s, rk[10]);
    }
}

/// <summary>
/// AES-128-ECB of one 16-byte block (spec 6.4.1 header protection for suite 0x01).
/// The schedule is computed once per header-protection key.
/// </summary>
public sealed class Aes128Ecb
{
    private readonly Vector128<byte>[] _rk = new Vector128<byte>[11];

    public static bool IsSupported => AesHw.IsSupported;

    public Aes128Ecb(ReadOnlySpan<byte> key)
    {
        AesHw.ExpandAes128(key, _rk);
    }

    public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length < 16 || output.Length < 16)
            throw new ArgumentException("AES-128 block is 16 bytes.");
        AesHw.EncryptAes128(_rk, Vector128.Create(input)).CopyTo(output);
    }

    /// <summary>Encrypt a 16-byte sample and write the first 8 bytes of the block.</summary>
    public void EncryptFirst8(ReadOnlySpan<byte> sample, Span<byte> mask)
    {
        if (sample.Length < 16 || mask.Length < 8)
            throw new ArgumentException("HP sample is 16 bytes; mask is 8 bytes.");
        Span<byte> block = stackalloc byte[16];
        AesHw.EncryptAes128(_rk, Vector128.Create(sample)).CopyTo(block);
        block.Slice(0, 8).CopyTo(mask);
    }

    public void Wipe()
    {
        Array.Clear(_rk);
    }
}
