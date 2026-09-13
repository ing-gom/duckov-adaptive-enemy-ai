using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveEnemyAI.Data
{
    /// <summary>
    /// Player vs enemy (AI) weapon matchup correction. Positive = AI advantage, negative = AI disadvantage.
    /// Summed with range and armor in GetAggressionFactorRelativeTo.
    /// </summary>
    public static class WeaponMatchupTable
    {
        /// <summary>Matchup contribution clamp min/max.</summary>
        public const float MatchupClampMin = -0.25f;
        public const float MatchupClampMax = 0.25f;

        /// <summary>Key: (player GunTypeTag, enemy GunTypeTag). Value: AI-side correction.</summary>
        private static readonly Dictionary<(string, string), float> Table = BuildTable();

        private static Dictionary<(string, string), float> BuildTable()
        {
            const string M = "Melee";
            const string PST = "Tag_GunType_PST";
            const string SMG = "Tag_GunType_SMG";
            const string AR = "Tag_GunType_AR";
            const string BR = "Tag_GunType_BR";
            const string SNP = "Tag_GunType_SNP";
            const string SHT = "Tag_GunType_SHT";
            const string MAG = "Tag_GunType_MAG";
            const string PWS = "Tag_GunType_PWS";
            const string ARR = "Tag_GunType_ARR";
            const string Rocket = "Tag_GunType_Rocket";

            var t = new Dictionary<(string, string), float>();

            // Player melee / enemy gun → AI advantage
            t[(M, PST)] = 0.25f;
            t[(M, SMG)] = 0.25f;
            t[(M, AR)] = 0.25f;
            t[(M, BR)] = 0.25f;
            t[(M, SNP)] = 0.25f;
            t[(M, SHT)] = 0.25f;
            t[(M, MAG)] = 0.25f;
            t[(M, PWS)] = 0.25f;
            t[(M, ARR)] = 0.25f;
            t[(M, Rocket)] = 0.25f;

            // Player gun / enemy melee → AI disadvantage
            t[(PST, M)] = -0.25f;
            t[(SMG, M)] = -0.25f;
            t[(AR, M)] = -0.25f;
            t[(BR, M)] = -0.25f;
            t[(SNP, M)] = -0.25f;
            t[(SHT, M)] = -0.25f;
            t[(MAG, M)] = -0.25f;
            t[(PWS, M)] = -0.25f;
            t[(ARR, M)] = -0.25f;
            t[(Rocket, M)] = -0.25f;

            // PST vs enemy
            t[(PST, SMG)] = -0.12f;
            t[(PST, AR)] = -0.18f;
            t[(PST, BR)] = -0.18f;
            t[(PST, SNP)] = -0.20f;
            t[(PST, SHT)] = -0.10f;
            t[(PST, MAG)] = -0.15f;
            t[(PST, PWS)] = -0.15f;
            t[(PST, ARR)] = -0.15f;
            t[(PST, Rocket)] = -0.22f;

            // SMG vs enemy
            t[(SMG, PST)] = 0.12f;
            t[(SMG, AR)] = -0.12f;
            t[(SMG, BR)] = -0.15f;
            t[(SMG, SNP)] = -0.18f;
            t[(SMG, MAG)] = -0.08f;
            t[(SMG, PWS)] = -0.08f;
            t[(SMG, ARR)] = -0.10f;
            t[(SMG, Rocket)] = -0.18f;

            // AR vs enemy
            t[(AR, PST)] = 0.18f;
            t[(AR, SMG)] = 0.12f;
            t[(AR, SNP)] = -0.12f;
            t[(AR, SHT)] = 0.08f;
            t[(AR, ARR)] = -0.05f;
            t[(AR, Rocket)] = -0.15f;

            // BR vs enemy
            t[(BR, PST)] = 0.18f;
            t[(BR, SMG)] = 0.15f;
            t[(BR, SNP)] = -0.10f;
            t[(BR, SHT)] = 0.05f;
            t[(BR, ARR)] = -0.05f;
            t[(BR, Rocket)] = -0.15f;

            // SNP vs enemy
            t[(SNP, PST)] = 0.20f;
            t[(SNP, SMG)] = 0.18f;
            t[(SNP, AR)] = 0.12f;
            t[(SNP, BR)] = 0.10f;
            t[(SNP, SHT)] = 0.15f;
            t[(SNP, MAG)] = 0.08f;
            t[(SNP, PWS)] = 0.08f;
            t[(SNP, ARR)] = 0.05f;
            t[(SNP, Rocket)] = -0.18f;

            // SHT vs enemy
            t[(SHT, PST)] = 0.10f;
            t[(SHT, AR)] = -0.08f;
            t[(SHT, BR)] = -0.05f;
            t[(SHT, SNP)] = -0.15f;
            t[(SHT, MAG)] = -0.05f;
            t[(SHT, PWS)] = -0.05f;
            t[(SHT, ARR)] = -0.08f;
            t[(SHT, Rocket)] = -0.15f;

            // MAG vs enemy
            t[(MAG, PST)] = 0.15f;
            t[(MAG, SMG)] = 0.08f;
            t[(MAG, SNP)] = -0.08f;
            t[(MAG, SHT)] = 0.05f;
            t[(MAG, ARR)] = -0.05f;
            t[(MAG, Rocket)] = -0.15f;

            // PWS vs enemy
            t[(PWS, PST)] = 0.15f;
            t[(PWS, SMG)] = 0.08f;
            t[(PWS, SNP)] = -0.08f;
            t[(PWS, SHT)] = 0.05f;
            t[(PWS, ARR)] = -0.05f;
            t[(PWS, Rocket)] = -0.15f;

            // ARR vs enemy
            t[(ARR, PST)] = 0.15f;
            t[(ARR, SMG)] = 0.10f;
            t[(ARR, AR)] = 0.05f;
            t[(ARR, BR)] = 0.05f;
            t[(ARR, SNP)] = -0.05f;
            t[(ARR, SHT)] = 0.08f;
            t[(ARR, MAG)] = 0.05f;
            t[(ARR, PWS)] = 0.05f;
            t[(ARR, Rocket)] = -0.12f;

            // Rocket vs enemy
            t[(Rocket, PST)] = 0.22f;
            t[(Rocket, SMG)] = 0.18f;
            t[(Rocket, AR)] = 0.15f;
            t[(Rocket, BR)] = 0.15f;
            t[(Rocket, SNP)] = 0.18f;
            t[(Rocket, SHT)] = 0.15f;
            t[(Rocket, MAG)] = 0.15f;
            t[(Rocket, PWS)] = 0.15f;
            t[(Rocket, ARR)] = 0.12f;

            return t;
        }

        /// <summary>
        /// AI-side matchup correction [-0.25, 0.25] for player vs enemy weapon tags.
        /// Returns 0 for null/empty.
        /// </summary>
        public static float GetMatchupFactor(string? playerTag, string? enemyTag)
        {
            if (string.IsNullOrEmpty(playerTag) || string.IsNullOrEmpty(enemyTag))
                return 0f;
            return Table.TryGetValue((playerTag, enemyTag), out float v)
                ? Mathf.Clamp(v, MatchupClampMin, MatchupClampMax)
                : 0f;
        }
    }
}
