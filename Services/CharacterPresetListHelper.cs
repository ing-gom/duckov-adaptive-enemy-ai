using System;
using System.Collections.Generic;
using UnityEngine;
using Duckov.Utilities;

namespace AdaptiveEnemyAI.Services
{
    /// <summary>
    /// Helper to get the game's character preset list.
    /// Uses GameplayDataSettings.CharacterRandomPresetData.presets (public field).
    /// </summary>
    public static class CharacterPresetListHelper
    {
        /// <summary>
        /// Gets the full character preset list.
        /// Each item: (name, displayName).
        /// </summary>
        public static List<(string name, string displayName)> GetAllCharacterPresets()
        {
            var result = new List<(string, string)>();
            try
            {
                GameplayDataSettings.CharacterRandomPresets? data = GameplayDataSettings.CharacterRandomPresetData;
                if (data?.presets == null)
                {
#if DEBUG
                    if (data == null)
                        Debug.Log("[AdaptiveEnemyAI] CharacterPresetListHelper: CharacterRandomPresetData is null (Resources not loaded?).");
#endif
                    return result;
                }

                foreach (CharacterRandomPreset? preset in data.presets)
                {
                    if (preset == null) continue;

                    string name = preset.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    string displayName = preset.DisplayName;
                    if (string.IsNullOrEmpty(displayName))
                        displayName = preset.name;

                    result.Add((name, displayName));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdaptiveEnemyAI] CharacterPresetListHelper GetAllCharacterPresets: {ex.Message}");
            }

            return result;
        }

#if DEBUG
        private static bool _loggedPresetList;

        /// <summary>
        /// Logs character preset list to console in debug only.
        /// After first F7 log, call with forceRefresh=true to log again.
        /// </summary>
        public static void LogAllCharacterPresetsToConsole(bool forceRefresh = false)
        {
            if (_loggedPresetList && !forceRefresh) return;

            var presets = GetAllCharacterPresets();
            if (presets.Count == 0)
            {
                Debug.Log("[AdaptiveEnemyAI] Character preset list: (empty or load failed)");
                _loggedPresetList = true;
                return;
            }

            Debug.Log($"[AdaptiveEnemyAI] Character preset list (total {presets.Count})");
            foreach (var (name, displayName) in presets)
                Debug.Log($"  preset={name}, display={displayName}");
            _loggedPresetList = true;
        }
#endif
    }
}
