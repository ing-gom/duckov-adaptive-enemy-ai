using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Per-weapon-type "preferred engagement range". In range: aggression bonus; outside: move toward approach/slight retreat.
    /// EngagementCap (range cap) can limit long-range weapons so they don't only shoot from far (for balance).
    /// </summary>
    public static class WeaponPreferredRange
    {
        /// <summary>Preferred range (m), "far" threshold, engagement cap. FarThreshold 0 = default 12m. EngagementCap 0 = no cap.</summary>
        public readonly struct Range
        {
            public readonly float OptimalMin;
            public readonly float OptimalMax;
            /// <summary>farPenalty applied when distance (m) exceeds this. 0 = default 12m.</summary>
            public readonly float FarThreshold;
            /// <summary>When distance (m) exceeds this, overRangePenalty instead of inRangeBonus. 0 = no cap (full range bonus). Recommend 25–30m for long-range for balance.</summary>
            public readonly float EngagementCap;

            public Range(float optimalMin, float optimalMax, float farThreshold = 0f, float engagementCap = 0f)
            {
                OptimalMin = optimalMin;
                OptimalMax = optimalMax;
                FarThreshold = farThreshold;
                EngagementCap = engagementCap;
            }
        }

        /// <summary>Preferred range for all guns within 10m. Encourages AI to engage inside 10m.</summary>
        private static readonly Dictionary<string, Range> Table = new Dictionary<string, Range>
        {
            { "Melee", new Range(0f, 2.5f) },                       // melee 0~2.5m
            { "Tag_GunType_PST", new Range(2f, 10f, 0f, 10f) },     // pistol
            { "Tag_GunType_SMG", new Range(2f, 10f, 0f, 10f) },     // SMG
            { "Tag_GunType_SHT", new Range(2.5f, 6f, 0f, 10f) },    // shotgun
            { "Tag_GunType_AR", new Range(3f, 10f, 0f, 10f) },      // AR
            { "Tag_GunType_BR", new Range(4f, 10f, 0f, 10f) },      // BR
            { "Tag_GunType_SNP", new Range(5f, 10f, 0f, 10f) },     // sniper
            { "Tag_GunType_MAG", new Range(4f, 10f, 0f, 10f) },     // LMG
            { "Tag_GunType_PWS", new Range(3f, 10f, 0f, 10f) },     // PWS
            { "Tag_GunType_ARR", new Range(3f, 10f, 0f, 10f) },     // crossbow etc
            { "Tag_GunType_Rocket", new Range(4f, 10f, 0f, 10f) },  // rocket
        };

        /// <summary>Preferred range for this weapon tag. Returns false if not found.</summary>
        public static bool TryGet(string? gunTypeTag, out Range range)
        {
            range = default;
            if (string.IsNullOrEmpty(gunTypeTag)) return false;
            return Table.TryGetValue(gunTypeTag, out range);
        }

        /// <summary>In preferred range: positive (aggression bonus). Far: negative. Over EngagementCap: small penalty to discourage only long-range shooting.</summary>
        /// <param name="gunTypeTag">Enemy (AI) weapon tag.</param>
        /// <param name="distanceToPlayer">Enemy to player distance (m).</param>
        /// <param name="inRangeBonus">Aggression modifier when in range (and under cap), e.g. +0.1.</param>
        /// <param name="farPenalty">Aggression modifier when far, e.g. -0.05.</param>
        /// <param name="overRangePenalty">Small penalty when in range but over EngagementCap (encourage approach).</param>
        /// <param name="defaultFarThreshold">Default (m) when weapon FarThreshold is 0.</param>
        public static float GetDistanceAggressionModifier(string? gunTypeTag, float distanceToPlayer,
            float inRangeBonus = 0.1f, float farPenalty = -0.05f, float overRangePenalty = -0.02f, float defaultFarThreshold = 12f)
        {
            if (!TryGet(gunTypeTag, out Range r)) return 0f;

            if (distanceToPlayer >= r.OptimalMin && distanceToPlayer <= r.OptimalMax)
            {
                // Engagement cap: bonus only inside cap; over cap = small penalty (encourage approach)
                if (r.EngagementCap > 0f && distanceToPlayer > r.EngagementCap)
                    return overRangePenalty;
                return inRangeBonus;
            }
            float far = r.FarThreshold > 0f ? r.FarThreshold : defaultFarThreshold;
            if (distanceToPlayer > far)
                return farPenalty;
            return 0f;
        }
    }
}
