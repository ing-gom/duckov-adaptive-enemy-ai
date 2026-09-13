using System;
using System.Collections.Generic;
using HarmonyLib;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Patches
{
    /// <summary>
    /// On CharacterSpawnerRoot.AddCreatedCharacter, registers spawned enemy AI in combat context target list.
    /// Enables combat check from "actually spawned mobs" only, without FindObjectsOfType.
    /// </summary>
    public static class CharacterSpawnerPatches
    {
        private const string HarmonyId = "AdaptiveEnemyAI.CharacterSpawner";

        private static Harmony? _harmony;
        private static bool _applied;

        public static void ApplyPatches()
        {
            if (_applied) return;
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.PatchAll(typeof(CharacterSpawnerPatches).Assembly);
                _applied = true;
            }
            catch (System.Exception)
            {
                // Log disabled state from ModEnabled etc.
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
            catch (System.Exception) { }
        }
    }

    [HarmonyPatch(typeof(CharacterSpawnerRoot), nameof(CharacterSpawnerRoot.AddCreatedCharacter))]
    public static class CharacterSpawnerRoot_AddCreatedCharacter_Patch
    {
        /// <summary>Presets excluded from registration and adaptive AI: NPC, merchant, non-aggro (lightman), turret, non-mob (dummy, horse, companion, chicken, etc.). 터렛은 게임 원래 로직만 사용.</summary>
        private static readonly HashSet<string> PresetNamesExcludedFromRegistration = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EnemyPreset_QuestGiver_Alex",
            "EnemyPreset_QuestGiver_Fo",
            "EnemyPreset_QuestGiver_XiaoMing",
            "EnemyPreset_Merchant_Jeff",
            "EnemyPreset_Merchant_Myst",
            "EnemyPreset_Merchant_Myst0",
            "EnemyPreset_Merchant_Test",
            "PetPreset_NormalPet",
            "EnemyPreset_Melee_UltraMan",
            "EnemyPreset_GunTurret",
            "MatePreset_PMC",
            "EnemyPreset_Basement",
            "EnemyPreset_BoomCar",
        };

        private const string PresetPrefixMerchant = "EnemyPreset_Merchant_";
        private const string PresetPrefixQuestGiver = "EnemyPreset_QuestGiver_";
        private const string PresetPrefixPet = "PetPreset_";
        private const string PresetPrefixDummy = "DummyEnemyCharacterRandomPresetLv";
        private const string PresetPrefixVehicleTest = "EnemyPreset_VehicleTest";
        private const string PresetPrefixSpawnAnimal = "SpawnPreset_Animal_";
        private const string PresetPrefixMate = "MatePreset_";

        private static bool IsExcludedPresetFromRegistration(CharacterMainControl c)
        {
            if (c?.characterPreset == null) return false;
            string name = c.characterPreset.name;
            if (PresetNamesExcludedFromRegistration.Contains(name)) return true;
            if (name.StartsWith(PresetPrefixMerchant, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixQuestGiver, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixPet, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixDummy, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixVehicleTest, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixSpawnAnimal, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(PresetPrefixMate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(CharacterMainControl c)
        {
            if (c == null) return;
            if (!Team.IsEnemy(c.Team, Teams.player)) return;
            if (c.aiCharacterController == null) return;
            // Do not register NPC, merchant, non-aggro (lightman), non-mob presets. Registering would include them in ForEachSpawnedEnemyAI and cause follow etc. issues.
            if (IsExcludedPresetFromRegistration(c))
                return;
            PlayerBehaviorCollector.RegisterSpawnedEnemyAI(c.aiCharacterController);
            AI_PathControlPatches.ApplyDefaultSpeedMultiplierFromSettings(c);
            if (PlayerBehaviorCollector.IsInBase()) return;
            // Move while firing: melee has no move when attacking (shootCanMove=false). Ranged can move while firing when setting enabled.
            if (c.GetMeleeWeapon() != null)
                c.aiCharacterController.shootCanMove = false;
            else if (AdaptiveAISettings.MoveWhileFiringEnabled)
                c.aiCharacterController.shootCanMove = true;
            // On spawn frame skip heavy apply → apply in next TickParamsAndFallbackDodge (0.1s) via EnsureAdaptiveParamsApplied. Reduces hitch when many enemies registered at once on player scene load.
            if (PlayerBehaviorCollector.IsSpawnFrameFor(c.aiCharacterController)) return;
            AICharacterControllerPatches.ApplyAdaptiveParamsOnce(c.aiCharacterController);
        }
    }
}
