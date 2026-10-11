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

        // Deep copy, taken on the game thread before handing the row to a database write so the
        // background task never reads state the game thread is still changing.
        public WeaponInfo Clone() => new()
        {
            Paint = Paint,
            Seed = Seed,
            Wear = Wear,
            Nametag = Nametag,
            StatTrak = StatTrak,
            StatTrakCount = StatTrakCount,
            KeyChain = KeyChain?.Clone(),
            Stickers = Stickers.Select(s => s.Clone()).ToList(),
        };
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

        public StickerInfo Clone() => (StickerInfo)MemberwiseClone();
    }

    public class KeyChainInfo
    {
        public uint Id { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public uint Seed { get; set; }
        public float Rotation { get; set; }

        // Sticker-slab charms carry the sticker drawn on the slab; highlight charms carry the
        // highlight reel id. 0 = none. Inspect-link fields 12 / 11.
        public uint Sticker { get; set; }
        public uint Highlight { get; set; }

        public KeyChainInfo Clone() => (KeyChainInfo)MemberwiseClone();
    }

    // A player's chicken pet, exactly as wp_player_pets stores it. See Pets.cs.
    public class PetInfo
    {
        // pet_definitions id: 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish.
        public int PetId { get; set; }

        // The "upgrade level" attribute: 0 egg, 1 chick, 2 pullet, 3 hen.
        public int Stage { get; set; } = WeaponPaints.PetStageHen;

        // The item's style: the material group (colour) the chicken is drawn with, by index.
        // Null = no style, which draws the model's default group. The seed never picks the colour.
        public int? Variant { get; set; }

        // The "pet seed" attribute: body shape and colour jitter, rolled by the client.
        public uint Seed { get; set; }

        // Name tag for the current stage, or null.
        public string? Name { get; set; }
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
