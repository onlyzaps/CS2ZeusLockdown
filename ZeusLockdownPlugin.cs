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
        public override string ModuleVersion => "1.0.9"; 
        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        // Used for checking physical entities (drops, pickups, map spawns)
        private readonly HashSet<string> allowedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taser", "flashbang", "hegrenade", "grenade", "smokegrenade", "smoke",
            "molotov", "incgrenade", "firebomb", "decoy", "decoygrenade", "c4",
            "kevlar", "assaultsuit", "defuser" 
        };

        // NEW: Strictly for the buy menu interceptor. 
        // We include the raw UI slot commands CS2 sends under the hood for utilities.
        private readonly HashSet<string> allowedBuyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taser",
            "flashbang", "hegrenade", "smokegrenade", "molotov", "incgrenade", "decoy", "smoke", "firebomb", "grenade",
            "grenade0", "grenade1", "grenade2", "grenade3", "grenade4", // CS2 Loadout slots for grenades
            "kevlar", "assaultsuit", "defuser", "vest", "vesthelm" // Include aliases for armor
        };

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            
            // We use command listeners to block the transaction BEFORE money is touched
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

        // --- THE BULLETPROOF BUY BLOCKER ---
        private HookResult OnPlayerBuy(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid) return HookResult.Continue;

            // Grab what they are trying to buy (e.g., "rifle1", "taser", "grenade0")
            string buyItem = info.GetArg(1).ToLowerInvariant().Trim();
            if (string.IsNullOrWhiteSpace(buyItem)) return HookResult.Continue;

            // If the command is NOT explicitly in our safe list (meaning they clicked a pistol, rifle, etc.)
            if (!allowedBuyCommands.Contains(buyItem))
            {
                player.PrintToChat(" \x02[Zeus Lockdown]\x01 That weapon is restricted! Only Zeus and Utility are allowed.");
                // HookResult.Stop halts the game engine here. No money is spent, no gun is spawned.
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

        // --- HANDLES MAP DROPS ---
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
                            // Safely cast to CBaseEntity so the compiler knows what OwnerEntity is
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
