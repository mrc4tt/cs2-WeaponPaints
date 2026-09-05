namespace WeaponPaints
{
    /// <summary>
    /// Which authored <c>StickerMarkup</c> home the FIFTH sticker hangs off, and how far from it to sit.
    /// </summary>
    internal readonly struct StickerAnchor
    {
        internal StickerAnchor(uint anchor, float dx, float dy)
        {
            Anchor = anchor;
            Dx = dx;
            Dy = dy;
        }

        /// <summary>The authored home to hang the slot off. Never 0 - see <see cref="StickerAnchors"/>.</summary>
        internal uint Anchor { get; }

        /// <summary>The derived fifth home minus the anchor home, in <c>g_vStickerNOffset</c> UV space.</summary>
        internal float Dx { get; }

        internal float Dy { get; }
    }

    /// <summary>
    /// The fifth sticker has nowhere to be drawn on 29 of the 69 weapon+mesh variants; this table says
    /// where the plugin puts it instead.
    ///
    /// A sticker slot's home is the weapon MODEL's, and a weapon does not have one per slot. Read out of
    /// the live game files:
    ///
    ///   - <c>weapon_rif_ak47.vmdl_c</c> authors FOUR <c>StickerMarkup</c> homes, Index 0..3, on both
    ///     <c>body_hd</c> and <c>body_legacy</c>. There is no home 4.
    ///   - <c>weapon_rif_ak47.vmat_c</c> bakes <c>g_vSticker4Offset [0 0]</c> and <c>g_vSticker4Scale
    ///     [0 0]</c>, and a zero scale is the pixel shader's SKIP, not a size
    ///     (<c>csgo_weapon_vulkan_50_ps</c> breaks out of the slot when <c>g_vStickerNScale.x</c> or
    ///     <c>.y</c> is 0).
    ///   - so slot 4 anchored to home 4 points at a home the AK has not got, and the fifth sticker draws
    ///     nothing at all.
    ///
    /// Verified in game (upstream SkinHubgg/WeaponPaints 79d2fe1) on a live AK-47:
    ///
    ///     weapon_sticker_4 = '60;0;0;0;0;1;0'          -> NOTHING renders
    ///     weapon_sticker_4 = '60;1;0.147;0.029;0;1;0'  -> the sticker RENDERS
    ///
    /// and <c>weapon_ak47</c>'s hd row below is that pair.
    ///
    /// A row that names its own anchor still wins: <c>SetStickers</c> only reaches this table when the
    /// row's <c>schema</c> is 0. Rows written by a site that already does the shift itself carry a
    /// non-zero anchor and pre-shifted offsets, and the substitution below would double-shift them.
    ///
    /// The table is GENERATED from the weapon markup SkinHub's 3D viewer renders from and the
    /// fifth-slot home it derives for a weapon that authors none (<c>deriveFifthSlot</c> in
    /// <c>stickerSlots.ts</c>; transcribed from <c>src/stickerAnchors.ts</c> in <c>@skinhub/cdn</c>).
    /// Re-run that generator after a CS2 update moves a weapon's authored homes; do not edit a number
    /// here by hand.
    ///
    /// Which home the rule picks: candidates are the authored homes 1..3 (home 0 cannot be named because
    /// 0 means "unset"). Prefer the home whose authored SCALE equals the size the fifth sticker should
    /// draw at, then whose ROTATION matches, then the nearest. Scale is the criterion because a
    /// WeaponPaints sticker's own <c>scale</c> field is not read as a size by the engine - the anchor
    /// home's authored scale is what the sticker draws at, and POSITION is what the offsets can move.
    /// 28 of the 29 borrow a home of exactly the right size; the Galil's legacy mesh is 2.5% out.
    ///
    /// A variant that authors its own fifth home is ABSENT from this table, and the absence is load
    /// bearing: 40 of the 69 variants author all five homes and work today, and no entry means
    /// <see cref="For"/> returns null so the slot keeps using its own index. The AWP is the shape to
    /// keep in mind: its LEGACY mesh authors a real fifth home and its hd mesh does not, so only
    /// <c>hd</c> is listed for it.
    /// </summary>
    internal static class StickerAnchors
    {
        /// <summary>The slot this is for. Slots 0-3 are authored on every sticker-capable weapon.</summary>
        internal const int FifthStickerSlot = 4;

        /// <summary>
        /// Keyed by weapon classname, then by mesh variant, because each variant binds its own material and
        /// its own markup - the two disagree by a few thousandths of a uv and by about 8% of scale, and on
        /// five weapons they do not even want the same anchor.
        /// </summary>
        private static readonly Dictionary<string, (StickerAnchor? Hd, StickerAnchor? Legacy)> Table = new()
        {
            { "weapon_ak47", (new StickerAnchor(1, 0.14699425f, 0.028994253f), new StickerAnchor(1, 0.12881461f, 0.03781461f)) },
            { "weapon_awp", (new StickerAnchor(1, 0.26138917f, 0.041389152f), null) },
            { "weapon_bizon", (new StickerAnchor(1, -0.16905054f, -0.00005054744f), new StickerAnchor(1, 0.11569809f, 0.054698095f)) },
            { "weapon_deagle", (null, new StickerAnchor(1, 0.13099661f, 0.031996623f)) },
            { "weapon_elite", (new StickerAnchor(2, -0.017009478f, 0.09099052f), new StickerAnchor(2, -0.034058955f, 0.06794105f)) },
            // legacy borrows a home authored 12.5 against the 12.2 the fifth wants - the one variant of the
            // 29 that does not get an exact size match.
            { "weapon_galilar", (new StickerAnchor(1, 0.3629775f, 0.0299775f), new StickerAnchor(3, 0.079801366f, 0.011801365f)) },
            { "weapon_glock", (new StickerAnchor(1, 0.31375384f, -0.07624616f), null) },
            { "weapon_m249", (new StickerAnchor(2, -0.26837832f, 0.1806217f), new StickerAnchor(3, 0.0037358073f, 0.047735807f)) },
            { "weapon_m4a1", (new StickerAnchor(1, 0.33399266f, 0.015992647f), null) },
            { "weapon_mac10", (null, new StickerAnchor(3, 0.12097203f, 0.06197203f)) },
            { "weapon_mag7", (new StickerAnchor(3, -0.11744954f, 0.0085504595f), new StickerAnchor(2, -0.22446628f, 0.0075337263f)) },
            { "weapon_mp9", (new StickerAnchor(2, -0.05625756f, 0.13074245f), null) },
            { "weapon_negev", (new StickerAnchor(1, 0.032989472f, 0.051989473f), new StickerAnchor(3, -0.15602273f, -0.016022725f)) },
            { "weapon_scar20", (new StickerAnchor(3, -0.0620967f, 0.013903301f), new StickerAnchor(2, 0.28459403f, -0.011405965f)) },
            { "weapon_sg556", (new StickerAnchor(2, -0.06719466f, -0.02519466f), new StickerAnchor(2, -0.061953463f, -0.015953463f)) },
            { "weapon_ssg08", (new StickerAnchor(1, 0.0839881f, 0.024988096f), new StickerAnchor(3, -0.041737854f, -0.0067378553f)) },
            { "weapon_tec9", (new StickerAnchor(1, 0.16984202f, 0.13384202f), null) },
            { "weapon_ump45", (new StickerAnchor(2, -0.060853533f, 0.06014647f), new StickerAnchor(3, -0.15774709f, -0.008747093f)) },
        };

        /// <summary>
        /// The anchor a slot needs, or null when it must keep using its own index.
        ///
        /// Null is the answer for every slot but the fifth, for every weapon whose rendered variant authors
        /// its own fifth home, and for anything this table has never heard of - all three of which have to
        /// keep behaving exactly as they did before this table existed.
        ///
        /// <paramref name="isLegacyModel"/> is the caller's own <c>SkinsLegacyModelIndex</c> lookup, so a
        /// paint the catalogue has never heard of (incl. paint 0) resolves legacy here for the same reason
        /// it renders on the legacy mesh.
        /// </summary>
        internal static StickerAnchor? For(int weaponDefIndex, bool isLegacyModel, int stickerSlot)
        {
            if (stickerSlot != FifthStickerSlot) return null;
            if (!WeaponPaints.WeaponDefindex.TryGetValue(weaponDefIndex, out var weaponName)) return null;
            if (!Table.TryGetValue(weaponName, out var entry)) return null;

            return isLegacyModel ? entry.Legacy : entry.Hd;
        }
    }
}
