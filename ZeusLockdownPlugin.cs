using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusLockdown
{
    public class ZeusLockdownPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Lockdown";
        public override string ModuleVersion => "1.0.6"; 
        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Clean list of actual entity names
        private readonly HashSet<string> allowedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taser",
            "flashbang",
            "hegrenade", "grenade",
            "smokegrenade", "smoke",
            "molotov", "incgrenade", "firebomb",
            "decoy", "decoygrenade", "c4",
            "kevlar", "assaultsuit", "defuser" 
        };

        // Hardcoded price dictionary for instantaneous refunds
        private readonly Dictionary<string, int> weaponPrices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Pistols
            { "glock", 200 }, { "hkp2000", 200 }, { "usp_silencer", 200 },
            { "elite", 300 }, { "p250", 300 }, { "tec9", 500 },
            { "fiveseven", 500 }, { "cz75a", 500 }, { "deagle", 700 }, { "revolver", 600 },
            // SMGs
            { "mac10", 1050 }, { "mp9", 1250 }, { "mp7", 1500 }, { "mp5sd", 1500 },
            { "ump45", 1200 }, { "p90", 2350 }, { "bizon", 1400 },
            // Shotguns
            { "nova", 1050 }, { "xm1014", 2000 }, { "sawedoff", 1100 }, { "mag7", 1300 },
            // Rifles
            { "galilar", 1800 }, { "famas", 2050 }, { "ak47", 2700 }, { "m4a1", 3100 },
            { "m4a1_silencer", 2900 }, { "ssg08", 1700 }, { "aug", 3300 },
            { "sg556", 3000 }, { "awp", 4700 }, { "scar20", 5000 }, { "g3sg1", 5000 },
            // Heavy
            { "m249", 5200 }, { "negev", 1700 }
        };

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            
            // Listen for the completed transaction to intercept the actual item name
            RegisterEventHandler<EventItemPurchase>(OnItemPurchase); 

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned); 

            zeusReminderTimer = AddTimer(60.0f, () =>
            {
                Server.PrintToChatAll(" \x04[Zeus Lockdown]\x01 Zeus, Utility, and Knife only!");
            }, TimerFlags.REPEAT);
        }

        private bool IsWeaponAllowed(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName)) return false;

            string cleanName = weaponName
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            return allowedWeapons.Contains(cleanName) || 
                   cleanName.StartsWith("knife", StringComparison.OrdinalIgnoreCase) || 
                   cleanName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        }

        // --- THE REFUND LOGIC ---
        private HookResult OnItemPurchase(EventItemPurchase ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid) return HookResult.Continue;

            string cleanName = ev.Weapon.ToLowerInvariant().Replace("weapon_", "").Replace("item_", "").Trim();

            if (!IsWeaponAllowed(cleanName))
            {
                player.PrintToChat(" \x02[Zeus Lockdown]\x01 That weapon is restricted! You have been refunded.");

                // 1. Refund the player
                var moneyServices = player.InGameMoneyServices;
                if (moneyServices != null && weaponPrices.TryGetValue(cleanName, out int price))
                {
                    moneyServices.Account += price;
                    // Force the server to update the player's HUD instantly
                    Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
                }

                // 2. Wait exactly one frame for the weapon to attach to the player, then delete it
                Server.NextFrame(() =>
                {
                    if (player == null || !player.IsValid) return;
                    StripIllegalWeapons(player);
                });
            }

            return HookResult.Continue;
        }

        private void OnEntitySpawned(CEntityInstance entity)
        {
            if (entity == null || !entity.IsValid) return;

            string name = entity.DesignerName;
            if (string.IsNullOrEmpty(name)) return;

            if (name.StartsWith("weapon_") || name.StartsWith("item_"))
            {
                if (!IsWeaponAllowed(name))
                {
                    Server.NextFrame(() =>
                    {
                        if (entity != null && entity.IsValid)
                        {
                            // Only remove it if it doesn't belong to a player (map drops)
                            // Player removals are handled by StripIllegalWeapons
                            if (entity.OwnerEntity == null || !entity.OwnerEntity.IsValid)
                            {
                                entity.Remove();
                            }
                        }
                    });
                }
            }
        }

        private HookResult OnRoundStart(EventRoundStart ev, GameEventInfo info)
        {
            Server.ExecuteCommand("mp_taser_recharge_time 1");
            Server.ExecuteCommand("sv_enablebunnyhopping 1");
            Server.ExecuteCommand("sv_autobunnyhopping 1");
            Server.ExecuteCommand("sv_staminamax 0");
            Server.ExecuteCommand("sv_staminajumpcost 0");
            Server.ExecuteCommand("sv_staminalandcost 0");
            Server.ExecuteCommand("sv_staminarecoveryrate 0");
            
            foreach (var player in Utilities.GetPlayers())
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive || player.TeamNum < 2) continue;
                StripIllegalWeapons(player);
            }

            return HookResult.Continue;
        }

        private void OnPlayerJoin(int playerSlot)
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.SteamID == 0) return;

            StripIllegalWeapons(player);
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
        {
            var player = ev.Userid;

            if (player == null || !player.IsValid || player.TeamNum < 2) return HookResult.Continue;

            Server.NextFrame(() =>
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive) return;

                var weaponServices = player.PlayerPawn.Value?.WeaponServices;
                if (weaponServices == null) return;

                bool hasTaser = false;

                foreach (var weapon in weaponServices.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                {
                    var weapEnt = weapon.Value;
                    if (weapEnt != null && weapEnt.IsValid)
                    {
                        if (weapEnt.DesignerName.Contains("taser", StringComparison.OrdinalIgnoreCase))
                        {
                            hasTaser = true;
                            break;
                        }
                    }
                }

                if (!hasTaser)
                {
                    player.GiveNamedItem("weapon_taser");
                }
            });

            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath ev, GameEventInfo info)
        {
            if (ev.Weapon != "taser") return HookResult.Continue;

            var victim = ev.Userid;

            if (victim == null || !victim.IsValid) return HookResult.Continue;
            
            var pawn = victim.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) return HookResult.Continue;

            var position = pawn.AbsOrigin;
            if (position == null) return HookResult.Continue;

            var deathPos = new Vector(position.X, position.Y, position.Z);

            AddTimer(2.0f, () =>
            {
                var spark = Utilities.CreateEntityByName<CBaseEntity>("env_spark");
                if (spark == null || !spark.IsValid) return;

                spark.Teleport(deathPos, new QAngle(0, 0, 0), new Vector(0, 0, 0));
                spark.DispatchSpawn();
                
                spark.AcceptInput("SparkOnce");
                spark.EmitSound("Weapon_Taser.ChargeReady_Zap");

                AddTimer(3.0f, () =>
                {
                    if (spark != null && spark.IsValid)
                    {
                        spark.Remove(); 
                    }
                });
            });

            return HookResult.Continue;
        }

        private HookResult OnItemPickup(EventItemPickup ev, GameEventInfo info)
        {
            if (!IsWeaponAllowed(ev.Item))
            {
                var player = ev.Userid;
                if (player != null && player.IsValid)
                {
                    StripIllegalWeapons(player);
                }
            }

            return HookResult.Continue;
        }

        private void StripIllegalWeapons(CCSPlayerController player)
        {
            if (player.PlayerPawn.Value == null || player.PlayerPawn.Value.WeaponServices == null) return;

            foreach (var weapon in player.PlayerPawn.Value.WeaponServices.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
            {
                var weapEnt = weapon.Value;

                if (weapEnt != null && weapEnt.IsValid)
                {
                    if (!IsWeaponAllowed(weapEnt.DesignerName))
                    {
                        weapEnt.Remove();
                    }
                }
            }
        }

        public override void Unload(bool hotReload)
        {
            zeusReminderTimer?.Kill();
            zeusReminderTimer = null;
        }
    }
}
