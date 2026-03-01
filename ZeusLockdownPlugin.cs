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
        public override string ModuleVersion => "1.0.8"; 
        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Unified list for both the buy menu AND map drops
        private readonly HashSet<string> allowedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Raw item names
            "taser", "flashbang", "hegrenade", "smokegrenade", "molotov", "incgrenade", 
            "decoy", "smoke", "firebomb", "grenade", "c4", "kevlar", "assaultsuit", 
            "defuser", "vest", "vesthelm", 

            // CS2 UI Loadout slot names (just in case)
            "grenade0", "grenade1", "grenade2", "grenade3", "grenade4",
            "equipment0", "equipment1", "equipment2", "equipment3", "equipment4"
        };

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            
            AddCommandListener("buy", OnPlayerBuy);
            AddCommandListener("autobuy", OnPlayerAutoBuy);
            AddCommandListener("rebuy", OnPlayerReBuy);

            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);
            RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned); 

            zeusReminderTimer = AddTimer(60.0f, () =>
            {
                Server.PrintToChatAll(" \x04[Zeus Lockdown]\x01 Zeus, Utility, and Knife only!");
            }, TimerFlags.REPEAT);
        }

        // --- UNIVERSAL CHECKER ---
        private bool IsItemAllowed(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return false;

            // Strip out quotes, weapon_, and item_ prefixes so "weapon_taser" becomes "taser"
            string cleanName = itemName
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Replace("\"", "")
                .Trim();

            return allowedItems.Contains(cleanName) || 
                   cleanName.StartsWith("knife", StringComparison.OrdinalIgnoreCase) || 
                   cleanName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        }

        private HookResult OnPlayerBuy(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid) return HookResult.Continue;

            string rawBuyArg = info.GetArg(1);
            if (string.IsNullOrWhiteSpace(rawBuyArg)) return HookResult.Continue;

            // Run the raw argument through our universal checker
            if (!IsItemAllowed(rawBuyArg))
            {
                // We print the exact raw argument here so you can see what CS2 is doing under the hood!
                player.PrintToChat($" \x02[Zeus Lockdown]\x01 Restricted! Only Zeus/Utility allowed. (Debug: {rawBuyArg})");
                return HookResult.Stop; 
            }

            return HookResult.Continue;
        }

        private HookResult OnPlayerAutoBuy(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid) return HookResult.Continue;
            player.PrintToChat(" \x02[Zeus Lockdown]\x01 Autobuy is disabled in this mode.");
            return HookResult.Stop; 
        }

        private HookResult OnPlayerReBuy(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid) return HookResult.Continue;
            player.PrintToChat(" \x02[Zeus Lockdown]\x01 Rebuy is disabled in this mode.");
            return HookResult.Stop; 
        }

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
                            if (baseEntity != null)
                            {
                                if (baseEntity.OwnerEntity == null || !baseEntity.OwnerEntity.IsValid)
                                {
                                    baseEntity.Remove();
                                }
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
