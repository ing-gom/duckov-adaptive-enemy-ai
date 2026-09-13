using System;
using HarmonyLib;
using AdaptiveEnemyAI.Services;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// 적 AI가 재장전 중 회피(대시)를 하면 CA_Reload.OnStop → CancleReload()가 호출되지만,
    /// 이미 시작된 LoadBulletsFromInventory 비동기 로드는 계속 진행되고 loadingBullets가 true로 남는다.
    /// 이 동안 ItemSetting_Gun.BulletCount는 loadingBullets일 때 -1을 반환해, 탄이 증발한 것처럼 보이는 버그가 있다.
    /// 재장전이 취소된 경우(총 GunState != reloading)에는 실제 탄 수를 반환하도록 보정한다.
    /// </summary>
    public static class ItemSettingGunPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.ItemSetting_Gun.BulletCount";

        private static Harmony? _harmony;
        private static bool _applied;

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                var getter = AccessTools.PropertyGetter(typeof(ItemSetting_Gun), "BulletCount");
                if (getter != null)
                {
                    _harmony.Patch(
                        getter,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(ItemSettingGunPatches), nameof(BulletCount_Postfix))));
                }
                _applied = true;
            }
            catch (Exception)
            {
                // 모드 로드 실패 시 무시
            }
        }

        public static void RemovePatches()
        {
            if (!_applied || _harmony == null) return;
            try
            {
                _harmony.UnpatchAll(HarmonyId);
                _applied = false;
            }
            catch (Exception) { }
        }

        private static void BulletCount_Postfix(ItemSetting_Gun __instance, ref int __result)
        {
            if (PlayerBehaviorCollector.IsInBase()) return;
            // 원본이 -1을 반환한 경우 = loadingBullets가 true인 경우. 재장전이 취소되었는지 확인.
            if (__result != -1) return;
            if (__instance?.Item?.ActiveAgent is ItemAgent_Gun gunAgent &&
                gunAgent.GunState != ItemAgent_Gun.GunStates.reloading)
            {
                __result = __instance.GetBulletCount();
            }
        }
    }
}
