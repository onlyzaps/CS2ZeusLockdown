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
        public override string ModuleVersion => "1.0"; // Version bump for Taser Respawn
        private CounterStrikeSharp.API.Modules.Timers.Timer? zeusReminderTimer;

        private readonly HashSet<string> allowedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "taser",
            "flashbang",
            "hegrenade", "grenade",
            "smokegrenade", "smoke",
            "molotov", "incgrenade", "firebomb",
            "decoy", "decoygrenade", "c4",
            "grenade0", "grenade1", "grenade2", "grenade3", "grenade4",
            "kevlar", "assaultsuit", "defuser" 
        };

        public override void Load(bool hotReload)
        {
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventItemPickup>(OnItemPickup);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn); // Added Spawn Event Hook
            RegisterListener<Listeners.OnClientPutInServer>(OnPlayerJoin);

            AddCommandListener("buy", OnBuyCommand);

            zeusReminderTimer = AddTimer(5.0f, () =>
            {
                Server.PrintToChatAll(" [Zeus Lockdown] Zeus, Utility, and Knife only!");
            }, TimerFlags.REPEAT);
        }

        private bool IsWeaponAllowed(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName)) return false;

            return allowedWeapons.Contains(weaponName) || 
                   weaponName.StartsWith("knife", StringComparison.OrdinalIgnoreCase) || 
                   weaponName.Contains("bayonet", StringComparison.OrdinalIgnoreCase);
        }

        private HookResult OnBuyCommand(CCSPlayerController? player, CommandInfo cmd)
        {
            if (player == null || !player.IsValid || player.TeamNum < 2)
                return HookResult.Continue;

            if (cmd.ArgCount < 2) return HookResult.Continue;

            string weaponName = cmd.GetArg(1)
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            if (!IsWeaponAllowed(weaponName))
            {
                player.PrintToChat(" [Zeus Lockdown] Only Zeus and Utility are allowed.");
                return HookResult.Stop; 
            }

            return HookResult.Continue;
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

        // --- NEW LOGIC: Give Taser on Spawn ---
        private HookResult OnPlayerSpawn(EventPlayerSpawn ev, GameEventInfo info)
        {
            var player = ev.Userid;

            if (player == null || !player.IsValid || player.TeamNum < 2) return HookResult.Continue;

            // Use NextFrame to ensure the game has finished its default weapon distribution
            Server.NextFrame(() =>
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive) return;

                var weaponServices = player.PlayerPawn.Value?.WeaponServices;
                if (weaponServices == null) return;

                bool hasTaser = false;

                // Check if they already have a Taser to prevent giving them duplicates
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

                // Give them a Taser with their equipped skin if they don't have one
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
                // Use env_spark to natively generate electrical sparks without .vpcf files
                var spark = Utilities.CreateEntityByName<CBaseEntity>("env_spark");
                if (spark == null || !spark.IsValid) return;

                spark.Teleport(deathPos, new QAngle(0, 0, 0), new Vector(0, 0, 0));

                spark.DispatchSpawn();
                
                // SparkOnce triggers the visual effect
                spark.AcceptInput("SparkOnce");
                
                // Emit the requested zap sound
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
            string weaponName = ev.Item
                .ToLowerInvariant()
                .Replace("weapon_", "")
                .Replace("item_", "")
                .Trim();

            if (!IsWeaponAllowed(weaponName))
            {
                var player = ev.Userid;
                if (player != null && player.IsValid && player.PlayerPawn.Value != null)
                {
                    var weaponServices = player.PlayerPawn.Value.WeaponServices;

                    foreach (var handle in weaponServices?.MyWeapons ?? Enumerable.Empty<CHandle<CBasePlayerWeapon>>())
                    {
                        var weapEnt = handle.Value;
                        if (weapEnt != null && weapEnt.IsValid)
                        {
                            string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                            if (className == weaponName)
                            {
                                weapEnt.Remove();
                                break;
                            }
                        }
                    }
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
                    string className = weapEnt.DesignerName.Replace("weapon_", "").ToLowerInvariant();
                    if (!IsWeaponAllowed(className))
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

