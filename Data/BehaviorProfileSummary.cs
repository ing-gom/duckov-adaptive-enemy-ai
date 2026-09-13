using UnityEngine;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Summary stats of player behavior and combat pattern. For AI adjustment and save.
    /// </summary>
    public sealed class BehaviorProfileSummary
    {
        /// <summary>Move strength EMA (0~1 approx).</summary>
        public float MoveStrengthEma { get; set; }

        /// <summary>Run ratio EMA (0~1).</summary>
        public float RunRatioEma { get; set; }

        /// <summary>Aim change variance (Welford).</summary>
        public float AimChangeVariance { get; set; }

        /// <summary>Dashes per minute by combat time (accumulated dashes / combat minutes).</summary>
        public float DashCountPerMin { get; set; }

        /// <summary>Shoots per minute by combat time (accumulated shoots / combat minutes).</summary>
        public float ShootCountPerMin { get; set; }

        /// <summary>Aim sample count. For validity check.</summary>
        public int AimSampleCount { get; set; }

        // ---- Combat pattern (distance, count, crit; damage amount unused) ----
        /// <summary>PvE: average distance when I hit enemy (m).</summary>
        public float PlayerToEnemyAvgDistanceEma { get; set; }
        /// <summary>PvE: hits per minute.</summary>
        public float PlayerToEnemyHitsPerMin { get; set; }
        /// <summary>PvE: crits per minute.</summary>
        public float PlayerToEnemyCritsPerMin { get; set; }
        /// <summary>EvP: player hits taken per minute.</summary>
        public float EnemyToPlayerHitsPerMin { get; set; }
        /// <summary>EvP: average distance when enemy hits me (aux).</summary>
        public float EnemyToPlayerAvgDistanceEma { get; set; }
        /// <summary>Whether PvE combat samples exist.</summary>
        public bool HasCombatSamplesP2E { get; set; }
        /// <summary>Whether EvP combat samples exist.</summary>
        public bool HasCombatSamplesE2P { get; set; }

        // ---- Movement pattern (for strafing/zigzag) ----
        /// <summary>Move direction · enemy direction dot EMA (-1~1). Approach(+), retreat(-), lateral(0).</summary>
        public float MoveDirDotToEnemyEma { get; set; }
        /// <summary>Lateral component ratio EMA (0~1). Strafe strength.</summary>
        public float LateralMoveRatioEma { get; set; }
        /// <summary>Move·enemy dot variance. Zigzag/variation strength.</summary>
        public float MoveDirDotVariance { get; set; }

        // ---- Player disadvantage (2.1 HP ratio, 2.4 concurrent enemy count) ----
        /// <summary>Combat player HP ratio EMA (0~1). Lower = player disadvantage → positive aggression.</summary>
        public float PlayerHealthRatioEma { get; set; }
        /// <summary>Enemy count within range of player EMA. Higher = many-vs-one = player disadvantage → positive aggression.</summary>
        public float EnemyCountNearPlayerEma { get; set; }

        // ---- v1.1.8: Reload timing, threat response, exchange ratio ----
        /// <summary>Risky reload ratio EMA (0~1). Higher = stronger AI approach during reload.</summary>
        public float ReloadRiskRatioEma { get; set; }
        /// <summary>Threat response retreat tendency EMA (0~1, 1=retreat type). Retreat type = stronger push right after hit.</summary>
        public float ThreatResponseRetreatTendencyEma { get; set; }
        /// <summary>Exchange ratio EMA: (PvE hits/min) / (EvP hits/min + ε). &lt;1 = disadvantage → positive aggression.</summary>
        public float ExchangeRatioEma { get; set; } = 1f;
        /// <summary>Last EvP hit time (Time.time). For recent-N-second hit check and post-hit push.</summary>
        public float LastEnemyToPlayerHitTime { get; set; } = -999f;

        // ---- Short-term (last N seconds) for "this engagement" correction ----
        /// <summary>Dashes per minute in last ShortTermWindowSeconds. High = player dashing a lot → AI evasion boost.</summary>
        public float ShortTermDashesPerMin { get; set; }
        /// <summary>Reload risk EMA in last window (0~1). High = player reload/cover a lot → AI approach boost.</summary>
        public float ShortTermReloadRiskEma { get; set; } = 0.5f;
        /// <summary>Move·enemy dot EMA in last window (-1~1). High = straight approach → defensive stance boost.</summary>
        public float ShortTermMoveDirDotEma { get; set; }

        /// <summary>Additive aggression correction from short-term window. Player dash heavy → +, reload/cover heavy → +.</summary>
        public float GetShortTermAggressionCorrection()
        {
            float cap = Mathf.Clamp(AdaptiveAISettings.ShortTermAggressionCorrectionCap, 0f, 0.5f);
            float corr = 0f;
            float dashNorm = Mathf.Clamp01(ShortTermDashesPerMin / 6f);
            corr += (dashNorm - 0.5f) * 0.4f;
            corr += (ShortTermReloadRiskEma - 0.5f) * 0.5f;
            return Mathf.Clamp(corr, -cap, cap);
        }

        /// <summary>Additive defensive stance correction from short-term window. Player straight approach in last Ns → +.</summary>
        public float GetShortTermDefensiveStanceCorrection()
        {
            float cap = Mathf.Clamp(AdaptiveAISettings.ShortTermDefensiveStanceCorrectionCap, 0f, 0.5f);
            float approachScore = (ShortTermMoveDirDotEma + 1f) * 0.5f;
            float corr = (approachScore - 0.5f) * 0.8f;
            return Mathf.Clamp(corr, -cap, cap);
        }

        /// <summary>Tactical mode index from aggression (0~1) and defensive stance (0~1). 0=conservative, 1=neutral, 2=aggressive. Used for tier-based parameter bundles.</summary>
        public static int GetTacticalModeIndex(float aggression01, float defensiveStance01)
        {
            float score = Mathf.Clamp01(aggression01) - Mathf.Clamp01(defensiveStance01);
            if (score < -0.33f) return 0;
            if (score > 0.33f) return 2;
            return 1;
        }

        /// <summary>Aggression factor [-0.5, 0.5]. Behavior 40% (move, run, dash) + combat 60% (distance, hits, crits, taken) + player disadvantage (HP, enemy count). Includes short-term correction.</summary>
        public float GetBehaviorAggressionFactor()
        {
            // Behavior block 40%: move, run, dash, aim change
            float behaviorPart = 0f;
            behaviorPart += (MoveStrengthEma - 0.5f) * 0.5f;
            behaviorPart += (RunRatioEma - 0.5f) * 0.4f;
            float dashNorm = Mathf.Clamp01(DashCountPerMin / 6f);
            behaviorPart += (dashNorm - 0.5f) * 0.3f;

            // Aim variance: higher = closer/aggressive estimate → positive
            float aimNorm = Mathf.Clamp01(AimChangeVariance / AdaptiveAISettings.AimVarianceRef);
            behaviorPart += (aimNorm - 0.5f) * 0.2f;

            float f = 0.4f * behaviorPart;

            const float refDistanceP2E = 15f;

            if (HasCombatSamplesP2E)
            {
                // Combat block 60%: PvE distance (weight 0.25)
                if (PlayerToEnemyAvgDistanceEma < refDistanceP2E)
                    f += 0.25f * (1f - PlayerToEnemyAvgDistanceEma / refDistanceP2E);
                else
                    f -= 0.25f * Mathf.Clamp01((PlayerToEnemyAvgDistanceEma - refDistanceP2E) / refDistanceP2E);

                // PvE hits/min: weight 0.15 (ref ~60 hits/min)
                float hitsNorm = Mathf.Clamp01(PlayerToEnemyHitsPerMin / 60f);
                f += 0.15f * (hitsNorm - 0.5f);

                // Crit aux: weight 0.1 (ref ~15 crit/min)
                float critsNorm = Mathf.Clamp01(PlayerToEnemyCritsPerMin / 15f);
                f += 0.1f * (critsNorm - 0.5f);

                // Shoots/min: higher = more aggressive (weight 0.12)
                float shootsNorm = Mathf.Clamp01(ShootCountPerMin / AdaptiveAISettings.ShootCountPerMinRef);
                f += 0.12f * (shootsNorm - 0.5f);
            }

            if (HasCombatSamplesE2P)
            {
                // EvP hits/min: weight 0.1 (ref ~30 hits/min)
                float takenHitsNorm = Mathf.Clamp01(EnemyToPlayerHitsPerMin / 30f);
                f += 0.1f * takenHitsNorm;
            }

            // Player disadvantage: lower HP ratio = more aggression (weight 0.15)
            f += (1f - Mathf.Clamp01(PlayerHealthRatioEma)) * AdaptiveAISettings.PlayerDisadvantageHealthWeight;
            // Player disadvantage: +N% aggression per enemy, cap at Cap% (contribute in 0~1 then map to factor)
            float perEnemy = Mathf.Max(0.0001f, AdaptiveAISettings.EnemyCountAggressionPerEnemy);
            float cap = Mathf.Clamp01(AdaptiveAISettings.EnemyCountAggressionCap);
            float maxCountForCap = cap / perEnemy;
            float enemyContrib01 = Mathf.Min(EnemyCountNearPlayerEma, maxCountForCap) * perEnemy;

            float range = AdaptiveAISettings.BehaviorAggressionRange > 0f
                ? AdaptiveAISettings.BehaviorAggressionRange
                : 0.5f;
            f += enemyContrib01 * 2f * range;

            // v1.1.8: Slight aggression boost when exchange ratio unfavorable
            if (ExchangeRatioEma < 1f)
                f += (1f - Mathf.Clamp01(ExchangeRatioEma)) * AdaptiveAISettings.ExchangeRatioAggressionWeight;

            f += GetShortTermAggressionCorrection();
            return Mathf.Clamp(f, -range, range);
        }

        /// <summary>
        /// Player-pattern defensive stance 0~1. 1=defensive (hold cover, retreat), 0=aggressive (leave cover, approach).
        /// ↑ when player straight approach, high accuracy, exchange ratio advantage; ↓ when retreat/camping, reload risk.
        /// Returns 0.5 when insufficient samples.
        /// </summary>
        public float GetDefensiveStanceFactor()
        {
            if (!HasCombatSamplesP2E && AimSampleCount < 2)
                return 0.5f;

            float raw = 0.5f;
            const float approachWeight = 0.22f;
            const float lateralWeight = 0.12f;
            const float hitRateWeight = 0.18f;
            const float aimStableWeight = 0.12f;
            const float exchangeWeight = 0.18f;
            const float retreatWeight = 0.18f;
            const float campingWeight = 0.12f;
            const float reloadWeight = 0.15f;

            // Player straight approach: higher MoveDirDotToEnemyEma (toward enemy) → defense ↑
            float approachScore = (MoveDirDotToEnemyEma + 1f) * 0.5f;
            raw += approachWeight * approachScore;

            // Lower lateral ratio = straight approach → defense ↑
            raw += lateralWeight * (1f - Mathf.Clamp01(LateralMoveRatioEma));

            // High accuracy/aim type: higher hit rate, more stable aim → defense ↑
            if (HasCombatSamplesP2E && ShootCountPerMin > 0.1f)
            {
                float hitRate = Mathf.Clamp01(PlayerToEnemyHitsPerMin / (ShootCountPerMin * 0.6f + 1f));
                raw += hitRateWeight * hitRate;
            }
            float aimVarRef = Mathf.Max(0.01f, AdaptiveAISettings.AimVarianceRef);
            float aimStable = 1f - Mathf.Clamp01(AimChangeVariance / aimVarRef);
            raw += aimStableWeight * aimStable;

            // Exchange ratio > 1 (player advantage) → defense ↑
            if (ExchangeRatioEma > 1f)
                raw += exchangeWeight * Mathf.Clamp01((ExchangeRatioEma - 1f) / 2f);

            // Player retreat: low MoveDirDotToEnemyEma (away from enemy) → defense ↓
            float retreatScore = MoveDirDotToEnemyEma < 0f ? -MoveDirDotToEnemyEma : 0f;
            raw -= retreatWeight * retreatScore;

            // Camping: low move strength → defense ↓
            raw -= campingWeight * (1f - Mathf.Clamp01(MoveStrengthEma));

            // Higher reload/low-DPS risk = AI keeps approach → defense ↓
            raw -= reloadWeight * Mathf.Clamp01(ReloadRiskRatioEma);

            raw += GetShortTermDefensiveStanceCorrection();
            return Mathf.Clamp01(raw);
        }
    }
}
