using System;
using System.Collections.Generic;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>Entry for per-map combat count save. For ES3 serialization.</summary>
    [Serializable]
    public struct MapCombatEntry
    {
        public string MapId;
        public int CombatCount;
    }

    /// <summary>
    /// Behavior profile DTO saved to save file. [Serializable] + public fields for ES3.
    /// V2: combat pattern fields (distance, count, crit; no damage amount). Old loads get default combat fields.
    /// V3: combat accumulations; after load restore then keep accumulating.
    /// V4: movement pattern (approach/retreat/lateral, zigzag) fields.
    /// V5: SavedAtUtcTicks — time decay on load to reduce weight of old patterns.
    /// V6: player disadvantage runtime-only (HP ratio, enemy count not saved).
    /// V7: MapCombatCounts — combat count per map for map familiarity.
    /// V8: ReloadRiskRatioEma, ThreatResponseRetreatTendencyEma (v1.1.8 player behavior dynamic AI).
    /// </summary>
    [Serializable]
    public sealed class BehaviorProfileSaveData
    {
        public int Version = 8;
        public float MoveStrengthEma;
        public float RunRatioEma;
        public float AimChangeVariance;
        public float DashCountPerMin;
        public float ShootCountPerMin;

        // Combat pattern (no damage amount; distance, count, crit) — per-minute summary
        /// <summary>PvE: average distance when I hit enemy (EMA or accumulated avg).</summary>
        public float PlayerToEnemyAvgDistanceEma;
        /// <summary>PvE: hits dealt per minute.</summary>
        public float PlayerToEnemyHitsPerMin;
        /// <summary>PvE: crits per minute.</summary>
        public float PlayerToEnemyCritsPerMin;
        /// <summary>EvP: player hits taken per minute.</summary>
        public float EnemyToPlayerHitsPerMin;
        /// <summary>EvP: average distance when enemy hits me (aux).</summary>
        public float EnemyToPlayerAvgDistanceEma;

        // Version 3: combat accumulations (keep accumulating on top after load)
        /// <summary>Total time (s) accumulated in combat only.</summary>
        public float TotalCombatTimeSeconds;
        /// <summary>PvE: accumulated hit count.</summary>
        public int TotalPlayerToEnemyHits;
        /// <summary>PvE: accumulated crit count.</summary>
        public int TotalPlayerToEnemyCrits;
        /// <summary>PvE: distance sum (avg = Sum / Hits).</summary>
        public float SumPlayerToEnemyDistance;
        /// <summary>EvP: accumulated hit count.</summary>
        public int TotalEnemyToPlayerHits;
        /// <summary>EvP: distance sum.</summary>
        public float SumEnemyToPlayerDistance;
        /// <summary>Accumulated dashes in combat. Per min = this / (TotalCombatTimeSeconds/60).</summary>
        public int TotalDashesInCombat;
        /// <summary>Accumulated shoots in combat.</summary>
        public int TotalShootsInCombat;

        // Version 4: movement pattern (player move direction → enemy strafe/zigzag)
        /// <summary>Move·enemy direction dot EMA (-1~1). Approach(+), retreat(-), lateral(0).</summary>
        public float MoveDirDotToEnemyEma;
        /// <summary>Lateral component ratio EMA (0~1). Strafe strength.</summary>
        public float LateralMoveRatioEma;
        /// <summary>Move·enemy dot variance. Zigzag strength.</summary>
        public float MoveDirDotVariance;

        // Version 5: reduce weight of old patterns on load
        /// <summary>Profile save time (UTC). Decay by days on load. 0 = legacy save.</summary>
        public long SavedAtUtcTicks;

        // Version 7: per-map combat count (map familiarity = CombatCount / MapFamiliarityCombatCap)
        /// <summary>Map ID → combat count on that map. Serialization list. null = legacy or empty.</summary>
        public List<MapCombatEntry>? MapCombatCounts;

        // Version 8: v1.1.8 reload timing, threat response
        /// <summary>Risky reload ratio EMA (0~1).</summary>
        public float ReloadRiskRatioEma;
        /// <summary>Threat response retreat tendency EMA (0~1).</summary>
        public float ThreatResponseRetreatTendencyEma;

        // Version 9: Feature 4 player habit data (persist across sessions)
        /// <summary>Dash direction histogram (8 compass buckets). null = no data.</summary>
        public float[]? HabitDashHistogram;
        /// <summary>Total dash direction samples collected.</summary>
        public int HabitTotalDashSamples;
        /// <summary>Post-shoot behavior histogram (4 buckets: retreat/strafe/advance/stay). null = no data.</summary>
        public float[]? HabitPostShootBehavior;
        /// <summary>Total post-shoot behavior samples collected.</summary>
        public int HabitPostShootSamples;
        /// <summary>Sum of HP ratios at flee observations (for averaging).</summary>
        public float HabitFleeHpSum;
        /// <summary>Number of flee observations.</summary>
        public int HabitFleeObservations;
    }
}
