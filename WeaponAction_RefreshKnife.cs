using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints
{
    public partial class WeaponPaints
    {
        private void RefreshKnife(CCSPlayerController? player)
        {
            if (!_gBCommandsAllowed)
            {
                return;
            }
            if (player == null || !player.IsValid || player.PlayerPawn.Value == null || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
            {
                return;
            }
            if (player.PlayerPawn.Value.WeaponServices == null)
            {
                return;
            }

            var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;
            CBasePlayerWeapon? currentKnife = null;

            // Find current knife
            foreach (var weapon in weapons)
            {
                if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid)
                    continue;
                var designerName = weapon.Value.DesignerName;
                if (designerName.Contains("knife") || designerName.Contains("bayonet"))
                {
                    currentKnife = weapon.Value;
                    break;
                }
            }

            if (currentKnife == null)
            {
                return;
            }

            // Kill current knife
            currentKnife.AddEntityIOEvent("Kill", currentKnife, null, "", 0.01f);

            // Give back a generic knife - OnEntityCreated event will automatically call GivePlayerWeaponSkin
            AddTimer(
                0.1f,
                () =>
                {
                    if (!_gBCommandsAllowed)
                        return;
                    if (player == null || !player.IsValid || player.PlayerPawn.Value == null)
                    {
                        return;
                    }

                    // Use CsItem.Knife - OnEntityCreated will handle applying the skin automatically
                    var newKnifeHandle = player.GiveNamedItem(CsItem.Knife);

                    if (newKnifeHandle == IntPtr.Zero)
                    {
                        return;
                    }

                    // Switch to knife slot
                    AddTimer(
                        0.05f,
                        () =>
                        {
                            if (player != null && player.IsValid)
                            {
                                player.ExecuteClientCommand("slot3");
                            }
                        }
                    );
                }
            );
        }
    }
}
