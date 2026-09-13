using HarmonyLib;
using AdaptiveEnemyAI.Settings;
using AdaptiveEnemyAI.Services;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 엄폐물이 있을 때 공격 시작(StartAction)만 막으면, 이미 연사 중일 때는 막히지 않음.
    /// 실제 발사(TransToFire) 직전에 게임이 설정한 hasObsticleToTarget만 보고, true면 해당 발사만 취소함.
    /// (모드의 IsCoverBetweenForBlockFire는 레이/레이어에 따라 오탐이 많아 여기서는 사용하지 않음.)
    /// </summary>
    [HarmonyPatch(typeof(ItemAgent_Gun), "TransToFire")]
    public static class ItemAgent_Gun_TransToFire_BlockWhenInCoverPrefix
    {
        [HarmonyPrefix]
        public static bool Prefix(ItemAgent_Gun __instance, bool isFirstShot)
        {
            if (!AdaptiveAISettings.BlockFireWhenInCoverEnabled || __instance == null) return true;
            var holder = __instance.Holder;
            if (holder == null || holder == CharacterMainControl.Main) return true;
            if (PlayerBehaviorCollector.IsInBase()) return true;
            var ai = AICharacterControllerPatches.GetAIForCharacter(holder);
            if (ai == null) return true;
            if (ai.hasObsticleToTarget)
                return false;
            return true;
        }
    }
}
