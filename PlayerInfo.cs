using CounterStrikeSharp.API.Core;

namespace WeaponPaints
{
	public class PlayerInfo
	{
		public int Index { get; set; }
		public int Slot { get; init; }
		public int? UserId { get; set; }
		public string? SteamId { get; init; }
		public string? Name { get; set; }

		public static PlayerInfo From(CCSPlayerController player) => new()
		{
			UserId = player.UserId,
			Slot = player.Slot,
			Index = (int)player.Index,
			SteamId = player.SteamID.ToString(),
			Name = player.PlayerName,
		};
	}
}
