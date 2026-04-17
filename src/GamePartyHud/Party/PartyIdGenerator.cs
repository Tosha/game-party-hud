using System;
using System.Security.Cryptography;

namespace GamePartyHud.Party;

/// <summary>Generates shareable party IDs — short, case-insensitive, unambiguous.</summary>
public static class PartyIdGenerator
{
    // 0/O/1/I are omitted because they're easy to confuse when read aloud or typed.
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate(int length = 6)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        var chars = new char[length];
        Span<byte> buf = stackalloc byte[length];
        RandomNumberGenerator.Fill(buf);
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[buf[i] % Alphabet.Length];
        return new string(chars);
    }
}
