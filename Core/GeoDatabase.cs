using System.Text;

namespace Kot.Core;

// Wire/schema validation for the pinned Xray common/geodata/geodat.proto.
// A checksum only proves byte integrity; it must never replace this check.
static class GeoDatabase
{
    static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool Valid(string path, bool ip)
    {
        try
        {
            var input = new Reader(File.ReadAllBytes(path));
            int entries = 0;
            while (input.Next(out int field, out int wire))
            {
                if (field == 1) { Entry(input.Bytes(wire), ip); entries++; }
                else input.Skip(wire);
            }
            return entries > 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException) { return false; }
    }
    static void Entry(ReadOnlySpan<byte> bytes, bool ip)
    {
        var input = new Reader(bytes); bool code = false;
        while (input.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: code = input.Text(wire); break;
                case 2: if (ip) Cidr(input.Bytes(wire)); else Domain(input.Bytes(wire)); break;
                case 3 when ip: input.Number(wire); break;
                default: input.Skip(wire); break;
            }
        }
        if (!code) throw new InvalidDataException();
    }
    static void Cidr(ReadOnlySpan<byte> bytes)
    {
        var input = new Reader(bytes); int size = 0; ulong prefix = 0;
        while (input.Next(out int field, out int wire))
        {
            if (field == 1) size = input.Bytes(wire).Length;
            else if (field == 2) prefix = input.Number(wire);
            else input.Skip(wire);
        }
        if (size is not (4 or 16) || prefix > (ulong)(size * 8)) throw new InvalidDataException();
    }
    static void Domain(ReadOnlySpan<byte> bytes)
    {
        var input = new Reader(bytes); bool value = false;
        while (input.Next(out int field, out int wire))
        {
            switch (field)
            {
                case 1: if (input.Number(wire) > 3) throw new InvalidDataException(); break;
                case 2: value = input.Text(wire); break;
                case 3: Attribute(input.Bytes(wire)); break;
                default: input.Skip(wire); break;
            }
        }
        if (!value) throw new InvalidDataException();
    }
    static void Attribute(ReadOnlySpan<byte> bytes)
    {
        var input = new Reader(bytes);
        while (input.Next(out int field, out int wire))
        {
            if (field == 1) input.Text(wire);
            else if (field is 2 or 3) input.Number(wire);
            else input.Skip(wire);
        }
    }
    ref struct Reader(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> remaining = data;
        public bool Next(out int field, out int wire)
        {
            field = wire = 0; if (remaining.IsEmpty) return false;
            ulong tag = Varint();
            if (tag >> 3 is 0 or > 536870911) throw new InvalidDataException();
            field = (int)(tag >> 3); wire = (int)(tag & 7); return true;
        }
        ulong Varint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (remaining.IsEmpty) throw new InvalidDataException();
                byte part = remaining[0]; remaining = remaining[1..];
                if (shift == 63 && part > 1) throw new InvalidDataException();
                value |= (ulong)(part & 127) << shift;
                if (part < 128) return value;
            }
            throw new InvalidDataException();
        }
        void Consume(int length)
        {
            if (length > remaining.Length) throw new InvalidDataException();
            remaining = remaining[length..];
        }
        public ulong Number(int wire)
        {
            if (wire != 0) throw new InvalidDataException();
            return Varint();
        }
        public ReadOnlySpan<byte> Bytes(int wire)
        {
            if (wire != 2) throw new InvalidDataException();
            ulong length = Varint();
            if (length > (ulong)remaining.Length) throw new InvalidDataException();
            var value = remaining[..(int)length]; Consume((int)length); return value;
        }
        public bool Text(int wire)
        {
            var value = Bytes(wire); Utf8.GetCharCount(value); return !value.IsEmpty;
        }
        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Consume(8); break;
                case 2: Bytes(wire); break;
                case 5: Consume(4); break;
                default: throw new InvalidDataException();
            }
        }
    }
}
