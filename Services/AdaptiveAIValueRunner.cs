using UnityEngine;
using Duckov.Scenes;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Patches;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Services
{
    /// <summary>
    /// Values (e.g. baseReactionTime) are applied once on spawn and kept. Projectile dodge is triggered mainly on "gun fire (Projectile.Init)"; canDash refreshed every 1s. Melee swings use fallback interval.
    /// </summary>
    public sealed class AdaptiveAIValueRunner : MonoBehaviour
    {
        private const float ParamsApplyInterval = 1f; // refresh adaptive params (canDash etc.) every 1s
        private const float DodgeCheckFallbackInterval = 0.45f; // fallback dodge check when no projectiles (melee etc.)
        /// <summary>Interval (s) each AI checks nearby bullets in real time while projectiles are flying. Shorter = more accurate; 0.05 = 20/s.</summary>
        private const float DodgeCheckRealtimeInterval = 0.05f;
        private const float DodgeCheckThrottle = 0.08f; // throttle (s) on consecutive triggers on fire
        /// <summary>Tactical dash (disorient player while moving) check interval. Probability and cooldown per AI at this interval.</summary>
        private const float TacticalDashCheckInterval = 1.2f;

        private float _lastParamsApplyTime = -999f;
        private float _lastDodgeCheckOnSpawnTime = -999f;
        private float _lastDodgeFallbackTime = -999f;
        private float _lastTacticalDashCheckTime = -999f;

        private void OnEnable()
        {
            InvokeRepeating(nameof(TickParamsAndFallbackDodge), 0.1f, 0.1f); // tick every 0.1s (param refresh + fallback)
            AICharacterControllerPatches.OnProjectileSpawned += OnProjectileSpawned;
            PlayerLoadoutService.OnLoadoutChanged += OnLoadoutChanged;
        }

        /// <summary>When projectiles exist, each AI checks nearby bullets in real time. Called every frame; full dodge check at RealtimeInterval.</summary>
        private void Update()
        {
            if (!LevelManager.LevelInited || MultiSceneCore.Instance == null)
            {
                if (PlayerBehaviorCollector.GetSpawnedEnemyCount() > 0)
                    PlayerBehaviorCollector.ClearSpawnedEnemyAIRegistry();
                return;
            }
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (PlayerBehaviorCollector.GetSpawnedEnemyCount() == 0) return;

            int count = 0;
            lock (ProjectilePatches.ProjectilesLock)
                count = ProjectilePatches.PlayerTeamProjectiles.Count;
            if (count == 0) return;

            float now = Time.time;
            if (now - _lastDodgeFallbackTime >= DodgeCheckRealtimeInterval)
            {
                _lastDodgeFallbackTime = now;
                RunDodgeCheckForAllEnemies();
            }
        }

        private void OnDisable()
        {
            CancelInvoke();
            AICharacterControllerPatches.OnProjectileSpawned -= OnProjectileSpawned;
            PlayerLoadoutService.OnLoadoutChanged -= OnLoadoutChanged;
        }

        /// <summary>On loadout change, reapply by new loadout only to currently spawned enemies.</summary>
        private void OnLoadoutChanged()
        {
            if (!LevelManager.LevelInited || MultiSceneCore.Instance == null) return;
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (PlayerBehaviorCollector.GetSpawnedEnemyCount() == 0) return;
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                AICharacterControllerPatches.ReapplyParamsAndCacheForLoadoutChange(ai);
            });
        }

        /// <summary>Called on gun fire (projectile spawn). Throttle then full dodge check.</summary>
        private void OnProjectileSpawned()
        {
            if (PlayerBehaviorCollector.IsInBase()) return;
            float now = Time.time;
            if (now - _lastDodgeCheckOnSpawnTime < DodgeCheckThrottle)
                return;
            _lastDodgeCheckOnSpawnTime = now;
            RunDodgeCheckForAllEnemies();
        }

        private void TickParamsAndFallbackDodge()
        {
            if (!LevelManager.LevelInited || MultiSceneCore.Instance == null)
            {
                if (PlayerBehaviorCollector.GetSpawnedEnemyCount() > 0)
                    PlayerBehaviorCollector.ClearSpawnedEnemyAIRegistry();
                return;
            }
            if (PlayerBehaviorCollector.IsInBase())
                return;
            if (PlayerBehaviorCollector.GetSpawnedEnemyCount() == 0)
                return;

            float now = Time.time;
            // Pre-compute cooldown flags once (avoid redundant checks per AI)
            bool doParamsRefresh = now - _lastParamsApplyTime >= ParamsApplyInterval;
            if (doParamsRefresh) _lastParamsApplyTime = now;

            bool doTacticalDash = AdaptiveAISettings.TacticalDashEnabled
                && now - _lastTacticalDashCheckTime >= TacticalDashCheckInterval;
            if (doTacticalDash) _lastTacticalDashCheckTime = now;

            bool doTacticOutcome = AdaptiveAISettings.TacticOutcomeTrackingEnabled;

            // Pre-compute player HP once for tactic outcome (shared across all AIs)
            float playerHp = 1f;
            if (doTacticOutcome)
            {
                var mainHealth = CharacterMainControl.Main?.Health;
                if (mainHealth != null && mainHealth.MaxHealth > 0f)
                    playerHp = Mathf.Clamp01(mainHealth.CurrentHealth / mainHealth.MaxHealth);
            }

            // Single iteration: all per-AI work consolidated. try-catch per AI so one failure doesn't skip remaining AIs.
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                try
                {
                    AICharacterControllerPatches.EnsureAdaptiveParamsApplied(ai);

                    if (!PerAIPatchControl.IsExcludedFromAdaptivePatches(ai))
                    {
                        AICharacterControllerPatches.ApplyMeleeAttackTreeByRange(ai);
                        AICharacterControllerPatches.StopMeleeAttackWhenOutOfRange(ai);
                        AICharacterControllerPatches.TryMeleeAttackWhenInRange(ai);
                        AICharacterControllerPatches.ApplyMeleeShootDelayByRange(ai);
                    }

                    if (doParamsRefresh)
                    {
                        AICharacterControllerPatches.RefreshCanDashFromDistance(ai);
                        AICharacterControllerPatches.TryLeaveCoverDash(ai);
                        AICharacterControllerPatches.TryApproachDash(ai);
                    }

                    if (doTacticalDash)
                        AICharacterControllerPatches.TryTacticalDisorientationDodge(ai);

                    if (doTacticOutcome)
                    {
                        if (!TacticOutcomeTracker.TryGetFor(ai, out var tracker)) return;
                        var health = ai.CharacterMainControl?.Health;
                        float aiHp = (health != null && health.MaxHealth > 0f)
                            ? Mathf.Clamp01(health.CurrentHealth / health.MaxHealth) : 1f;
                        tracker.UpdatePendingOutcome(aiHp, playerHp);
                    }
                }
                catch (System.Exception) { }
            });

            // fallback: dodge check here only when no projectiles (melee etc.). With projectiles, Update() checks at 0.05s realtime interval
            int playerProjectileCount = 0;
            lock (ProjectilePatches.ProjectilesLock)
                playerProjectileCount = ProjectilePatches.PlayerTeamProjectiles.Count;
            if (playerProjectileCount == 0 && now - _lastDodgeFallbackTime >= DodgeCheckFallbackInterval)
            {
                _lastDodgeFallbackTime = now;
                RunDodgeCheckForAllEnemies();
            }
        }

        private void RunDodgeCheckForAllEnemies()
        {
            if (PlayerBehaviorCollector.IsInBase()) return;
            if (PlayerBehaviorCollector.GetSpawnedEnemyCount() == 0) return;
            PlayerBehaviorCollector.ForEachSpawnedEnemyAI(ai =>
            {
                if (ai == null) return;
                AICharacterControllerPatches.CheckIncomingProjectileAndTryDodge(
                    ai, AICharacterControllerPatches.GetCachedAggressionFor(ai));
            });
        }
    }
}
