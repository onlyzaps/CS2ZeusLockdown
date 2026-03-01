using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Evasive Brawler)";
        public override string ModuleVersion => "1.5.0";
        private CounterStrikeSharp.API.Modules.Timers.Timer? botAiTimer;
        
        private readonly HashSet<uint> reactingBots = new HashSet<uint>();
        private readonly Random random = new Random();

        // State machine tracking for smooth OnTick movement
        private readonly Dictionary<uint, float> strafeTimers = new Dictionary<uint, float>();
        private readonly Dictionary<uint, ulong> strafeDirections = new Dictionary<uint, ulong>();
        private readonly Dictionary<uint, ulong> activeMovementOverrides = new Dictionary<uint, ulong>();

        public override void Load(bool hotReload)
        {
            // The "Brain": Runs 10x a second to process complex math and target selection
            botAiTimer = AddTimer(0.1f, RunBotScanner, TimerFlags.REPEAT);
            
            // The "Muscles": Runs 64x a second to forcefully hold down WASD keys smoothly
            RegisterListener<Listeners.OnTick>(ForceBotMovement);
            
            Console.WriteLine("[Zeus Bot AI] Close-quarters evasive strafing loaded.");
        }

        private void ForceBotMovement()
        {
            foreach (var kvp in activeMovementOverrides)
            {
                if (kvp.Value == 0) continue; // If mask is 0, let standard Bot AI take over

                var bot = Utilities.GetPlayerFromIndex((int)kvp.Key);
                if (bot != null && bot.IsValid && bot.PawnIsAlive && bot.PlayerPawn.Value?.MovementServices != null)
                {
                    // Clear the standard Bot WASD keys so they don't fight our inputs
                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~((ulong)PlayerButtons.Forward | (ulong)PlayerButtons.Back | (ulong)PlayerButtons.Moveleft | (ulong)PlayerButtons.Moveright);
                    
                    // Jam our calculated evasive movement keys down
                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] |= kvp.Value;
                }
            }
        }

        private void RunBotScanner()
        {
            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();
            var aliveEnemies = players.Where(p => p != null && p.IsValid && p.PawnIsAlive && !p.IsBot).ToList();

            if (!bots.Any()) return;

            foreach (var bot in bots)
            {
                var botPawn = bot.PlayerPawn.Value;
                if (botPawn == null) continue;

                var target = GetHighestPriorityTarget(bot, botPawn, aliveEnemies);
                if (target == null) 
                {
                    activeMovementOverrides[bot.Index] = 0; // Release movement control
                    continue;
                }

                var targetPawn = target.PlayerPawn.Value;
                if (targetPawn == null) continue;

                var botOrigin = botPawn.AbsOrigin;
                var targetOrigin = targetPawn.AbsOrigin;
                if (botOrigin == null || targetOrigin == null) continue;

                float distance = (botOrigin - targetOrigin).Length();
                ulong currentMovementMask = 0;

                // THE BRAWL ZONE (Increased from 500 to 600 to start evasive maneuvers earlier)
                if (distance <= 600.0f)
                {
                    EnsureBotHasAndHoldsZeus(bot, botPawn);

                    // 1. STRAFE LOGIC: Pick a direction and hold it, swap erratically to dodge bullets
                    if (!strafeTimers.TryGetValue(bot.Index, out float nextStrafe) || Server.CurrentTime > nextStrafe)
                    {
                        strafeDirections[bot.Index] = random.NextDouble() > 0.5 ? (ulong)PlayerButtons.Moveleft : (ulong)PlayerButtons.Moveright;
                        // Hold this strafe for 0.3 to 1.2 seconds before zig-zagging the other way
                        strafeTimers[bot.Index] = Server.CurrentTime + (random.NextSingle() * 0.9f + 0.3f);
                    }
                    currentMovementMask |= strafeDirections[bot.Index];

                    // 2. GAP CONTROL: Maintain the Zeus sweet spot
                    if (distance > 160.0f) 
                    {
                        currentMovementMask |= (ulong)PlayerButtons.Forward; // Diagonal push
                    }
                    else if (distance < 110.0f) 
                    {
                        currentMovementMask |= (ulong)PlayerButtons.Back; // Backpedal to avoid phasing through the player
                    }

                    // 3. CHAOTIC JUMP DODGES: 5% chance per tick to jump while strafing to mess up headshot tracking
                    if (random.NextDouble() < 0.05 && botPawn.MovementServices != null)
                    {
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                        AddTimer(0.1f, () => {
                            if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                                bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
                        });
                        AddTimer(0.5f, () => {
                            if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                            {
                                bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Jump;
                                bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Duck;
                            }
                        });
                    }

                    // 4. PULL THE TRIGGER
                    if (distance <= 170.0f && !reactingBots.Contains(bot.Index))
                    {
                        StartHumanReaction(bot, botPawn, targetPawn);
                    }
                }

                // Save our calculated movement keys to be injected by the OnTick method
                activeMovementOverrides[bot.Index] = currentMovementMask;
            }
        }

        private void EnsureBotHasAndHoldsZeus(CCSPlayerController bot, CCSPlayerPawn botPawn)
        {
            var weaponServices = botPawn.WeaponServices;
            if (weaponServices == null) return;

            bool hasZeus = false;
            uint taserHandleRaw = 0;

            if (weaponServices.MyWeapons != null)
            {
                foreach (var weaponHandle in weaponServices.MyWeapons)
                {
                    var weapon = weaponHandle.Value;
                    if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains("taser"))
                    {
                        hasZeus = true;
                        taserHandleRaw = weaponHandle.Raw;
                        break;
                    }
                }
            }

            if (!hasZeus)
            {
                bot.GiveNamedItem("weapon_taser");
            }
            else
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.DesignerName != null && !activeWeapon.DesignerName.Contains("taser"))
                {
                    weaponServices.ActiveWeapon.Raw = taserHandleRaw;
                    Utilities.SetStateChanged(botPawn, "CBasePlayerPawn", "m_pWeaponServices");
                }
            }
        }

        private CCSPlayerController? GetHighestPriorityTarget(CCSPlayerController bot, CCSPlayerPawn botPawn, List<CCSPlayerController> enemies)
        {
            CCSPlayerController? bestTarget = null;
            float highestThreatScore = -float.MaxValue;

            var botPos = botPawn.AbsOrigin;
            var botAngles = botPawn.EyeAngles;
            if (botPos == null || botAngles == null) return null;

            Vector botForward = GetForwardVector(botAngles);

            foreach (var enemy in enemies)
            {
                if (enemy.TeamNum == bot.TeamNum) continue;
                
                var enemyPawn = enemy.PlayerPawn.Value;
                if (enemyPawn == null) continue;
                
                var enemyPos = enemyPawn.AbsOrigin;
                var enemyAngles = enemyPawn.EyeAngles;
                if (enemyPos == null || enemyAngles == null) continue;

                float distance = (botPos - enemyPos).Length();
                if (distance > 1500.0f) continue; 

                float threatScore = 0;
                threatScore += (1500.0f - distance);

                Vector dirToEnemy = GetNormalizedVector(botPos, enemyPos);
                Vector dirToBot = GetNormalizedVector(enemyPos, botPos);
                Vector enemyForward = GetForwardVector(enemyAngles);

                // BRAWLER OVERRIDE: If an enemy is actively aiming at the bot, they are priority #1 so we can strafe their shots
                float enemyDot = DotProduct(enemyForward, dirToBot);
                if (enemyDot > 0.85f) threatScore += 1200.0f; 
                else if (enemyDot > 0.5f) threatScore += 400.0f; 

                float botDot = DotProduct(botForward, dirToEnemy);
                if (botDot > 0.7f) threatScore += 400.0f; 
                else if (botDot < 0.0f) threatScore -= 300.0f; 

                var weaponServices = enemyPawn.WeaponServices;
                if (weaponServices?.ActiveWeapon?.Value != null)
                {
                    string weaponName = weaponServices.ActiveWeapon.Value.DesignerName ?? "";
                    if (weaponName.Contains("grenade") || weaponName.Contains("flashbang") || weaponName.Contains("smokegrenade") || weaponName.Contains("decoy") || weaponName.Contains("molotov") || weaponName.Contains("incgrenade") || weaponName.Contains("c4"))
                        threatScore -= 600.0f; // Brawlers ignore defenseless targets to focus immediate threats
                    else if (weaponName.Contains("knife"))
                        threatScore -= 150.0f; 
                    else 
                        threatScore += 300.0f; 
                }

                // If someone enters the kill zone, commit immediately
                if (distance <= 200.0f) threatScore += 3000.0f; 

                if (threatScore > highestThreatScore)
                {
                    highestThreatScore = threatScore;
                    bestTarget = enemy;
                }
            }
            return bestTarget;
        }

        private void StartHumanReaction(CCSPlayerController bot, CCSPlayerPawn botPawn, CCSPlayerPawn targetPawn)
        {
            uint botIndex = bot.Index;
            reactingBots.Add(botIndex);

            var botPos = botPawn.AbsOrigin;
            var targetPos = targetPawn.AbsOrigin;
            var botAngles = botPawn.EyeAngles;

            if (botPos == null || targetPos == null || botAngles == null)
            {
                reactingBots.Remove(botIndex);
                return;
            }

            float deltaX = targetPos.X - botPos.X;
            float deltaY = targetPos.Y - botPos.Y;
            float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);

            float currentYaw = botAngles.Y;
            float yawDifference = Math.Abs(perfectYaw - currentYaw);
            if (yawDifference > 180.0f) yawDifference = 360.0f - yawDifference;

            float baseReaction = (random.NextSingle() * 0.10f) + 0.10f;
            float flickPenalty = (yawDifference / 180.0f) * 0.15f; 
            float reactionTime = baseReaction + flickPenalty;

            AddTimer(reactionTime, () =>
            {
                reactingBots.Remove(botIndex);

                if (!bot.IsValid || !bot.PawnIsAlive || !targetPawn.IsValid) return;

                var newBotPos = botPawn.AbsOrigin;
                var newTargetPos = targetPawn.AbsOrigin;
                if (newBotPos == null || newTargetPos == null) return;

                float distance = (newBotPos - newTargetPos).Length();
                if (distance > 185.0f) return;

                deltaX = newTargetPos.X - newBotPos.X;
                deltaY = newTargetPos.Y - newBotPos.Y;
                float deltaZ = (newTargetPos.Z + 40.0f) - (newBotPos.Z + 40.0f); 

                perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                float inaccuracyScale = 1.0f + ((yawDifference / 180.0f) * 3.0f);
                float panicYaw = perfectYaw + ((random.NextSingle() * (inaccuracyScale * 2)) - inaccuracyScale);
                float panicPitch = perfectPitch + ((random.NextSingle() * (inaccuracyScale * 2)) - inaccuracyScale);

                var newAngles = new QAngle(panicPitch, panicYaw, 0);
                botPawn.Teleport(newBotPos, newAngles, new Vector(0, 0, 0));

                if (botPawn.MovementServices != null)
                {
                    botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                    
                    AddTimer(0.05f, () => 
                    { 
                        if (bot.IsValid)
                        {
                            var currentPawn = bot.PlayerPawn.Value;
                            if (currentPawn != null && currentPawn.IsValid && currentPawn.MovementServices != null)
                            {
                                currentPawn.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Attack; 
                            }
                        }
                    });
                }
            });
        }

        private Vector GetForwardVector(QAngle angles)
        {
            float pitchRad = angles.X * (float)(Math.PI / 180.0);
            float yawRad = angles.Y * (float)(Math.PI / 180.0);
            return new Vector(
                (float)(Math.Cos(yawRad) * Math.Cos(pitchRad)),
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)(-Math.Sin(pitchRad))
            );
        }

        private Vector GetNormalizedVector(Vector from, Vector to)
        {
            Vector dir = new Vector(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            float length = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (length == 0) return new Vector(0, 0, 0);
            dir.X /= length;
            dir.Y /= length;
            dir.Z /= length;
            return dir;
        }

        private float DotProduct(Vector v1, Vector v2)
        {
            return (v1.X * v2.X) + (v1.Y * v2.Y) + (v1.Z * v2.Z);
        }

        public override void Unload(bool hotReload)
        {
            botAiTimer?.Kill();
            botAiTimer = null;
            reactingBots.Clear();
            activeMovementOverrides.Clear();
            strafeTimers.Clear();
            strafeDirections.Clear();
            RemoveListener<Listeners.OnTick>(ForceBotMovement);
        }
    }
}
