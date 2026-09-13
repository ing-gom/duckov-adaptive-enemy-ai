using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// Blocks dash when enemy is reloading or has no ammo but aggression is high ("charge" dash).
    /// Allows projectile dodge dash.
    /// Game original Dash() does not call StartAction(dashAction) when "attacking and damage not yet dealt",
    /// so dodge dash would not run; for dodge we ignore attack condition and call StartAction directly.
    /// </summary>
    public static class CharacterMainControlDashPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.CharacterMainControl.Dash";

        /// <summary>True if this Dash() call is for mod's projectile dodge. Cleared in Postfix.</summary>
        internal static bool DashIsIncomingDodge;

        /// <summary>Direction for CA_Dash.OnStart when dodge dashing. BT may overwrite MoveInput same frame causing in-place roll; OnStart Postfix forces this value.</summary>
        internal static Vector3 IncomingDodgeDirection;

        /// <summary>Preset names that cannot dash (e.g. turret). This preset blocks both approach and dodge dash.</summary>
        internal const string PresetNameNoDash = "EnemyPreset_GunTurret";

        /// <summary>Presets that skip adaptive AI entirely (quest, merchant, pet, turret, etc.). 터렛은 게임 원래 인지/사격 로직 사용(모드 패치 미적용). 대시만 IsDashBlockedForPreset으로 차단.</summary>
        private static readonly HashSet<string> PresetNamesAdaptiveAIExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EnemyPreset_QuestGiver_Alex",
            "EnemyPreset_QuestGiver_Fo",
            "EnemyPreset_QuestGiver_XiaoMing",
            "EnemyPreset_Merchant_Jeff",
            "EnemyPreset_Merchant_Myst",
            "EnemyPreset_Merchant_Myst0",
            "EnemyPreset_Merchant_Test",
            "PetPreset_NormalPet",
            "EnemyPreset_GunTurret",
        };

        /// <summary>Prefix for merchant/quest etc. NPC preset names. If name starts with this, excluded even if assigned different name at runtime.</summary>
        private const string PresetNamePrefixMerchant = "EnemyPreset_Merchant_";
        private const string PresetNamePrefixQuestGiver = "EnemyPreset_QuestGiver_";
        /// <summary>Pet (companion) preset name prefix. If name starts with this, adaptive AI not applied.</summary>
        private const string PresetNamePrefixPet = "PetPreset_";
        /// <summary>True if this character has adaptive-AI-excluded preset (quest giver, secret merchant, pet, etc.). Then no mod logic: loadout correction, dodge, movement pattern, etc.</summary>
        internal static bool IsAdaptiveAIExcludedForPreset(CharacterMainControl? cmc)
        {
            if (cmc?.characterPreset == null) return false;
            string name = cmc.characterPreset.name;
            if (PresetNamesAdaptiveAIExcluded.Contains(name)) return true;
            // Handle runtime Clone/variant names (e.g. Myst 0, Myst1) — exclude if merchant/quest giver/pet prefix
            if (name.StartsWith(PresetNamePrefixMerchant, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetNamePrefixQuestGiver, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetNamePrefixPet, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static Harmony? _harmony;
        private static bool _applied;

        private static Type? _itemSettingGunType;
        private static PropertyInfo? _bulletCountProp;
        private static PropertyInfo? _loadingBulletsProp;

        private static FieldInfo? _disableTriggerTimerField;

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(
                    AccessTools.Method(typeof(CharacterMainControl), "Dash"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlDashPatches), nameof(Dash_Prefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterMainControlDashPatches), nameof(Dash_Postfix))));
                _applied = true;
            }
            catch (Exception)
            {
                // Ignore on mod load failure
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

        private static void Dash_Postfix()
        {
            DashIsIncomingDodge = false;
            IncomingDodgeDirection = default;
        }

        /// <summary>True if this character has no-dash preset (e.g. turret).</summary>
        internal static bool IsDashBlockedForPreset(CharacterMainControl cmc)
        {
            if (cmc?.characterPreset == null) return false;
            bool blocked = string.Equals(cmc.characterPreset.name, PresetNameNoDash, StringComparison.OrdinalIgnoreCase);
            if (AdaptiveAISettings.DebugLogDash && blocked)
                Debug.Log($"[AdaptiveEnemyAI] Dash blocked(preset): preset=\"{cmc.characterPreset.name}\" (turret-only block target={PresetNameNoDash})");
            return blocked;
        }

        /// <summary>Only turret preset has dash blocked. Reload/no-ammo block removed — enemy with canDash can do approach and dodge dash. Player (main character) always allowed.
        /// Dodge dash: game original Dash() does not call StartAction(dashAction) when attackAction.Running && !DamageDealed, so dash never starts. We call StartAction(dashAction) directly and skip original so dodge dash runs even while attacking.</summary>
        private static bool Dash_Prefix(CharacterMainControl __instance)
        {
            if (__instance == null) return true;
            if (PlayerBehaviorCollector.IsInBase()) return true;

            // Player (main character) always allow dash without mod logic — avoid confusion when debugging other mod/game causes
            if (__instance == CharacterMainControl.Main)
            {
                if (AdaptiveAISettings.DebugLogDash)
                    Debug.Log("[AdaptiveEnemyAI] Dash allowed(player): CharacterMainControl.Main");
                return true;
            }

            if (IsDashBlockedForPreset(__instance))
            {
                if (AdaptiveAISettings.DebugLogDash)
                    Debug.Log($"[AdaptiveEnemyAI] Dash() blocked(preset): preset=\"{__instance.characterPreset?.name ?? "null"}\"");
                return false;
            }

            // Dodge dash: game Dash() does not call StartAction when "attacking + damage not yet dealt". We call it and skip original.
            if (DashIsIncomingDodge && __instance.dashAction != null)
            {
                bool started = __instance.StartAction(__instance.dashAction);
                if (AdaptiveAISettings.DebugLogDash)
                    Debug.Log($"[AdaptiveEnemyAI] Dodge dash StartAction result: started={started} AI={__instance.GetInstanceID()}");
                if (started && !__instance.DashCanControl)
                {
                    if (_disableTriggerTimerField == null)
                        _disableTriggerTimerField = AccessTools.Field(typeof(CharacterMainControl), "disableTriggerTimer");
                    if (_disableTriggerTimerField != null)
                    {
                        try
                        {
                            float current = (float)(_disableTriggerTimerField.GetValue(__instance) ?? 0f);
                            if (current < 0.6f)
                                _disableTriggerTimerField.SetValue(__instance, 0.6f);
                        }
                        catch { }
                    }
                }
                return false; // Do not run original Dash() (dash already started)
            }

            var ai = __instance.GetComponent<global::AICharacterController>();
            if (ai == null) return true;

            if (__instance.Team == Teams.player) return true;

            // Allow approach dash too: do not block when reloading/no ammo + aggressive (if canDash true, BT/mod already allows approach dash).
            return true;
        }

        /// <summary>Whether this character is currently reloading. (For dodge-priority behavior.)</summary>
        internal static bool IsCharacterReloading(CharacterMainControl cmc)
        {
            if (cmc?.reloadAction == null) return false;
            return cmc.CurrentAction == cmc.reloadAction && cmc.CurrentAction.Running;
        }

        private static bool IsReloadingOrNoAmmo(CharacterMainControl cmc)
        {
            if (cmc == null) return false;

            if (IsCharacterReloading(cmc))
                return true;

            var agent = cmc.CurrentHoldItemAgent;
            if (agent == null) return false;

            var comp = agent as Component;
            if (comp == null) return false;

            var gun = GetGunSetting(comp.gameObject);
            if (gun == null) return false;

            int bulletCount = GetBulletCount(gun);
            bool loading = GetLoadingBullets(gun);
            return loading || bulletCount <= 0;
        }

        private static object? GetGunSetting(GameObject go)
        {
            if (go == null) return null;
            if (_itemSettingGunType == null)
            {
                _itemSettingGunType = Type.GetType("ItemSetting_Gun, TeamSoda.Duckov.Core");
                if (_itemSettingGunType == null) _itemSettingGunType = Type.GetType("ItemSetting_Gun");
            }
            if (_itemSettingGunType == null) return null;
            var c = go.GetComponentInChildren(_itemSettingGunType);
            return c;
        }

        private static int GetBulletCount(object? gun)
        {
            if (gun == null) return -1;
            if (_bulletCountProp == null)
            {
                var t = gun.GetType();
                _bulletCountProp = t.GetProperty("BulletCount", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_bulletCountProp == null) return -1;
            try
            {
                var v = _bulletCountProp.GetValue(gun);
                return v is int i ? i : -1;
            }
            catch { return -1; }
        }

        private static bool GetLoadingBullets(object? gun)
        {
            if (gun == null) return false;
            if (_loadingBulletsProp == null)
            {
                var t = gun.GetType();
                _loadingBulletsProp = t.GetProperty("LoadingBullets", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_loadingBulletsProp == null) return false;
            try
            {
                var v = _loadingBulletsProp.GetValue(gun);
                return v is bool b && b;
            }
            catch { return false; }
        }
    }
}
