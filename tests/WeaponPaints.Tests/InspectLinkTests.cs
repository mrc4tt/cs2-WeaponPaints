using Xunit;

namespace WeaponPaints.Tests;

public class InspectLinkTests
{
    // Minimal protobuf writer for CEconItemPreviewDataBlock payloads.
    private sealed class Pb
    {
        private readonly List<byte> _b = [];

        public Pb Varint(int field, ulong v) { Tag(field, 0); Raw(v); return this; }
        public Pb Fixed32(int field, float f) { Tag(field, 5); _b.AddRange(BitConverter.GetBytes(f)); return this; }
        public Pb Bytes(int field, byte[] data) { Tag(field, 2); Raw((ulong)data.Length); _b.AddRange(data); return this; }
        public Pb Str(int field, string s) => Bytes(field, System.Text.Encoding.UTF8.GetBytes(s));
        public byte[] ToArray() => [.. _b];

        private void Tag(int field, int wire) => Raw((ulong)((field << 3) | wire));

        private void Raw(ulong v)
        {
            while (v >= 0x80) { _b.Add((byte)(v | 0x80)); v >>= 7; }
            _b.Add((byte)v);
        }
    }

    // 0x00 + protobuf + 4-byte checksum (not validated by the parser), hex-encoded.
    private static string Payload(Pb pb) => Convert.ToHexString([0x00, .. pb.ToArray(), 0, 0, 0, 0]);

    private static string Masked(Pb pb, byte key)
    {
        byte[] plain = [0x00, .. pb.ToArray(), 0, 0, 0, 0];
        return Convert.ToHexString([key, .. plain.Select(b => (byte)(b ^ key))]);
    }

    private static Pb Ak() => new Pb()
        .Varint(3, 7)
        .Varint(4, 44)
        .Varint(7, BitConverter.SingleToUInt32Bits(0.15f))
        .Varint(8, 661);

    [Fact]
    public void DecodesBasicFields()
    {
        var hex = Payload(Ak().Varint(10, 123).Str(11, "Hello"));
        Assert.Equal(InspectParseResult.Ok, InspectItemPreview.TryParse(hex, out var item));
        Assert.Equal(7, item.DefIndex);
        Assert.Equal(44, item.PaintIndex);
        Assert.Equal(661, item.PaintSeed);
        Assert.Equal(0.15f, item.PaintWear);
        Assert.Equal(123, item.KillEaterValue);
        Assert.Equal("Hello", item.CustomName);
    }

    [Fact]
    public void DecodesFixed32Wear()
    {
        var hex = Payload(new Pb().Varint(3, 7).Varint(4, 44).Fixed32(7, 0.07f));
        Assert.Equal(InspectParseResult.Ok, InspectItemPreview.TryParse(hex, out var item));
        Assert.Equal(0.07f, item.PaintWear);
    }

    [Fact]
    public void DecodesStickersAndKeychain()
    {
        var sticker = new Pb().Varint(1, 2).Varint(2, 4321).Fixed32(3, 0.5f).ToArray();
        var charm = new Pb().Varint(1, 0).Varint(2, 36).Varint(10, 9999).Varint(12, 5021).ToArray();
        var hex = Payload(Ak().Bytes(12, sticker).Bytes(20, charm));

        Assert.Equal(InspectParseResult.Ok, InspectItemPreview.TryParse(hex, out var item));
        var s = Assert.Single(item.Stickers);
        Assert.Equal(2, s.Slot);
        Assert.Equal(4321u, s.Id);
        Assert.Equal(0.5f, s.Wear);
        var k = Assert.Single(item.Keychains);
        Assert.Equal(36u, k.Id);
        Assert.Equal(9999u, k.Pattern);
        Assert.Equal(5021u, k.StickerId);
    }

    [Fact]
    public void DecodesSteamLinkWithUrlEncodedPayload()
    {
        var link = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20" + Payload(Ak());
        Assert.Equal(InspectParseResult.Ok, InspectItemPreview.TryParse(link, out var item));
        Assert.Equal(44, item.PaintIndex);
    }

    [Fact]
    public void DecodesXorMaskedPayload()
    {
        Assert.Equal(InspectParseResult.Ok, InspectItemPreview.TryParse(Masked(Ak(), 0xA5), out var item));
        Assert.Equal(7, item.DefIndex);
        Assert.Equal(661, item.PaintSeed);
    }

    [Fact]
    public void OwnedItemLinkIsRecognized()
    {
        var link = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview S76561198000000000A12345678D1234567890";
        Assert.Equal(InspectParseResult.OwnedItemLink, InspectItemPreview.TryParse(link, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("00ABCDEF")]
    [InlineData("0011223344556677889")] // odd length
    public void GarbageIsInvalid(string input)
    {
        Assert.Equal(InspectParseResult.Invalid, InspectItemPreview.TryParse(input, out _));
    }
}

public class GenCodeTests
{
    private static bool Known(int defIndex) => defIndex is 7 or 9 or 60;

    [Fact]
    public void SeedThenWear()
    {
        Assert.True(GenCodeResolver.TryParseNumeric("7 44 661 0.15", Known, out var item));
        Assert.Equal(661, item.PaintSeed);
        Assert.Equal(0.15f, item.PaintWear);
    }

    [Fact]
    public void WearThenSeed()
    {
        Assert.True(GenCodeResolver.TryParseNumeric("7 44 0.15 661", Known, out var item));
        Assert.Equal(661, item.PaintSeed);
        Assert.Equal(0.15f, item.PaintWear);
    }

    [Fact]
    public void ClassicStickerPairsKeepSlotIndex()
    {
        // Pair index == slot, so an empty pair (id 0) must not shift later stickers.
        Assert.True(GenCodeResolver.TryParseNumeric("7 44 661 0.15 0 0 4321 0.5", Known, out var item));
        var s = Assert.Single(item.Stickers);
        Assert.Equal(1, s.Slot);
        Assert.Equal(4321u, s.Id);
    }

    [Theory]
    [InlineData("1234 44 661 0.15")] // unknown defindex
    [InlineData("7 44 661")]         // too short
    [InlineData("7 44 2000 0.15")]   // seed out of range
    [InlineData("7 44 661 1.5")]     // wear out of range
    [InlineData("7 44 abc 0.15")]
    public void RejectsBadCodes(string input)
    {
        Assert.False(GenCodeResolver.TryParseNumeric(input, Known, out _));
    }

    [Fact]
    public void ShareCodeExtraction()
    {
        Assert.True(GenCodeResolver.TryExtractShareCode("123456789 44 661 0.15", out var code));
        Assert.Equal("123456789", code);
        Assert.False(GenCodeResolver.TryExtractShareCode("7 44 661 0.15", out _));
    }
}
