using UnityEngine;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Feature 5: Multi-axis adaptation.
    /// Replaces the single aggression value with 3 independent axes:
    /// - Aggression (0~1): approach vs retreat tendency
    /// - Precision (0~1): accuracy and aim quality
    /// - Reactivity (0~1): dodge speed, reaction time, movement frequency
    /// Each axis is computed from a relevant subset of behavior data and applied independently.
    /// </summary>
    public sealed class MultiAxisProfile
    {
        /// <summary>Approach vs retreat tendency (0=retreat, 0.5=neutral, 1=aggressive rush).</summary>
        public float Aggression { get; private set; } = 0.5f;

        /// <summary>Accuracy and aim quality (0=inaccurate/spray, 0.5=neutral, 1=precise sniper).</summary>
        public float Precision { get; private set; } = 0.5f;

        /// <summary>Dodge/reaction speed (0=slow/static, 0.5=neutral, 1=highly reactive/mobile).</summary>
        public float Reactivity { get; private set; } = 0.5f;

        /// <summary>Whether enough data has been collected for multi-axis computation.</summary>
        public bool IsValid { get; private set; }

        /// <summary>
        /// Compute multi-axis values from behavior summary.
        /// Called once per frame (or per summary update) to refresh axes.
        /// </summary>
        public void ComputeFrom(BehaviorProfileSummary summary)
        {
            if (summary == null || summary.AimSampleCount < 5)
            {
                IsValid = false;
                Aggression = 0.5f;
                Precision = 0.5f;
                Reactivity = 0.5f;
                return;
            }
            IsValid = true;

            // ---- Aggression axis ----
            // Primary signals: movement toward enemy, fire rate, exchange ratio (winning → more aggressive)
            float aggressionRaw = 0.5f;
            {
                // Movement approach: MoveDirDotToEnemyEma > 0 = approaching = aggressive
                float approachScore = (summary.MoveDirDotToEnemyEma + 1f) * 0.5f; // 0~1
                aggressionRaw += (approachScore - 0.5f) * 0.35f;

                // Move strength: higher = more aggressive
                aggressionRaw += (summary.MoveStrengthEma - 0.5f) * 0.2f;

                // Run ratio: running = aggressive
                aggressionRaw += (summary.RunRatioEma - 0.5f) * 0.15f;

                // Fire rate: higher fire rate = more aggressive
                float fireNorm = Mathf.Clamp01(summary.ShootCountPerMin / Mathf.Max(1f, AdaptiveAISettings.ShootCountPerMinRef));
                aggressionRaw += (fireNorm - 0.5f) * 0.15f;

                // Exchange ratio: winning (>1) = more aggressive
                if (summary.ExchangeRatioEma > 1f)
                    aggressionRaw += Mathf.Clamp01((summary.ExchangeRatioEma - 1f) / 2f) * 0.1f;
                else
                    aggressionRaw -= Mathf.Clamp01((1f - summary.ExchangeRatioEma)) * 0.1f;

                // Player HP: low HP = player more desperate/aggressive or defensive
                aggressionRaw += (1f - Mathf.Clamp01(summary.PlayerHealthRatioEma)) * 0.05f;
            }
            Aggression = Mathf.Clamp01(aggressionRaw);

            // ---- Precision axis ----
            // Primary signals: aim stability, hit rate, crit rate, engagement distance
            float precisionRaw = 0.5f;
            {
                // Aim stability: low variance = precise
                float aimVarRef = Mathf.Max(0.01f, AdaptiveAISettings.AimVarianceRef);
                float aimStable = 1f - Mathf.Clamp01(summary.AimChangeVariance / aimVarRef);
                precisionRaw += (aimStable - 0.5f) * 0.3f;

                // Hit rate: high = precise
                if (summary.HasCombatSamplesP2E && summary.ShootCountPerMin > 0.1f)
                {
                    float hitRate = Mathf.Clamp01(summary.PlayerToEnemyHitsPerMin / (summary.ShootCountPerMin * 0.6f + 1f));
                    precisionRaw += (hitRate - 0.3f) * 0.25f;
                }

                // Crit rate: high = very precise
                if (summary.HasCombatSamplesP2E && summary.PlayerToEnemyHitsPerMin > 0.1f)
                {
                    float critRatio = Mathf.Clamp01(summary.PlayerToEnemyCritsPerMin / (summary.PlayerToEnemyHitsPerMin + 1f));
                    precisionRaw += critRatio * 0.15f;
                }

                // Distance: far engagement = precision-oriented
                if (summary.HasCombatSamplesP2E)
                {
                    float distNorm = Mathf.Clamp01(summary.PlayerToEnemyAvgDistanceEma / 30f);
                    precisionRaw += (distNorm - 0.3f) * 0.15f;
                }

                // Low lateral movement = steady aim = precise
                float lateralInverse = 1f - Mathf.Clamp01(summary.LateralMoveRatioEma);
                precisionRaw += (lateralInverse - 0.5f) * 0.1f;
            }
            Precision = Mathf.Clamp01(precisionRaw);

            // ---- Reactivity axis ----
            // Primary signals: dash frequency, movement variety, threat response speed
            float reactivityRaw = 0.5f;
            {
                // Dash frequency: more dashes = more reactive
                float dashNorm = Mathf.Clamp01(summary.DashCountPerMin / 8f); // 8 dashes/min = very reactive
                reactivityRaw += (dashNorm - 0.3f) * 0.3f;

                // Move direction variance: high = unpredictable/reactive
                float varRef = Mathf.Max(0.01f, AdaptiveAISettings.MovementPatternVarianceRef);
                float dirVariety = Mathf.Clamp01(summary.MoveDirDotVariance / varRef);
                reactivityRaw += (dirVariety - 0.5f) * 0.2f;

                // Lateral movement: high = evasive
                reactivityRaw += (Mathf.Clamp01(summary.LateralMoveRatioEma) - 0.3f) * 0.2f;

                // Threat response: retreat tendency = reactive to damage
                reactivityRaw += (summary.ThreatResponseRetreatTendencyEma - 0.5f) * 0.15f;

                // Short-term dash spike: very reactive right now
                float shortDashNorm = Mathf.Clamp01(summary.ShortTermDashesPerMin / 12f);
                reactivityRaw += (shortDashNorm - 0.3f) * 0.15f;
            }
            Reactivity = Mathf.Clamp01(reactivityRaw);
        }

        /// <summary>
        /// Get the legacy single aggression value from multi-axis profile.
        /// Used for backward compatibility with systems that still expect a single float.
        /// Blends all three axes with configurable weights.
        /// </summary>
        public float GetLegacyAggression()
        {
            if (!IsValid) return 0.5f;
            float aggressionWeight = AdaptiveAISettings.MultiAxisAggressionWeight;
            float precisionWeight = AdaptiveAISettings.MultiAxisPrecisionWeight;
            float reactivityWeight = AdaptiveAISettings.MultiAxisReactivityWeight;
            float total = aggressionWeight + precisionWeight + reactivityWeight;
            if (total < 0.01f) return 0.5f;
            return (Aggression * aggressionWeight + Precision * precisionWeight + Reactivity * reactivityWeight) / total;
        }

        // ---- Per-axis parameter application helpers ----

        /// <summary>Reaction time multiplier from Reactivity axis. High reactivity = faster reaction.</summary>
        public float GetReactionTimeMult()
        {
            if (!IsValid) return 1f;
            // Reactivity 0→1.3x (slow), 0.5→1x, 1→0.7x (fast)
            return Mathf.Lerp(1.3f, 0.7f, Reactivity);
        }

        /// <summary>Scatter (accuracy) multiplier from Precision axis. High precision player = AI needs more accuracy to match.</summary>
        public float GetScatterMult()
        {
            if (!IsValid) return 1f;
            // Precision 0→1.2x (less accurate AI), 0.5→1x, 1→0.8x (more accurate AI)
            return Mathf.Lerp(1.2f, 0.8f, Precision);
        }

        /// <summary>Dodge chance multiplier from Reactivity axis. High reactivity player = AI dodges more.</summary>
        public float GetDodgeChanceMult()
        {
            if (!IsValid) return 1f;
            // Reactivity 0→0.85x, 0.5→1x, 1→1.2x
            return Mathf.Lerp(0.85f, 1.2f, Reactivity);
        }

        /// <summary>Movement range multiplier from Aggression axis. High aggression player = AI adjusts engagement range.</summary>
        public float GetMovementRangeMult()
        {
            if (!IsValid) return 1f;
            // Aggression 0→0.85x (less range), 0.5→1x, 1→1.15x (more range for aggressive player)
            return Mathf.Lerp(0.85f, 1.15f, Aggression);
        }

        /// <summary>Forget time multiplier from Precision+Aggression. Precise aggressive player = AI remembers longer.</summary>
        public float GetForgetTimeMult()
        {
            if (!IsValid) return 1f;
            float combined = (Precision + Aggression) * 0.5f;
            // combined 0→1.2x (forget faster), 0.5→1x, 1→0.8x (remember longer)
            return Mathf.Lerp(1.2f, 0.8f, combined);
        }

        public void Reset()
        {
            Aggression = 0.5f;
            Precision = 0.5f;
            Reactivity = 0.5f;
            IsValid = false;
        }
    }
}
