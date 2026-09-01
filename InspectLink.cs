using System.Net;
using System.Text.RegularExpressions;

namespace WeaponPaints;

internal enum InspectParseResult
{
    Ok,

    // Input didn't contain a decodable hex payload.
    Invalid,

    // A steam inspect link in the owned-item form (S<steam64>A<asset>D<d> / M<market>A<asset>D<d>).
    // Those carry no item data — the client asks the Game Coordinator for it — so they cannot be
    // decoded offline. The player has to use a gen code / "unowned" preview link instead.
    OwnedItemLink,
}

// One sticker or keychain block from an inspect payload (CEconItemPreviewDataBlock.Sticker).
internal sealed class InspectSticker
{
    public int Slot;
    public uint Id;
    public float Wear;
    public float Scale = 1f;
    public float Rotation;
    public float OffsetX;
    public float OffsetY;
    public float OffsetZ;
    public uint Pattern; // keychain seed
}

// Decoder for CS2 inspect payloads: the hex-encoded CEconItemPreviewDataBlock protobuf carried by
// steam://rungame/730/.../+csgo_econ_action_preview links and by the gen codes / preview URLs that
// cs2inspects.com, csfloat.com and cs2preview produce. All of those formats reduce to "a hex blob
// somewhere in the string", so extraction is site-agnostic: URL-decode past the marker if present,
// then take the longest plausible hex run. The protobuf reader is hand-rolled — the message is tiny
// and stable, not worth a protobuf dependency.
//
// Payload layout: 0x00, protobuf bytes, 4-byte checksum. Some generators XOR-mask the whole thing
// with the first byte as the key; a leading byte != 0x00 is unmasked before parsing. The checksum
// is not validated — a payload either parses into a sane item or it doesn't, and rejecting on
// checksum would break the (common) generator variants that compute it differently.
internal sealed partial class InspectItemPreview
{
    private const string Marker = "csgo_econ_action_preview";
    private const int MinHexChars = 12;
    private const int MaxPayloadBytes = 4096;

    public int DefIndex;
    public int PaintIndex;
    public int PaintSeed;
    public float PaintWear;
    public int KillEaterValue = -1; // >= 0 => StatTrak with that kill count
    public string? CustomName;
    public List<InspectSticker> Stickers = [];
    public List<InspectSticker> Keychains = [];

    [GeneratedRegex(@"[SM]\d{6,}A\d+D\d+", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedLinkRegex();

    public static InspectParseResult TryParse(string input, out InspectItemPreview item)
    {
        item = new InspectItemPreview();

        var text = Normalize(input);
        if (text.Length == 0)
            return InspectParseResult.Invalid;

        if (OwnedLinkRegex().IsMatch(text))
            return InspectParseResult.OwnedItemLink;

        var hex = ExtractHex(text);
        if (hex == null || hex.Length % 2 != 0 || hex.Length / 2 > MaxPayloadBytes)
            return InspectParseResult.Invalid;

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                return InspectParseResult.Invalid;
        }

        // XOR-masked variant: the first byte is the key, everything after is data ^ key.
        if (bytes[0] != 0x00)
        {
            var key = bytes[0];
            var unmasked = new byte[bytes.Length];
            for (var i = 1; i < bytes.Length; i++)
                unmasked[i] = (byte)(bytes[i] ^ key);

            bytes = unmasked[1] == 0x00 ? unmasked[1..] : unmasked;
        }

        if (bytes.Length < 6 || bytes[0] != 0x00)
            return InspectParseResult.Invalid;

        // Primary form: strip the leading 0x00 and the trailing 4-byte checksum. Fallback for
        // checksum-less generator output: parse everything after the leading 0x00.
        if (TryParseBlock(bytes.AsSpan(1, bytes.Length - 5), item) && item.DefIndex > 0)
            return InspectParseResult.Ok;

        item = new InspectItemPreview();
        if (TryParseBlock(bytes.AsSpan(1), item) && item.DefIndex > 0)
            return InspectParseResult.Ok;

        item = new InspectItemPreview();
        return InspectParseResult.Invalid;
    }

    private static string Normalize(string input)
    {
        var text = input.Trim().Trim('"', '\'');
        var marker = text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            text = WebUtility.UrlDecode(text[(marker + Marker.Length)..]).Trim();
        return text;
    }

    // Longest run of hex digits that is at least MinHexChars long. Site URLs put the payload in a
    // query parameter or path segment, so any surrounding non-hex characters end the run for us.
    private static string? ExtractHex(string text)
    {
        string? best = null;
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && Uri.IsHexDigit(text[i]))
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                var len = i - start;
                if (len >= MinHexChars && (best == null || len > best.Length))
                    best = text.Substring(start, len);
                start = -1;
            }
        }

        return best;
    }

    private static bool TryParseBlock(ReadOnlySpan<byte> data, InspectItemPreview item)
    {
        try
        {
            var pos = 0;
            while (pos < data.Length)
            {
                var tag = ReadVarint(data, ref pos);
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);

                switch (field)
                {
                    case 3:
                        item.DefIndex = (int)ReadVarint(data, ref pos);
                        break;
                    case 4:
                        item.PaintIndex = (int)ReadVarint(data, ref pos);
                        break;
                    case 7:
                        // paintwear is a uint32 whose bits are the IEEE float; some encoders emit
                        // it as a varint, others as fixed32 — accept both.
                        item.PaintWear = wire == 5
                            ? ReadFloat(data, ref pos)
                            : BitConverter.UInt32BitsToSingle((uint)ReadVarint(data, ref pos));
                        break;
                    case 8:
                        item.PaintSeed = (int)ReadVarint(data, ref pos);
                        break;
                    case 10:
                        item.KillEaterValue = (int)ReadVarint(data, ref pos);
                        break;
                    case 11:
                        item.CustomName = ReadString(data, ref pos);
                        break;
                    case 12:
                        if (item.Stickers.Count < 16)
                            item.Stickers.Add(ParseSticker(ReadBytes(data, ref pos)));
                        else
                            ReadBytes(data, ref pos);
                        break;
                    case 20:
                        if (item.Keychains.Count < 4)
                            item.Keychains.Add(ParseSticker(ReadBytes(data, ref pos)));
                        else
                            ReadBytes(data, ref pos);
                        break;
                    default:
                        Skip(data, ref pos, wire);
                        break;
                }
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static InspectSticker ParseSticker(ReadOnlySpan<byte> data)
    {
        var sticker = new InspectSticker();
        var pos = 0;
        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);

            switch (field)
            {
                case 1:
                    sticker.Slot = (int)ReadVarint(data, ref pos);
                    break;
                case 2:
                    sticker.Id = (uint)ReadVarint(data, ref pos);
                    break;
                case 3:
                    sticker.Wear = ReadFloatField(data, ref pos, wire);
                    break;
                case 4:
                    sticker.Scale = ReadFloatField(data, ref pos, wire);
                    break;
                case 5:
                    sticker.Rotation = ReadFloatField(data, ref pos, wire);
                    break;
                case 7:
                    sticker.OffsetX = ReadFloatField(data, ref pos, wire);
                    break;
                case 8:
                    sticker.OffsetY = ReadFloatField(data, ref pos, wire);
                    break;
                case 9:
                    sticker.OffsetZ = ReadFloatField(data, ref pos, wire);
                    break;
                case 10:
                    sticker.Pattern = (uint)ReadVarint(data, ref pos);
                    break;
                default:
                    Skip(data, ref pos, wire);
                    break;
            }
        }

        return sticker;
    }

    private static float ReadFloatField(ReadOnlySpan<byte> data, ref int pos, int wire)
    {
        return wire == 5
            ? ReadFloat(data, ref pos)
            : BitConverter.UInt32BitsToSingle((uint)ReadVarint(data, ref pos));
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        var shift = 0;
        while (pos < data.Length)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 63)
                break;
        }

        throw new FormatException();
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 4 > data.Length)
            throw new FormatException();
        var value = BitConverter.ToSingle(data.Slice(pos, 4));
        pos += 4;
        return value;
    }

    private static ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> data, ref int pos)
    {
        var length = (int)ReadVarint(data, ref pos);
        if (length < 0 || pos + length > data.Length)
            throw new FormatException();
        var span = data.Slice(pos, length);
        pos += length;
        return span;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        return System.Text.Encoding.UTF8.GetString(ReadBytes(data, ref pos));
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0:
                ReadVarint(data, ref pos);
                break;
            case 1:
                if (pos + 8 > data.Length)
                    throw new FormatException();
                pos += 8;
                break;
            case 2:
                ReadBytes(data, ref pos);
                break;
            case 5:
                if (pos + 4 > data.Length)
                    throw new FormatException();
                pos += 4;
                break;
            default:
                throw new FormatException();
        }
    }
}
