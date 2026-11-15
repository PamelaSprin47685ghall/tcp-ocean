using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;

namespace Tbt.Crypto.Kernels;

/// <summary>
/// AEGIS-128L (RFC 10032 / draft-irtf-cfrg-aegis-aead, AEGIS v1.1 Finalize).
/// 128-bit key, 128-bit nonce, 128-bit tag. One instance per key; single owner
/// thread. Encrypt / TryDecrypt allocate nothing. Forged tags never throw;
/// on failure the plaintext buffer is overwritten with zeros.
/// Requires hardware AES (see <see cref="IsSupported"/>); there is no
/// table-based software AES.
/// </summary>
public sealed class Aegis128L
{
    // C0, C1 from RFC 10032 §2 (Fibonacci-ish and its "sqrt(2)" companion).
    private static readonly Vector128<byte> C0 = Vector128.Create(
        (byte)0x00, 0x01, 0x01, 0x02, 0x03, 0x05, 0x08, 0x0d,
        0x15, 0x22, 0x37, 0x59, 0x90, 0xe9, 0x79, 0x62);
    private static readonly Vector128<byte> C1 = Vector128.Create(
        (byte)0xdb, 0x3d, 0x18, 0x55, 0x6d, 0xc2, 0x2f, 0xf1,
        0x20, 0x11, 0x31, 0x42, 0x73, 0xb5, 0x28, 0xdd);

    private Vector128<byte> _key;
    private Vector128<byte> _s0, _s1, _s2, _s3, _s4, _s5, _s6, _s7;
    private readonly byte[] _expected = new byte[16];
    private bool _hasKey;

    public static bool IsSupported => AesHw.IsSupported;

    public const int KeyLength = 16;
    public const int NonceLength = 16;
    public const int TagLength = 16;

    public Aegis128L(ReadOnlySpan<byte> key) => SetKey(key);

    /// <summary>Parameterless constructor for the Update KAT only.</summary>
    public Aegis128L()
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("AEGIS-128L requires hardware AES (AES-NI or ARMv8 Crypto).");
    }

    public void SetKey(ReadOnlySpan<byte> key)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("AEGIS-128L requires hardware AES (AES-NI or ARMv8 Crypto).");
        if (key.Length != KeyLength)
            throw new ArgumentException("AEGIS-128L key is 16 bytes.", nameof(key));
        _key = Vector128.Create(key);
        _hasKey = true;
    }

    public void Wipe()
    {
        _key = default;
        _s0 = _s1 = _s2 = _s3 = _s4 = _s5 = _s6 = _s7 = default;
        Array.Clear(_expected);
        _hasKey = false;
    }

    /// <summary>RFC 10032 Appendix A.1: AESRound(in, rk).</summary>
    public static void AesRound(ReadOnlySpan<byte> input, ReadOnlySpan<byte> roundKey, Span<byte> output)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("AESRound requires hardware AES.");
        if (input.Length < 16 || roundKey.Length < 16 || output.Length < 16)
            throw new ArgumentException("AES block is 16 bytes.");
        AesHw.Round(Vector128.Create(input), Vector128.Create(roundKey)).CopyTo(output);
    }

    /// <summary>
    /// RFC 10032 Appendix A.2.1: one Update(M0, M1) on a 128-byte state (S0..S7).
    /// </summary>
    public static void ApplyUpdate(Span<byte> state, ReadOnlySpan<byte> m0, ReadOnlySpan<byte> m1)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("AEGIS-128L requires hardware AES.");
        if (state.Length != 128 || m0.Length != 16 || m1.Length != 16)
            throw new ArgumentException("AEGIS-128L state is 8 blocks; M0 and M1 are 16 bytes.");
        var a = new Aegis128L();
        a.LoadState(state);
        a.Update(Vector128.Create(m0), Vector128.Create(m1));
        a.StoreState(state);
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plain, Span<byte> cipher, Span<byte> tag, ReadOnlySpan<byte> aad)
    {
        if (nonce.Length != NonceLength || tag.Length != TagLength || cipher.Length != plain.Length)
            throw new ArgumentException("bad nonce, tag or length");
        Init(nonce);
        Absorb(aad);
        Enc(plain, cipher);
        FinalizeTag((ulong)aad.Length * 8UL, (ulong)plain.Length * 8UL).CopyTo(tag);
    }

    /// <summary>
    /// Constant-time tag check. On success, decrypt into <paramref name="plain"/>
    /// (in-place is allowed). On failure, <paramref name="plain"/> is cleared and
    /// the method returns false; nothing is thrown and nothing is allocated.
    /// </summary>
    public bool TryDecrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain, ReadOnlySpan<byte> aad)
    {
        if (nonce.Length != NonceLength || tag.Length != TagLength || cipher.Length != plain.Length)
            return false;
        Init(nonce);
        Absorb(aad);
        Dec(cipher, plain);
        Vector128<byte> expected = FinalizeTag((ulong)aad.Length * 8UL, (ulong)plain.Length * 8UL);
        expected.CopyTo(_expected);
        if (CryptographicOperations.FixedTimeEquals(_expected, tag))
            return true;
        plain.Clear();
        Array.Clear(_expected);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Update(Vector128<byte> m0, Vector128<byte> m1)
    {
        Vector128<byte> s0 = _s0, s1 = _s1, s2 = _s2, s3 = _s3, s4 = _s4, s5 = _s5, s6 = _s6, s7 = _s7;
        _s0 = AesHw.Round(s7, s0 ^ m0);
        _s1 = AesHw.Round(s0, s1);
        _s2 = AesHw.Round(s1, s2);
        _s3 = AesHw.Round(s2, s3);
        _s4 = AesHw.Round(s3, s4 ^ m1);
        _s5 = AesHw.Round(s4, s5);
        _s6 = AesHw.Round(s5, s6);
        _s7 = AesHw.Round(s6, s7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void KeyStream(out Vector128<byte> z0, out Vector128<byte> z1)
    {
        z0 = _s1 ^ _s6 ^ (_s2 & _s3);
        z1 = _s2 ^ _s5 ^ (_s6 & _s7);
    }

    private void Init(ReadOnlySpan<byte> nonce)
    {
        if (!_hasKey)
            throw new InvalidOperationException("AEGIS-128L key is not set.");
        Vector128<byte> k = _key;
        Vector128<byte> n = Vector128.Create(nonce);
        _s0 = k ^ n;
        _s1 = C1;
        _s2 = C0;
        _s3 = C1;
        _s4 = k ^ n;
        _s5 = k ^ C0;
        _s6 = k ^ C1;
        _s7 = k ^ C0;
        for (int i = 0; i < 10; i++)
            Update(n, k);
    }

    private void Absorb(ReadOnlySpan<byte> ad)
    {
        int i = 0;
        int n = ad.Length;
        while (i + 32 <= n)
        {
            Update(Vector128.Create(ad.Slice(i, 16)), Vector128.Create(ad.Slice(i + 16, 16)));
            i += 32;
        }
        if (i < n)
        {
            Span<byte> pad = stackalloc byte[32];
            pad.Clear();
            ad.Slice(i).CopyTo(pad);
            Update(Vector128.Create<byte>(pad), Vector128.Create<byte>(pad.Slice(16)));
        }
    }

    private void Enc(ReadOnlySpan<byte> plain, Span<byte> cipher)
    {
        int i = 0;
        int n = plain.Length;
        while (i + 32 <= n)
        {
            Vector128<byte> t0 = Vector128.Create(plain.Slice(i, 16));
            Vector128<byte> t1 = Vector128.Create(plain.Slice(i + 16, 16));
            KeyStream(out Vector128<byte> z0, out Vector128<byte> z1);
            (t0 ^ z0).CopyTo(cipher.Slice(i, 16));
            (t1 ^ z1).CopyTo(cipher.Slice(i + 16, 16));
            Update(t0, t1);
            i += 32;
        }
        if (i < n)
        {
            Span<byte> pad = stackalloc byte[32];
            pad.Clear();
            plain.Slice(i).CopyTo(pad);
            Vector128<byte> t0 = Vector128.Create<byte>(pad);
            Vector128<byte> t1 = Vector128.Create<byte>(pad.Slice(16));
            KeyStream(out Vector128<byte> z0, out Vector128<byte> z1);
            (t0 ^ z0).CopyTo(pad);
            (t1 ^ z1).CopyTo(pad.Slice(16));
            pad.Slice(0, n - i).CopyTo(cipher.Slice(i));
            Update(t0, t1);
        }
    }

    private void Dec(ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        int i = 0;
        int n = cipher.Length;
        while (i + 32 <= n)
        {
            Vector128<byte> t0 = Vector128.Create(cipher.Slice(i, 16));
            Vector128<byte> t1 = Vector128.Create(cipher.Slice(i + 16, 16));
            KeyStream(out Vector128<byte> z0, out Vector128<byte> z1);
            Vector128<byte> p0 = t0 ^ z0;
            Vector128<byte> p1 = t1 ^ z1;
            p0.CopyTo(plain.Slice(i, 16));
            p1.CopyTo(plain.Slice(i + 16, 16));
            Update(p0, p1);
            i += 32;
        }
        if (i < n)
        {
            // DecPartial: xor the zero-padded ciphertext, truncate, then Update
            // with the zero-padded plaintext (RFC 10032 §3.8).
            int rest = n - i;
            Span<byte> cpad = stackalloc byte[32];
            cpad.Clear();
            cipher.Slice(i).CopyTo(cpad);
            KeyStream(out Vector128<byte> z0, out Vector128<byte> z1);
            (Vector128.Create<byte>(cpad) ^ z0).CopyTo(cpad);
            (Vector128.Create<byte>(cpad.Slice(16)) ^ z1).CopyTo(cpad.Slice(16));
            cpad.Slice(rest).Clear();
            Update(Vector128.Create<byte>(cpad), Vector128.Create<byte>(cpad.Slice(16)));
            cpad.Slice(0, rest).CopyTo(plain.Slice(i));
        }
    }

    private Vector128<byte> FinalizeTag(ulong adLenBits, ulong msgLenBits)
    {
        Vector128<byte> t = _s2 ^ Vector128.Create(adLenBits, msgLenBits).AsByte();
        for (int i = 0; i < 7; i++)
            Update(t, t);
        // 128-bit tag: S0 XOR S1 XOR S2 XOR S3 XOR S4 XOR S5 XOR S6 (not S7).
        return _s0 ^ _s1 ^ _s2 ^ _s3 ^ _s4 ^ _s5 ^ _s6;
    }

    private void LoadState(ReadOnlySpan<byte> state)
    {
        _s0 = Vector128.Create(state.Slice(0, 16));
        _s1 = Vector128.Create(state.Slice(16, 16));
        _s2 = Vector128.Create(state.Slice(32, 16));
        _s3 = Vector128.Create(state.Slice(48, 16));
        _s4 = Vector128.Create(state.Slice(64, 16));
        _s5 = Vector128.Create(state.Slice(80, 16));
        _s6 = Vector128.Create(state.Slice(96, 16));
        _s7 = Vector128.Create(state.Slice(112, 16));
    }

    private void StoreState(Span<byte> state)
    {
        _s0.CopyTo(state.Slice(0, 16));
        _s1.CopyTo(state.Slice(16, 16));
        _s2.CopyTo(state.Slice(32, 16));
        _s3.CopyTo(state.Slice(48, 16));
        _s4.CopyTo(state.Slice(64, 16));
        _s5.CopyTo(state.Slice(80, 16));
        _s6.CopyTo(state.Slice(96, 16));
        _s7.CopyTo(state.Slice(112, 16));
    }
}
