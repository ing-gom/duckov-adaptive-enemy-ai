using System.Collections;
using Ducky.Sdk.ModBehaviours;
using AdaptiveEnemyAI.Patches;
using AdaptiveEnemyAI.Services;
using AdaptiveEnemyAI.Systems;
using Saves;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AdaptiveEnemyAI
{
    /// <summary>
    /// Adaptive enemy AI mod: adjusts enemy AI parameters in real time based on player loadout and behavior.
    /// </summary>
    public class ModBehaviour : ModBehaviourBase
    {
        private GameObject? _loadoutServiceObject;
        private GameObject? _behaviorCollectorObject;
        private GameObject? _debugOverlayObject;

        protected override void ModEnabled()
        {
            try
            {
                _loadoutServiceObject = new GameObject("AdaptiveEnemyAI_PlayerLoadoutService");
                _loadoutServiceObject.AddComponent<PlayerLoadoutService>();
                UnityEngine.Object.DontDestroyOnLoad(_loadoutServiceObject);

                _behaviorCollectorObject = new GameObject("AdaptiveEnemyAI_PlayerBehaviorCollector");
                _behaviorCollectorObject.AddComponent<PlayerBehaviorCollector>();
                _behaviorCollectorObject.AddComponent<AdaptiveAIValueRunner>();
                _behaviorCollectorObject.AddComponent<MovementAimSyncRunner>();
                _behaviorCollectorObject.AddComponent<DebugPerceptionRangeDrawer>();
                UnityEngine.Object.DontDestroyOnLoad(_behaviorCollectorObject);

                _debugOverlayObject = new GameObject("AdaptiveEnemyAI_DebugOverlay");
                _debugOverlayObject.AddComponent<DebugOverlay>();
                UnityEngine.Object.DontDestroyOnLoad(_debugOverlayObject);

                StartCoroutine(DelayedLoadBehaviorProfile());
                LevelManager.OnLevelInitialized += SetPetLeaderToMainCharacter;

                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                SavesSystem.OnCollectSaveData += OnCollectSaveData;

                AICharacterControllerPatches.ApplyPatches();
                CharacterMainControlDashPatches.ApplyPatches();
                ItemSettingGunPatches.ApplyPatches();
                CA_DashPatches.ApplyPatches();
                CharacterMainControlSetMoveInputPatches.ApplyPatches();
                AI_PathControlPatches.ApplyPatches();
                ProjectilePatches.ApplyPatches();
                CharacterSpawnerPatches.ApplyPatches();
                CharacterMainControlMeleeSpeedPatches.ApplyPatches();
                UnityEngine.Debug.Log("[AdaptiveEnemyAI] Adaptive enemy AI loaded.");
            }
            catch (System.Exception)
            {
                // Silently ignore init errors in release; enable logging for debugging if needed.
            }
        }

        protected override void ModDisabled()
        {
            try
            {
                AICharacterControllerPatches.RemovePatches();
                CharacterMainControlDashPatches.RemovePatches();
                ItemSettingGunPatches.RemovePatches();
                CA_DashPatches.RemovePatches();
                CharacterMainControlSetMoveInputPatches.RemovePatches();
                AI_PathControlPatches.RemovePatches();
                ProjectilePatches.RemovePatches();
                CharacterSpawnerPatches.RemovePatches();
                CharacterMainControlMeleeSpeedPatches.RemovePatches();
                LevelManager.OnLevelInitialized -= SetPetLeaderToMainCharacter;
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                SavesSystem.OnCollectSaveData -= OnCollectSaveData;
                PlayerBehaviorCollector.ClearSpawnedEnemyAIRegistry();
                if (_loadoutServiceObject != null)
                {
                    UnityEngine.Object.Destroy(_loadoutServiceObject);
                    _loadoutServiceObject = null;
                }
                if (_behaviorCollectorObject != null)
                {
                    UnityEngine.Object.Destroy(_behaviorCollectorObject);
                    _behaviorCollectorObject = null;
                }
                if (_debugOverlayObject != null)
                {
                    UnityEngine.Object.Destroy(_debugOverlayObject);
                    _debugOverlayObject = null;
                }
            }
            catch (System.Exception)
            {
                // Silently ignore cleanup errors in release.
            }
        }

        private IEnumerator DelayedLoadBehaviorProfile()
        {
            yield return new WaitForSeconds(2f);
            try { BehaviorSaveLoadSystem.LoadAndApply(); }
            catch (System.Exception ex) { Debug.LogWarning($"[AdaptiveEnemyAI] DelayedLoad: {ex.Message}"); }
        }

        /// <summary>After level load, sets the pet AI's leader to the main character so the pet follows the player.</summary>
        private static void SetPetLeaderToMainCharacter()
        {
            try
            {
                if (LevelManager.Instance?.MainCharacter == null || LevelManager.Instance?.PetCharacter == null)
                    return;
                var petAI = LevelManager.Instance.PetCharacter.GetComponentInChildren<AICharacterController>();
                if (petAI != null)
                    petAI.leader = LevelManager.Instance.MainCharacter;
            }
            catch (System.Exception) { }
        }

        /// <summary>Clears the enemy AI registry on scene load and resets ephemeral feature state (short-term, habit, multi-axis) to prevent stale cross-map data.</summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PlayerBehaviorCollector.ClearSpawnedEnemyAIRegistry();
            PlayerBehaviorCollector.ResetEphemeralFeatureState();
        }

        /// <summary>Clears the registry when scene unload starts so the 0.1s tick does not iterate a long AI list during transition, reducing hitches on map load.</summary>
        private void OnSceneUnloaded(Scene scene)
        {
            PlayerBehaviorCollector.ClearSpawnedEnemyAIRegistry();
        }

        /// <summary>When the game saves (e.g. entering base), puts the profile into cache so it is written to disk with StoreCachedFile.</summary>
        private void OnCollectSaveData()
        {
            try { BehaviorSaveLoadSystem.Save(); }
            catch (System.Exception ex) { Debug.LogWarning($"[AdaptiveEnemyAI] OnCollectSaveData: {ex.Message}"); }
        }
    }
}
