namespace WeaponPaints
{
    public class WeaponInfo
    {
        public int Paint { get; set; }
        public int Seed { get; set; }
        public float Wear { get; set; }
        public string Nametag { get; set; } = "";
        public bool StatTrak { get; set; }
        public int StatTrakCount { get; set; }
        public KeyChainInfo? KeyChain { get; set; }
        public List<StickerInfo> Stickers { get; set; } = new();
    }

    public class StickerInfo
    {
        public uint Id { get; set; }
        public uint Schema { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float Wear { get; set; }
        public float Scale { get; set; }
        public float Rotation { get; set; }
    }

    public class KeyChainInfo
    {
        public uint Id { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public uint Seed { get; set; }
        public float Rotation { get; set; }
    }

    // Snapshot of CEconItemView fields read off pawn.EconGloves before any custom glove
    // override. Restored verbatim when the player picks "None" in the glove menu so the
    // server-assigned default gloves come back without a respawn.
    internal sealed class NativeGloveSnapshot
    {
        internal ushort ItemDefinitionIndex { get; init; }
        internal int EntityQuality { get; init; }
        internal uint EntityLevel { get; init; }
        internal ulong ItemID { get; init; }
        internal uint ItemIDHigh { get; init; }
        internal uint ItemIDLow { get; init; }
        internal uint AccountID { get; init; }
        internal uint InventoryPosition { get; init; }
        internal bool Initialized { get; init; }
        internal string CustomName { get; init; } = string.Empty;
        internal string CustomNameOverride { get; init; } = string.Empty;
    }
}
