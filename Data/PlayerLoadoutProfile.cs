using UnityEngine;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Summary of player current loadout and armor. Used for enemy AI adjustment by loadout.
    /// </summary>
    public sealed class PlayerLoadoutProfile
    {
        /// <summary>Weapon range. 0 if melee or none.</summary>
        public float WeaponRange { get; set; }

        /// <summary>Whether currently holding melee weapon.</summary>
        public bool IsMelee { get; set; }

        /// <summary>Body armor value.</summary>
        public float BodyArmor { get; set; }

        /// <summary>Head armor value.</summary>
        public float HeadArmor { get; set; }

        /// <summary>GunType tag of player's gun (e.g. Tag_GunType_AR). null = common.</summary>
        public string? GunTypeTag { get; set; }

        /// <summary>Long-range threshold (>= = evasive).</summary>
        public const float LongRangeThreshold = 30f;

        /// <summary>Short-range threshold (<= = aggressive).</summary>
        public const float ShortRangeThreshold = 12f;

        /// <summary>Total armor (tankiness).</summary>
        public float TotalArmor => BodyArmor + HeadArmor;

        /// <summary>Whether long-range oriented (evasive).</summary>
        public bool IsLongRange => !IsMelee && WeaponRange >= LongRangeThreshold;

        /// <summary>Whether short/melee oriented (rush).</summary>
        public bool IsShortOrMelee => IsMelee || WeaponRange <= ShortRangeThreshold;

        /// <summary>Expected max total armor (for normalization).</summary>
        public const float ArmorScale = 80f;

        /// <summary>
        /// Aggression factor from loadout and armor. Positive = aggressive, negative = evasive. Clamped by BaseAggressionRange.
        /// Linear interpolation 12m~30m. Absolute; used when no enemy context (e.g. debug).
        /// </summary>
        public float GetAggressionFactor()
        {
            // Range lerp: 12m → +0.35 (aggressive), 30m → -0.35 (evasive), linear in between
            float rangeContrib;
            if (IsMelee || WeaponRange <= ShortRangeThreshold)
            {
                rangeContrib = 0.35f;
            }
            else if (WeaponRange >= LongRangeThreshold)
            {
                rangeContrib = -0.35f;
            }
            else
            {
                float rangeT = Mathf.Clamp01((WeaponRange - ShortRangeThreshold) / (LongRangeThreshold - ShortRangeThreshold));
                rangeContrib = Mathf.Lerp(0.35f, -0.35f, rangeT);
            }

            float armorNorm = Mathf.Clamp01(TotalArmor / ArmorScale);
            float armorContrib = (armorNorm - 0.5f) * 0.4f;

            float raw = rangeContrib + armorContrib;
            float sens = Mathf.Max(0.1f, AdaptiveAISettings.LoadoutAggressionSensitivity);
            float range = AdaptiveAISettings.BaseAggressionRange;
            return Mathf.Clamp(raw * sens, -range, range);
        }

        /// <summary>
        /// Aggression factor from player vs this enemy (AI). Positive = AI advantage, negative = AI disadvantage.
        /// - Range: player longer → AI disadvantage (negative) / AI longer → AI advantage (positive).
        /// - Matchup: WeaponMatchupTable.
        /// - Armor: player higher → AI disadvantage / AI higher → AI advantage.
        /// </summary>
        /// <param name="enemyWeaponRange">Enemy (AI) weapon range. 0 if melee/none.</param>
        /// <param name="enemyIsMelee">Enemy (AI) has melee.</param>
        /// <param name="enemyTotalArmor">Enemy (AI) total armor (body+head).</param>
        /// <param name="enemyGunTypeTag">Enemy (AI) GunType tag. null = no matchup.</param>
        public float GetAggressionFactorRelativeTo(float enemyWeaponRange, bool enemyIsMelee, float enemyTotalArmor, string? enemyGunTypeTag = null)
        {
            float f = 0f;

            // Range comparison: player > AI → AI disadvantage / player < AI → AI advantage
            bool playerMelee = IsMelee;
            bool playerHasGun = !IsMelee && WeaponRange > 0f;
            bool enemyHasGun = !enemyIsMelee && enemyWeaponRange > 0f;

            if (playerMelee && enemyHasGun)
                f += 0.35f;   // Player melee, AI ranged → AI range advantage
            else if (playerHasGun && enemyIsMelee)
                f -= 0.35f;
            else if (playerHasGun && enemyHasGun)
            {
                const float rangeTolerance = 3f; // similar range
                float rangeDiff = WeaponRange - enemyWeaponRange;
                if (rangeDiff > rangeTolerance)
                    f -= 0.35f; // Player longer → AI disadvantage
                else if (rangeDiff < -rangeTolerance)
                    f += 0.35f; // AI longer → AI advantage
            }

            // Weapon matchup: player vs enemy combination
            float matchupContrib = WeaponMatchupTable.GetMatchupFactor(GunTypeTag, enemyGunTypeTag);
            f += matchupContrib;

            // Armor comparison: player > AI → AI disadvantage / player < AI → AI advantage
            float armorDiff = enemyTotalArmor - TotalArmor; // positive = AI tankier → AI advantage
            float armorContrib = Mathf.Clamp(armorDiff / ArmorScale * 0.4f, -0.4f, 0.4f);
            f += armorContrib;

            float sens = Mathf.Max(0.1f, AdaptiveAISettings.LoadoutAggressionSensitivity);
            float range = AdaptiveAISettings.BaseAggressionRange;
            return Mathf.Clamp(f * sens, -range, range);
        }
    }
}
