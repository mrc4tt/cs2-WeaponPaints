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

// Numeric gen-code support, layered on top of the hex parser above:
//
//  - Classic space-separated codes ("!gen <defindex> <paint> <seed> <wear> [stickerId wear]*",
//    also tolerated with wear before seed) are decoded fully offline.
//  - cs2inspects.com share codes (a single large numeric id, e.g. "2002300386" — also the first
//    token of the site's longer display string) carry no item data at all; they are resolved
//    through the public api.cs2inspects.com getGenCode endpoint, whose response contains a normal
//    inspect link that the hex parser then decodes.
internal static class GenCodeResolver
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Classic numeric gen code. Returns false when the input isn't fully numeric or the first
    // token isn't a defindex the caller recognizes (so share codes fall through to the resolver).
    public static bool TryParseNumeric(string input, Func<int, bool> isKnownDefIndex, out InspectItemPreview item)
    {
        item = new InspectItemPreview();

        var tokens = Tokenize(input, out var extendedFormat);
        if (tokens.Length < 4)
            return false;

        foreach (var token in tokens)
        {
            if (!float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;
        }

        if (!int.TryParse(tokens[0], out var defIndex) || !isKnownDefIndex(defIndex))
            return false;
        if (!int.TryParse(tokens[1], out var paint) || paint < 0)
            return false;

        // Header order varies by generator: "<seed> <wear>" (OpenGen-style) vs "<wear> <seed>"
        // (cs2locker-style). A decimal point marks the wear; two bare ints default to seed-first.
        var third = tokens[2];
        var fourth = tokens[3];
        string seedToken, wearToken;
        if (third.Contains('.') && !fourth.Contains('.'))
            (wearToken, seedToken) = (third, fourth);
        else
            (seedToken, wearToken) = (third, fourth);

        if (!int.TryParse(seedToken, out var seed) || seed < 0 || seed > 1000)
            return false;
        if (!float.TryParse(wearToken, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wear) || wear < 0f || wear > 1f)
            return false;

        item.DefIndex = defIndex;
        item.PaintIndex = paint;
        item.PaintSeed = seed;
        item.PaintWear = wear;

        // Stickers. Classic: "<id> <wear>" pairs, pair index == slot. Extended "!gens":
        // "<slot> <id> <wear> <x> <y> <rotation>" six-token groups.
        var pos = 4;
        var pairSlot = 0;
        while (pos < tokens.Length)
        {
            if (extendedFormat)
            {
                if (pos + 6 > tokens.Length)
                    break;
                if (int.TryParse(tokens[pos], out var slot)
                    && int.TryParse(tokens[pos + 1], out var id)
                    && float.TryParse(tokens[pos + 2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stickerWear)
                    && id > 0 && slot is >= 0 and <= 4)
                {
                    item.Stickers.Add(new InspectSticker { Slot = slot, Id = (uint)id, Wear = stickerWear });
                }
                pos += 6;
            }
            else
            {
                if (pos + 2 > tokens.Length || pairSlot > 4)
                    break;
                if (int.TryParse(tokens[pos], out var id)
                    && float.TryParse(tokens[pos + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stickerWear)
                    && id > 0)
                {
                    item.Stickers.Add(new InspectSticker { Slot = pairSlot, Id = (uint)id, Wear = stickerWear });
                }
                pairSlot++;
                pos += 2;
            }
        }

        // Trailing tokens (StatTrak count, nametag flags) vary per generator and are ignored.
        return true;
    }

    // A cs2inspects share code: the whole input is numeric and the first token is a pure-digit id
    // too large to be a defindex. The rest of the site's display string (paint/seed/wear/stickers)
    // is redundant — the id alone resolves the item.
    public static bool TryExtractShareCode(string input, out string shareCode)
    {
        shareCode = "";

        var tokens = Tokenize(input, out _);
        if (tokens.Length == 0)
            return false;

        var first = tokens[0];
        if (first.Length is < 6 or > 16 || !first.All(char.IsDigit))
            return false;
        if (!ulong.TryParse(first, out var value) || value <= 65535)
            return false;

        foreach (var token in tokens)
        {
            if (!float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                return false;
        }

        shareCode = first;
        return true;
    }

    // Resolves a share code via api.cs2inspects.com and returns the inspect-link text out of the
    // response (null on any failure). Runs on a background thread — never call from the main
    // thread, and marshal back via Server.NextFrame before touching entities with the result.
    public static async Task<string?> ResolveShareCodeAsync(string shareCode)
    {
        try
        {
            var url = "https://api.cs2inspects.com/getGenCode?url=" + Uri.EscapeDataString(shareCode);
            using var response = await Http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            if (doc.RootElement.TryGetProperty("InspectLinkConsole", out var console) && console.GetString() is { Length: > 0 } consoleLink)
                return consoleLink;
            if (doc.RootElement.TryGetProperty("InspectLink", out var link) && link.GetString() is { Length: > 0 } steamLink)
                return steamLink;

            return null;
        }
        catch (Exception e)
        {
            Utility.Log($"Gen share-code lookup failed: {e.Message}");
            return null;
        }
    }

    private static string[] Tokenize(string input, out bool extendedFormat)
    {
        var text = input.Trim().Trim('"', '\'');
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        extendedFormat = false;
        if (tokens.Length > 0)
        {
            var head = tokens[0].TrimStart('!');
            if (head.Equals("gens", StringComparison.OrdinalIgnoreCase))
            {
                extendedFormat = true;
                tokens = tokens[1..];
            }
            else if (head.Equals("gen", StringComparison.OrdinalIgnoreCase) || head.Equals("g", StringComparison.OrdinalIgnoreCase))
            {
                tokens = tokens[1..];
            }
        }

        return tokens;
    }
}
