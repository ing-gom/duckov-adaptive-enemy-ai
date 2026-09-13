using System.Runtime.CompilerServices;
using UnityEngine;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// Per-AI registry controlling whether "adaptive patch (mod logic)" is applied.
    /// Harmony patches by type only, so patches are applied once globally and each Prefix/Postfix
    /// queries this class to decide "skip patch logic for this AI (run original)".
    /// </summary>
    public static class PerAIPatchControl
    {
        /// <summary>Marker attached to excluded AI. If value exists in ConditionalWeakTable, "this AI is excluded from adaptive patch".</summary>
        private sealed class ExcludedMarker { }

        private static readonly ConditionalWeakTable<global::AICharacterController, ExcludedMarker> _excludedAi = new ConditionalWeakTable<global::AICharacterController, ExcludedMarker>();

        /// <summary>
        /// Excludes this AI from adaptive patches. For this AI, each patch runs the original method as-is.
        /// (Separate from preset exclusion IsAdaptiveAIExcludedForPreset; use when excluding specific instances at runtime.)
        /// </summary>
        public static void ExcludeFromAdaptivePatches(global::AICharacterController ai)
        {
            if (ai == null) return;
            try { _excludedAi.GetOrCreateValue(ai); } catch { }
        }

        /// <summary>
        /// Re-includes previously excluded AI in adaptive patch targets.
        /// </summary>
        public static void IncludeInAdaptivePatches(global::AICharacterController ai)
        {
            if (ai == null) return;
            try { _excludedAi.Remove(ai); } catch { }
        }

        /// <summary>
        /// Whether this AI is excluded from adaptive patches.
        /// If true, skip patch logic and run original method only.
        /// Exclusion: 0) global SkipAttackLogicPatchesGlobally, 1) manual (_excludedAi), 2) melee (has melee weapon) AI.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsExcludedFromAdaptivePatches(global::AICharacterController? ai)
        {
            if (AdaptiveAISettings.SkipAttackLogicPatchesGlobally) return true;
            if (ai == null) return false;
            try
            {
                if (_excludedAi.TryGetValue(ai, out _)) return true;
                if (IsMeleeAttacker(ai)) return true;
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Checks whether the AI linked to CharacterMainControl is in the exclusion list.
        /// Use when patch only has __instance(CharacterMainControl) for single lookup.
        /// Exclusion: 0) global SkipAttackLogicPatchesGlobally, 1) manual, 2) melee (has melee weapon) AI.
        /// </summary>
        public static bool IsExcludedFromAdaptivePatches(CharacterMainControl? c)
        {
            if (AdaptiveAISettings.SkipAttackLogicPatchesGlobally) return true;
            if (c == null) return false;
            if (c.GetMeleeWeapon() != null) return true;
            var ai = AICharacterControllerPatches.GetAIForCharacter(c);
            return IsExcludedFromAdaptivePatches(ai);
        }

        /// <summary>
        /// Whether this AI is melee (has melee weapon). Melee AI is treated as true for adaptive patch exclusion.
        /// </summary>
        public static bool IsMeleeAttacker(global::AICharacterController? ai)
        {
            if (ai?.CharacterMainControl == null) return false;
            return ai.CharacterMainControl.GetMeleeWeapon() != null;
        }
    }
}
