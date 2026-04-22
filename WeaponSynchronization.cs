using System.Collections.Concurrent;
using System.Globalization;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

internal class WeaponSynchronization
{
    private readonly WeaponPaintsConfig _config;
    private readonly Database _database;

    internal WeaponSynchronization(Database database, WeaponPaintsConfig config)
    {
        _database = database;
        _config = config;
    }

    internal async Task GetPlayerData(PlayerInfo? player)
    {
        try
        {
            // Clear existing cached data before loading fresh from database
            if (player != null)
            {
                WeaponPaints.GPlayersKnife.TryRemove(player.Slot, out _);
                WeaponPaints.GPlayersGlove.TryRemove(player.Slot, out _);
                WeaponPaints.GPlayersAgent.TryRemove(player.Slot, out _);
                WeaponPaints.GPlayersMusic.TryRemove(player.Slot, out _);
                WeaponPaints.GPlayerWeaponsInfo.TryRemove(player.Slot, out _);
                WeaponPaints.GPlayersPin.TryRemove(player.Slot, out _);
            }

            await using var connection = await _database.GetConnectionAsync();

            if (_config.Additional.KnifeEnabled)
                await GetKnifeFromDatabaseAsync(player, connection);
            if (_config.Additional.GloveEnabled)
                await GetGloveFromDatabaseAsync(player, connection);
            if (_config.Additional.AgentEnabled)
                await GetAgentFromDatabaseAsync(player, connection);
            if (_config.Additional.MusicEnabled)
                await GetMusicFromDatabaseAsync(player, connection);
            if (_config.Additional.SkinEnabled)
                await GetWeaponPaintsFromDatabaseAsync(player, connection);
            if (_config.Additional.PinsEnabled)
                await GetPinsFromDatabaseAsync(player, connection);
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetPlayerData failed: {Message}", ex.Message);
        }
    }

    private async Task GetKnifeFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (!_config.Additional.KnifeEnabled || string.IsNullOrEmpty(player?.SteamId))
                return;

            const string query =
                "SELECT `knife`, `weapon_team` FROM `wp_player_knife` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC";
            var rows = await connection.QueryAsync<dynamic>(query, new { steamid = player.SteamId });

            var rowCount = 0;
            foreach (var row in rows)
            {
                rowCount++;

                if (string.IsNullOrEmpty(row.knife))
                    continue;

                CsTeam weaponTeam = (int)row.weapon_team switch
                {
                    2 => CsTeam.Terrorist,
                    3 => CsTeam.CounterTerrorist,
                    _ => CsTeam.None,
                };

                var playerKnives = WeaponPaints.GPlayersKnife.GetOrAdd(
                    player.Slot,
                    _ => new ConcurrentDictionary<CsTeam, string>()
                );

                if (weaponTeam == CsTeam.None)
                {
                    playerKnives[CsTeam.Terrorist] = row.knife;
                    playerKnives[CsTeam.CounterTerrorist] = row.knife;
                }
                else
                {
                    playerKnives[weaponTeam] = row.knife;
                }
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetKnifeFromDatabase failed: {Message}", ex.Message);
        }
    }

    private async Task GetGloveFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (!_config.Additional.GloveEnabled || string.IsNullOrEmpty(player?.SteamId))
                return;

            const string query =
                "SELECT `weapon_defindex`, `weapon_team` FROM `wp_player_gloves` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC";
            var rows = await connection.QueryAsync<dynamic>(query, new { steamid = player.SteamId });

            var rowCount = 0;
            foreach (var row in rows)
            {
                rowCount++;
                if (row.weapon_defindex == null)
                    continue;

                var playerGloves = WeaponPaints.GPlayersGlove.GetOrAdd(
                    player.Slot,
                    _ => new ConcurrentDictionary<CsTeam, ushort>()
                );
                CsTeam weaponTeam = (int)row.weapon_team switch
                {
                    2 => CsTeam.Terrorist,
                    3 => CsTeam.CounterTerrorist,
                    _ => CsTeam.None,
                };

                if (weaponTeam == CsTeam.None)
                {
                    playerGloves[CsTeam.Terrorist] = (ushort)row.weapon_defindex;
                    playerGloves[CsTeam.CounterTerrorist] = (ushort)row.weapon_defindex;
                }
                else
                {
                    playerGloves[weaponTeam] = (ushort)row.weapon_defindex;
                }
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetGloveFromDatabase failed: {Message}", ex.Message);
        }
    }

    private async Task GetAgentFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (!_config.Additional.AgentEnabled || string.IsNullOrEmpty(player?.SteamId))
                return;

            const string query =
                "SELECT `agent_ct`, `agent_t` FROM `wp_player_agents` WHERE `steamid` = @steamid";
            var agentData = await connection.QueryFirstOrDefaultAsync<(string, string)>(
                query,
                new { steamid = player.SteamId }
            );

            if (agentData == default)
            {
                return;
            }

            var agentCT = agentData.Item1;
            var agentT = agentData.Item2;

            if (!string.IsNullOrEmpty(agentCT) || !string.IsNullOrEmpty(agentT))
            {
                WeaponPaints.GPlayersAgent[player.Slot] = (agentCT, agentT);
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetAgentFromDatabase failed: {Message}", ex.Message);
        }
    }

    private async Task GetWeaponPaintsFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (
                !_config.Additional.SkinEnabled
                || player == null
                || string.IsNullOrEmpty(player.SteamId)
            )
                return;

            var playerWeapons = WeaponPaints.GPlayerWeaponsInfo.GetOrAdd(
                player.Slot,
                _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>()
            );

            const string query = @"
                SELECT `weapon_team`, `weapon_defindex`, `weapon_paint_id`, `weapon_wear`, `weapon_seed`,
                       `weapon_nametag`, `weapon_stattrak`, `weapon_stattrak_count`,
                       `weapon_sticker_0`, `weapon_sticker_1`, `weapon_sticker_2`,
                       `weapon_sticker_3`, `weapon_sticker_4`, `weapon_keychain`
                FROM `wp_player_skins`
                WHERE `steamid` = @steamid
                ORDER BY `weapon_team` ASC";
            var playerSkins = await connection.QueryAsync<dynamic>(query, new { steamid = player.SteamId });

            var rowCount = 0;
            foreach (var row in playerSkins)
            {
                rowCount++;
                int weaponDefIndex = row.weapon_defindex ?? 0;
                int weaponPaintId = row.weapon_paint_id ?? 0;
                float weaponWear = row.weapon_wear ?? 0f;
                int weaponSeed = row.weapon_seed ?? 0;
                string weaponNameTag = row.weapon_nametag ?? "";
                if (weaponNameTag.Length > 128)
                    weaponNameTag = weaponNameTag[..128];
                bool weaponStatTrak = row.weapon_stattrak ?? false;
                int weaponStatTrakCount = row.weapon_stattrak_count ?? 0;

                CsTeam weaponTeam = row.weapon_team switch
                {
                    2 => CsTeam.Terrorist,
                    3 => CsTeam.CounterTerrorist,
                    _ => CsTeam.None,
                };

                string[]? keyChainParts = row.weapon_keychain?.ToString().Split(';');

                KeyChainInfo keyChainInfo = new KeyChainInfo();

                if (
                    keyChainParts!.Length == 5
                    && uint.TryParse(keyChainParts[0], out uint keyChainId)
                    && float.TryParse(
                        keyChainParts[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float keyChainOffsetX
                    )
                    && float.TryParse(
                        keyChainParts[2],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float keyChainOffsetY
                    )
                    && float.TryParse(
                        keyChainParts[3],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out float keyChainOffsetZ
                    )
                    && uint.TryParse(keyChainParts[4], out uint keyChainSeed)
                )
                {
                    keyChainInfo.Id = keyChainId;
                    keyChainInfo.OffsetX = keyChainOffsetX;
                    keyChainInfo.OffsetY = keyChainOffsetY;
                    keyChainInfo.OffsetZ = keyChainOffsetZ;
                    keyChainInfo.Seed = keyChainSeed;
                }
                else
                {
                    keyChainInfo.Id = 0;
                    keyChainInfo.OffsetX = 0f;
                    keyChainInfo.OffsetY = 0f;
                    keyChainInfo.OffsetZ = 0f;
                    keyChainInfo.Seed = 0;
                }

                WeaponInfo weaponInfo = new WeaponInfo
                {
                    Paint = weaponPaintId,
                    Seed = weaponSeed,
                    Wear = weaponWear,
                    Nametag = weaponNameTag,
                    KeyChain = keyChainInfo,
                    StatTrak = weaponStatTrak,
                    StatTrakCount = weaponStatTrakCount,
                };

                // Retrieve and parse sticker data (up to 5 slots)
                for (int i = 0; i <= 4; i++)
                {
                    // Access the sticker data dynamically using reflection
                    string stickerColumn = $"weapon_sticker_{i}";
                    var stickerData = ((IDictionary<string, object>)row!)[stickerColumn];

                    if (string.IsNullOrEmpty(stickerData?.ToString()))
                        continue;

                    var parts = stickerData.ToString()!.Split(';');

                    //"id;schema;x;y;wear;scale;rotation"
                    if (
                        parts.Length != 7
                        || !uint.TryParse(parts[0], out uint stickerId)
                        || !uint.TryParse(parts[1], out uint stickerSchema)
                        || !float.TryParse(
                            parts[2],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float stickerOffsetX
                        )
                        || !float.TryParse(
                            parts[3],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float stickerOffsetY
                        )
                        || !float.TryParse(
                            parts[4],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float stickerWear
                        )
                        || !float.TryParse(
                            parts[5],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float stickerScale
                        )
                        || !float.TryParse(
                            parts[6],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out float stickerRotation
                        )
                    )
                        continue;

                    StickerInfo stickerInfo = new StickerInfo
                    {
                        Id = stickerId,
                        Schema = stickerSchema,
                        OffsetX = stickerOffsetX,
                        OffsetY = stickerOffsetY,
                        Wear = stickerWear,
                        Scale = stickerScale,
                        Rotation = stickerRotation,
                    };

                    weaponInfo.Stickers.Add(stickerInfo);
                }

                var teamWeapons = playerWeapons.GetOrAdd(
                    weaponTeam,
                    _ => new ConcurrentDictionary<int, WeaponInfo>()
                );
                teamWeapons[weaponDefIndex] = weaponInfo;

                // Also add to both teams if None
                if (weaponTeam == CsTeam.None)
                {
                    var tWeapons = playerWeapons.GetOrAdd(
                        CsTeam.Terrorist,
                        _ => new ConcurrentDictionary<int, WeaponInfo>()
                    );
                    var ctWeapons = playerWeapons.GetOrAdd(
                        CsTeam.CounterTerrorist,
                        _ => new ConcurrentDictionary<int, WeaponInfo>()
                    );
                    tWeapons[weaponDefIndex] = weaponInfo;
                    ctWeapons[weaponDefIndex] = weaponInfo;
                }
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetWeaponPaintsFromDatabase failed: {Message}", ex.Message);
        }
    }

    private async Task GetMusicFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (!_config.Additional.MusicEnabled || string.IsNullOrEmpty(player?.SteamId))
                return;

            const string query =
                "SELECT `music_id`, `weapon_team` FROM `wp_player_music` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC";
            var rows = await connection.QueryAsync<dynamic>(query, new { steamid = player.SteamId });

            var rowCount = 0;
            foreach (var row in rows)
            {
                rowCount++;
                if (row.music_id == null)
                    continue;

                var playerMusic = WeaponPaints.GPlayersMusic.GetOrAdd(
                    player.Slot,
                    _ => new ConcurrentDictionary<CsTeam, ushort>()
                );
                CsTeam weaponTeam = (int)row.weapon_team switch
                {
                    2 => CsTeam.Terrorist,
                    3 => CsTeam.CounterTerrorist,
                    _ => CsTeam.None,
                };

                if (weaponTeam == CsTeam.None)
                {
                    playerMusic[CsTeam.Terrorist] = (ushort)row.music_id;
                    playerMusic[CsTeam.CounterTerrorist] = (ushort)row.music_id;
                }
                else
                {
                    playerMusic[weaponTeam] = (ushort)row.music_id;
                }
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetMusicFromDatabase failed: {Message}", ex.Message);
        }
    }

    private async Task GetPinsFromDatabaseAsync(PlayerInfo? player, MySqlConnection connection)
    {
        try
        {
            if (!_config.Additional.PinsEnabled || string.IsNullOrEmpty(player?.SteamId))
                return;

            const string query =
                "SELECT `id`, `weapon_team` FROM `wp_player_pins` WHERE `steamid` = @steamid ORDER BY `weapon_team` ASC";
            var rows = await connection.QueryAsync<dynamic>(query, new { steamid = player.SteamId });

            var rowCount = 0;
            foreach (var row in rows)
            {
                rowCount++;
                if (row.id == null)
                    continue;

                var playerPins = WeaponPaints.GPlayersPin.GetOrAdd(
                    player.Slot,
                    _ => new ConcurrentDictionary<CsTeam, ushort>()
                );
                CsTeam weaponTeam = (int)row.weapon_team switch
                {
                    2 => CsTeam.Terrorist,
                    3 => CsTeam.CounterTerrorist,
                    _ => CsTeam.None,
                };

                if (weaponTeam == CsTeam.None)
                {
                    playerPins[CsTeam.Terrorist] = (ushort)row.id;
                    playerPins[CsTeam.CounterTerrorist] = (ushort)row.id;
                }
                else
                {
                    playerPins[weaponTeam] = (ushort)row.id;
                }
            }
        }
        catch (Exception ex)
        {
            WeaponPaints.Instance.Logger.LogWarning("GetPinsFromDatabase failed: {Message}", ex.Message);
        }
    }

    internal async Task SyncKnifeToDatabase(PlayerInfo player, string knife, CsTeam[] teams)
    {
        if (
            !_config.Additional.KnifeEnabled
            || string.IsNullOrEmpty(player.SteamId)
            || teams.Length == 0
        )
            return;

        const string query =
            "INSERT INTO `wp_player_knife` (`steamid`, `weapon_team`, `knife`) VALUES(@steamid, @team, @newKnife) ON DUPLICATE KEY UPDATE `knife` = @newKnife";

        try
        {
            await using var connection = await _database.GetConnectionAsync();

            foreach (var team in teams)
            {
                await connection.ExecuteAsync(
                    query,
                    new
                    {
                        steamid = player.SteamId,
                        team,
                        newKnife = knife,
                    }
                );
            }
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing knife to database: {e.Message}");
        }
    }

    internal async Task SyncGloveToDatabase(PlayerInfo player, ushort gloveDefIndex, CsTeam[] teams)
    {
        if (
            !_config.Additional.GloveEnabled
            || string.IsNullOrEmpty(player.SteamId)
            || teams.Length == 0
        )
            return;

        const string query =
            @"
        INSERT INTO `wp_player_gloves` (`steamid`, `weapon_team`, `weapon_defindex`) 
        VALUES(@steamid, @team, @gloveDefIndex) 
        ON DUPLICATE KEY UPDATE `weapon_defindex` = @gloveDefIndex";

        try
        {
            await using var connection = await _database.GetConnectionAsync();

            foreach (var team in teams)
            {
                await connection.ExecuteAsync(
                    query,
                    new
                    {
                        steamid = player.SteamId,
                        team = (int)team,
                        gloveDefIndex,
                    }
                );
            }
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing glove to database: {e.Message}");
        }
    }

    internal async Task SyncAgentToDatabase(PlayerInfo player)
    {
        if (!_config.Additional.AgentEnabled || string.IsNullOrEmpty(player.SteamId))
            return;

        if (!WeaponPaints.GPlayersAgent.TryGetValue(player.Slot, out var agents))
            return;

        const string query = """
            					INSERT INTO `wp_player_agents` (`steamid`, `agent_ct`, `agent_t`)
            					VALUES(@steamid, @agent_ct, @agent_t)
            					ON DUPLICATE KEY UPDATE
            						`agent_ct` = @agent_ct,
            						`agent_t` = @agent_t
            """;
        try
        {
            await using var connection = await _database.GetConnectionAsync();

            await connection.ExecuteAsync(
                query,
                new
                {
                    steamid = player.SteamId,
                    agent_ct = agents.CT,
                    agent_t = agents.T,
                }
            );
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing agents to database: {e.Message}");
        }
    }

    internal async Task SyncWeaponPaintsToDatabase(PlayerInfo player)
    {
        if (
            string.IsNullOrEmpty(player.SteamId)
            || !WeaponPaints.GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamWeaponInfos)
        )
            return;

        // Flatten to a single batch — one round-trip instead of N inserts.
        var sb = new System.Text.StringBuilder(
            "INSERT INTO `wp_player_skins` " +
            "(`steamid`, `weapon_defindex`, `weapon_team`, `weapon_paint_id`, `weapon_wear`, `weapon_seed`) VALUES "
        );
        var args = new DynamicParameters();
        args.Add("steamid", player.SteamId);

        var i = 0;
        foreach (var (teamId, weaponsInfo) in teamWeaponInfos)
        {
            foreach (var (weaponDefIndex, weaponInfo) in weaponsInfo)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"(@steamid, @d{i}, @t{i}, @p{i}, @w{i}, @s{i})");
                args.Add($"d{i}", weaponDefIndex);
                args.Add($"t{i}", (int)teamId);
                args.Add($"p{i}", weaponInfo.Paint);
                args.Add($"w{i}", weaponInfo.Wear);
                args.Add($"s{i}", weaponInfo.Seed);
                i++;
            }
        }

        if (i == 0) return;

        sb.Append(
            " ON DUPLICATE KEY UPDATE " +
            "`weapon_paint_id` = VALUES(`weapon_paint_id`), " +
            "`weapon_wear` = VALUES(`weapon_wear`), " +
            "`weapon_seed` = VALUES(`weapon_seed`)"
        );

        try
        {
            await using var connection = await _database.GetConnectionAsync();
            await connection.ExecuteAsync(sb.ToString(), args);
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing weapon paints to database: {e.Message}");
        }
    }

    internal async Task SyncMusicToDatabase(PlayerInfo player, ushort music, CsTeam[] teams)
    {
        if (!_config.Additional.MusicEnabled || string.IsNullOrEmpty(player.SteamId))
            return;

        const string query =
            "INSERT INTO `wp_player_music` (`steamid`, `weapon_team`, `music_id`) VALUES(@steamid, @team, @newMusic) ON DUPLICATE KEY UPDATE `music_id` = @newMusic";

        try
        {
            await using var connection = await _database.GetConnectionAsync();

            foreach (var team in teams)
            {
                await connection.ExecuteAsync(
                    query,
                    new
                    {
                        steamid = player.SteamId,
                        team,
                        newMusic = music,
                    }
                );
            }
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing music kit to database: {e.Message}");
        }
    }

    internal async Task SyncPinToDatabase(PlayerInfo player, ushort pin, CsTeam[] teams)
    {
        if (!_config.Additional.PinsEnabled || string.IsNullOrEmpty(player.SteamId))
            return;

        const string query =
            "INSERT INTO `wp_player_pins` (`steamid`, `weapon_team`, `id`) VALUES(@steamid, @team, @newPin) ON DUPLICATE KEY UPDATE `id` = @newPin";

        try
        {
            await using var connection = await _database.GetConnectionAsync();

            foreach (var team in teams)
            {
                await connection.ExecuteAsync(
                    query,
                    new
                    {
                        steamid = player.SteamId,
                        team,
                        newPin = pin,
                    }
                );
            }
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing pin to database: {e.Message}");
        }
    }

    internal async Task SyncStatTrakToDatabase(PlayerInfo player)
    {
        if (WeaponPaints.WeaponSync == null || WeaponPaints.GPlayerWeaponsInfo.IsEmpty)
            return;
        if (string.IsNullOrEmpty(player.SteamId))
            return;

        try
        {
            await using var connection = await _database.GetConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            if (!WeaponPaints.GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamWeaponsInfo))
                return;

            foreach (var teamInfo in teamWeaponsInfo)
            {
                var weaponInfos = teamInfo.Value;

                var statTrakWeapons = weaponInfos.ToDictionary(
                    w => w.Key,
                    w => (w.Value.StatTrak, w.Value.StatTrakCount)
                );

                if (statTrakWeapons.Count == 0)
                    continue;

                int weaponTeam = (int)teamInfo.Key;

                foreach (var (defindex, (statTrak, statTrakCount)) in statTrakWeapons)
                {
                    const string query =
                        @"
				    UPDATE `wp_player_skins` 
				    SET `weapon_stattrak` = @StatTrak, 
				        `weapon_stattrak_count` = @StatTrakCount
				    WHERE `steamid` = @steamid 
				      AND `weapon_defindex` = @weaponDefIndex
				      AND `weapon_team` = @weaponTeam";

                    var parameters = new
                    {
                        steamid = player.SteamId,
                        weaponDefIndex = defindex,
                        StatTrak = statTrak,
                        StatTrakCount = statTrakCount,
                        weaponTeam,
                    };

                    await connection.ExecuteAsync(query, parameters, transaction);
                }
            }

            await transaction.CommitAsync();
        }
        catch (Exception e)
        {
            Utility.Log($"Error syncing stattrak to database: {e.Message}");
        }
    }
}
