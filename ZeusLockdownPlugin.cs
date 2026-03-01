using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
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
        public override string ModuleVersion => "1.1.6"; 
        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Clean, simple list for allowed items
        private readonly HashSet<string> allowedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taser", "flashbang", "hegrenade", "smokegrenade", "molotov", "incgrenade", 
            "decoy", "smoke", "firebomb", "grenade", "c4", "kevlar", "assaultsuit", 
            "defuser", "vest", "vesthelm"
        };

        // --- THE WEAPON RESTRICT METHOD ---
        // Hardcoded dictionary of standard CS2 economy prices for illegal items
        private readonly Dictionary<string, int> WeaponPrices = new(StringComparer.OrdinalIgnoreCase)
        {
            // Pistols
            {"glock", 200}, {"usp_silencer", 200}, {"hkp2000", 200}, {"elite", 300}, 
            {"p250", 300}, {"tec9", 500}, {"cz75a", 500}, {"fiveseven", 500}, 
            {"deagle", 700}, {"revolver", 600},

            // SMGs
            {"mac10", 1050}, {"mp9", 1250}, {"mp7", 1400}, {"mp5sd", 1500}, 
            {"ump45", 1200}, {"p90", 2350}, {"bizon", 1300},

            // Rifles
            {"galilar", 1800}, 
            {"famas", 1950},           // Dropped from $2050 to $1950 in Jan 2025
            {"ak47", 2700}, 
            {"m4a1", 2900},            // M4A4 dropped from $3100 to $2900 in Jan 2025
            {"m4a1_silencer", 2900},   // M4A1-S remains $2900
            {"aug", 3300}, 
            {"sg556", 3000}, 
            {"ssg08", 1700}, 
            {"awp", 4750},             // Fixed: Was listed as $4700 previously
            {"g3sg1", 5000}, 
            {"scar20", 5000},

            // Heavy
            {"nova", 1050}, {"xm1014", 2000}, {"mag7", 1300}, {"sawedoff", 1100}, 
            {"m249", 5200}, {"negev", 1700}
        };

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            
            // Listen for the actual purchase event
            RegisterEventHandler<EventItemPurchase>(OnItemPurchase);

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned); 

            zeusReminderTimer = AddTimer(60.0f, () =>
            {
                Server.PrintToChatAll(" \x04[Zeus Lockdown]\x01 Zeus, Utility, and Knife only!");
            }, TimerFlags.REPEAT);
        }

        private bool IsItemAllowed(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return false;

            string cleanName = itemName
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            return allowedItems.Contains(cleanName) || 
                   cleanName.StartsWith("knife", StringComparison.OrdinalIgnoreCase) || 
                   cleanName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        }

        // --- THE PERFECT REFUND METHOD ---
        private HookResult OnItemPurchase(EventItemPurchase ev, GameEventInfo info)
        {
            var player = ev.Userid;
            if (player == null || !player.IsValid) return HookResult.Continue;

            string weaponPurchased = ev.Weapon;

            if (!IsItemAllowed(weaponPurchased))
            {
                string cleanName = weaponPurchased.ToLowerInvariant().Replace("weapon_", "").Replace("item_", "").Trim();
                
                // Lookup the price in our dictionary and refund them
                if (WeaponPrices.TryGetValue(cleanName, out int refundAmount))
                {
                    if (player.InGameMoneyServices != null)
                    {
                        player.InGameMoneyServices.Account += refundAmount;
                        // Force the UI to update the player's wallet instantly
                        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
                    }
                }

                // Warn the player
                player.PrintToChat(" \x02[Zeus Lockdown]\x01 That weapon is restricted! Purchase refunded.");

                // Strip the illegal weapon immediately on the next frame
                Server.NextFrame(() => StripIllegalWeapons(player));
            }

            return HookResult.Continue;
        }

        // --- HANDLES MAP DROPS AND DROPPED ILLEGAL WEAPONS ---
        private void OnEntitySpawned(CEntityInstance entity)
        {
            if (entity == null || !entity.IsValid) return;

            string name = entity.DesignerName;
            if (string.IsNullOrEmpty(name)) return;

            if (name.StartsWith("weapon_") || name.StartsWith("item_"))
            {
                if (!IsItemAllowed(name))
                {
                    Server.NextFrame(() =>
                    {
                        if (entity != null && entity.IsValid)
                        {
                            var baseEntity = entity as CBaseEntity;
                            if (baseEntity != null && (baseEntity.OwnerEntity == null || !baseEntity.OwnerEntity.IsValid))
                            {
                                baseEntity.Remove();
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
            if (!IsItemAllowed(ev.Item))
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
                    if (!IsItemAllowed(weapEnt.DesignerName))
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



