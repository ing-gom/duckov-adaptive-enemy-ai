using System;
using HarmonyLib;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 근접무기를 들고 있는 모든 적 AI에게 이동속도 10% 버프를 적용합니다 (걷기/달리기 공통).
    /// </summary>
    public static class CharacterMainControlMeleeSpeedPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.CharacterMainControl.MeleeSpeedBuff";
        private static Harmony? _harmony;
        private static bool _applied;

        /// <summary>근접 시 이동속도 배율. 1.1 = 10% 버프.</summary>
        public const float MeleeHoldSpeedMultiplier = 1.1f;

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                var walkGetter = AccessTools.PropertyGetter(typeof(CharacterMainControl), "CharacterWalkSpeed");
                var runGetter = AccessTools.PropertyGetter(typeof(CharacterMainControl), "CharacterRunSpeed");
                if (walkGetter != null)
                    _harmony.Patch(walkGetter, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlMeleeSpeedPatches), nameof(MeleeSpeed_Postfix))));
                if (runGetter != null)
                    _harmony.Patch(runGetter, postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlMeleeSpeedPatches), nameof(MeleeSpeed_Postfix))));
                _applied = true;
            }
            catch (Exception) { }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try { _harmony.UnpatchAll(HarmonyId); _applied = false; } catch { }
        }

        private static void MeleeSpeed_Postfix(CharacterMainControl __instance, ref float __result)
        {
            if (__instance == null || __instance == CharacterMainControl.Main) return;
            if (!AdaptiveAISettings.MeleeHoldSpeedBuffEnabled) return;
            if (__instance.GetMeleeWeapon() == null) return;

            __result *= MeleeHoldSpeedMultiplier;
        }
    }
}
