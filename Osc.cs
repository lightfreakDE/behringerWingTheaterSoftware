using System.Buffers.Binary;
using System.Text;

namespace WingScenes;

/// <summary>Minimaler OSC-1.0-Encoder/Decoder (Typen i, f, s).</summary>
public static class Osc
{
    public static byte[] Encode(string address, params object[] args)
    {
        var ms = new MemoryStream();
        WriteString(ms, address);
        var tags = new StringBuilder(",");
        foreach (var a in args)
            tags.Append(a switch { int => 'i', float => 'f', string => 's', _ => throw new ArgumentException("OSC-Typ nicht unterstützt: " + a.GetType()) });
        WriteString(ms, tags.ToString());
        foreach (var a in args)
        {
            switch (a)
            {
                case int i: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, i); ms.Write(b); break; }
                case float f: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, f); ms.Write(b); break; }
                case string s: WriteString(ms, s); break;
            }
        }
        return ms.ToArray();
    }

    static void WriteString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        ms.Write(bytes);
        int pad = 4 - bytes.Length % 4; // immer mindestens ein NUL
        for (int i = 0; i < pad; i++) ms.WriteByte(0);
    }

    public static (string Address, object[] Args)? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            int pos = 0;
            var address = ReadString(data, ref pos);
            if (address.Length == 0 || address[0] != '/') return null;
            if (pos >= data.Length) return (address, Array.Empty<object>());
            var tags = ReadString(data, ref pos);
            var args = new List<object>();
            if (tags.StartsWith(','))
            {
                foreach (var t in tags.AsSpan(1))
                {
                    switch (t)
                    {
                        case 'i': args.Add(BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4))); pos += 4; break;
                        case 'f': args.Add(BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4))); pos += 4; break;
                        case 's': args.Add(ReadString(data, ref pos)); break;
                        default: return (address, args.ToArray()); // unbekannt: abbrechen
                    }
                }
            }
            return (address, args.ToArray());
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        int end = pos;
        while (end < data.Length && data[end] != 0) end++;
        var s = Encoding.UTF8.GetString(data[pos..end]);
        pos = (end + 4) & ~3; // inkl. NUL auf 4 Byte aufrunden
        return s;
    }
}
