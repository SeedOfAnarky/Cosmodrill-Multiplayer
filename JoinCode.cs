using System.Net;

namespace CosmodrillMultiplayer;

internal static class JoinCode
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(IPAddress address, ushort port)
    {
        byte[] ip = address.MapToIPv4().GetAddressBytes();
        byte[] data = { ip[0], ip[1], ip[2], ip[3], (byte)(port >> 8), (byte)port };
        string result = Encode58(data);
        return result.PadLeft(9, '1');
    }

    public static bool TryDecode(string code, out IPAddress address, out ushort port, out string error)
    {
        address = null; port = 0; error = "";
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length != 9) { error = "Join codes contain exactly 9 characters."; return false; }
        try
        {
            byte[] decoded = Decode58(code.Trim());
            byte[] data = new byte[6];
            if (decoded.Length > 6)
            {
                int extra = decoded.Length - 6;
                for (int i = 0; i < extra; i++)
                    if (decoded[i] != 0) { error = "Join code is too large."; return false; }
                byte[] trimmed = new byte[6];
                Buffer.BlockCopy(decoded, extra, trimmed, 0, 6);
                decoded = trimmed;
            }
            Buffer.BlockCopy(decoded, 0, data, 6 - decoded.Length, decoded.Length);
            port = (ushort)((data[4] << 8) | data[5]);
            if (port == 0) { error = "Join code contains an invalid port."; return false; }
            address = new IPAddress(new[] { data[0], data[1], data[2], data[3] });
            return true;
        }
        catch (Exception ex) { error = "Invalid join code: " + ex.Message; return false; }
    }

    private static string Encode58(byte[] input)
    {
        byte[] number = (byte[])input.Clone();
        int zeros = 0; while (zeros < number.Length && number[zeros] == 0) zeros++;
        var chars = new List<char>();
        int start = zeros;
        while (start < number.Length)
        {
            int remainder = 0;
            for (int i = start; i < number.Length; i++) { int value = remainder * 256 + number[i]; number[i] = (byte)(value / 58); remainder = value % 58; }
            chars.Add(Alphabet[remainder]);
            while (start < number.Length && number[start] == 0) start++;
        }
        for (int i = 0; i < zeros; i++) chars.Add('1');
        chars.Reverse(); return new string(chars.ToArray());
    }

    private static byte[] Decode58(string input)
    {
        var bytes = new List<byte> { 0 };
        foreach (char c in input)
        {
            int digit = Alphabet.IndexOf(c); if (digit < 0) throw new FormatException("Unsupported character '" + c + "'.");
            int carry = digit;
            for (int i = bytes.Count - 1; i >= 0; i--) { int value = bytes[i] * 58 + carry; bytes[i] = (byte)value; carry = value >> 8; }
            while (carry > 0) { bytes.Insert(0, (byte)carry); carry >>= 8; }
        }
        int leading = 0; while (leading < input.Length && input[leading] == '1') leading++;
        while (bytes.Count > 1 && bytes[0] == 0) bytes.RemoveAt(0);
        for (int i = 0; i < leading; i++) bytes.Insert(0, 0);
        return bytes.ToArray();
    }
}
