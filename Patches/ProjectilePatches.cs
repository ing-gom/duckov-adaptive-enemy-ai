using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Duckov;
using AdaptiveEnemyAI.Services;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// Maintains active projectile list so AI can detect "incoming projectiles" and attempt dodge dash.
    /// </summary>
    public static class ProjectilePatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.Projectile";

        private static Harmony? _harmony;
        private static bool _applied;

        /// <summary>Debug log throttle for Init Postfix calls.</summary>
        private static float _lastInitLogTime = -999f;

        /// <summary>Currently active projectiles (added on Init, removed on Release).</summary>
        internal static readonly List<global::Projectile> ActiveProjectiles = new List<global::Projectile>();

        /// <summary>Player-team projectiles only (added when team==player on Init, removed on Release). Used for dodge check without full iteration every frame.</summary>
        internal static readonly List<global::Projectile> PlayerTeamProjectiles = new List<global::Projectile>();

        /// <summary>Used when accessing ActiveProjectiles / PlayerTeamProjectiles.</summary>
        internal static readonly object ProjectilesLock = new object();

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);

                var initMethod = AccessTools.Method(typeof(global::Projectile), "Init", new[] { typeof(global::ProjectileContext) });
                if (initMethod == null)
                {
                    Debug.LogWarning("[AdaptiveEnemyAI] Projectile.Init(ProjectileContext) method not found, skipping projectile patch. Dodge detection will not work.");
                    return;
                }
                var initPostfix = AccessTools.Method(typeof(ProjectileInitPatch), nameof(ProjectileInitPatch.Postfix));
                _harmony.Patch(initMethod, postfix: new HarmonyMethod(initPostfix));

                var releaseMethod = AccessTools.Method(typeof(global::Projectile), "Release");
                if (releaseMethod != null)
                {
                    var releasePrefix = AccessTools.Method(typeof(ProjectileReleasePatch), nameof(ProjectileReleasePatch.Prefix));
                    _harmony.Patch(releaseMethod, prefix: new HarmonyMethod(releasePrefix));
                }

                _applied = true;
#if DEBUG
                Debug.Log("[AdaptiveEnemyAI] Projectile Init/Release patch applied (projectile dodge detection available)");
#endif
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdaptiveEnemyAI] ProjectilePatches.ApplyPatches: {ex.Message}");
            }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _applied = false;
                ActiveProjectiles.Clear();
                PlayerTeamProjectiles.Clear();
            }
            catch (System.Exception)
            {
                // Ignore unpatch errors in release.
            }
        }

        /// <summary>Detection timing: right after Init(ProjectileContext) called when game fires. Dodge check triggers same frame. See docs for details.</summary>
        /// <remarks>Game original signature is Init(ProjectileContext _context), so parameter name must be _context for Harmony to match.</remarks>
        public static class ProjectileInitPatch
        {
            public static void Postfix(global::Projectile __instance, global::ProjectileContext _context)
            {
                if (__instance == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                lock (ProjectilesLock)
                {
                    if (!ActiveProjectiles.Contains(__instance))
                        ActiveProjectiles.Add(__instance);
                    if (_context.team == Teams.player && !PlayerTeamProjectiles.Contains(__instance))
                        PlayerTeamProjectiles.Add(__instance);
                }
                if (AICharacterControllerPatches.DebugLogIncomingDodge && Time.time - _lastInitLogTime >= 0.25f)
                {
                    _lastInitLogTime = Time.time;
                    lock (ProjectilesLock) { Debug.Log($"[AdaptiveEnemyAI] Projectile.Init Postfix called, Active projectiles={ActiveProjectiles.Count}"); }
                }
                AICharacterControllerPatches.NotifyProjectileSpawned();
            }
        }

        public static class ProjectileReleasePatch
        {
            public static void Prefix(global::Projectile __instance)
            {
                if (__instance == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                lock (ProjectilesLock)
                {
                    ActiveProjectiles.Remove(__instance);
                    PlayerTeamProjectiles.Remove(__instance);
                    if (AICharacterControllerPatches.DebugLogIncomingDodge && Time.time - _lastInitLogTime >= 0.5f)
                    {
                        _lastInitLogTime = Time.time;
                        Debug.Log($"[AdaptiveEnemyAI] Projectile.Release: after remove Active={ActiveProjectiles.Count}");
                    }
                }
            }
        }
    }
}
