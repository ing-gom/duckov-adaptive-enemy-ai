using HarmonyLib;
using UnityEngine;
using AdaptiveEnemyAI.Services;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 플레이어 입력(이동·조준)·대시 시작을 수집해 BehaviorCollector에 전달합니다.
    /// PatchAll(Assembly) 시 함께 적용됩니다.
    /// </summary>
    public static class PlayerInputPatches
    {
        [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetMoveInput))]
        public static class SetMoveInputPatch
        {
            public static void Postfix(CharacterMainControl __instance, Vector3 moveInput)
            {
                if (__instance != CharacterMainControl.Main) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                PlayerBehaviorCollector.SetLastMoveInput(moveInput);
            }
        }

        [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.SetAimPoint))]
        public static class SetAimPointPatch
        {
            public static void Postfix(CharacterMainControl __instance, Vector3 _aimPoint)
            {
                if (__instance != CharacterMainControl.Main) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                PlayerBehaviorCollector.SetAimPointAndDelta(_aimPoint, out float delta);
                PlayerBehaviorCollector.RecordAimDelta(delta);
            }
        }

        [HarmonyPatch(typeof(CharacterMainControl), "StartAction")]
        public static class StartActionPatch
        {
            public static void Postfix(CharacterMainControl __instance, CharacterActionBase newAction)
            {
                if (__instance != CharacterMainControl.Main || newAction == null) return;
                if (PlayerBehaviorCollector.IsInBase()) return;
                if (newAction.GetType().Name == "CA_Dash")
                    PlayerBehaviorCollector.RecordDash();
            }
        }
    }
}
