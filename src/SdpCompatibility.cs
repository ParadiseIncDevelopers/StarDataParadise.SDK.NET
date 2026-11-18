using System;
using System.Security.Cryptography;

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property)]
    internal sealed class AllowNullAttribute : Attribute { }
}
#endif

namespace StarDataParadise.SDK
{
    internal static class SdpCompatibility
    {
        private const string HexDigits = "0123456789abcdef";

        internal static string Sha256Hex(byte[] value)
        {
#if NETFRAMEWORK
            using var hash = SHA256.Create();
            return ToHex(hash.ComputeHash(value));
#else
            return ToHex(SHA256.HashData(value));
#endif
        }

        internal static string ToHex(ReadOnlySpan<byte> value)
        {
#if NETFRAMEWORK
            var chars = new char[checked(value.Length * 2)];
            for (var i = 0; i < value.Length; i++)
            {
                chars[i * 2] = HexDigits[value[i] >> 4];
                chars[i * 2 + 1] = HexDigits[value[i] & 15];
            }
            return new string(chars);
#else
            return Convert.ToHexString(value).ToLowerInvariant();
#endif
        }

        internal static byte[] FromHex(string text)
        {
#if NETFRAMEWORK
            if (text.Length % 2 != 0) throw new FormatException("Expected an even number of hexadecimal digits.");
            var bytes = new byte[text.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var upper = HexDigits.IndexOf(text[i * 2]);
                var lower = HexDigits.IndexOf(text[i * 2 + 1]);
                if (upper < 0 || lower < 0) throw new FormatException("Invalid hexadecimal digit.");
                bytes[i] = (byte)((upper << 4) | lower);
            }
            return bytes;
#else
            return Convert.FromHexString(text);
#endif
        }
    }
}
