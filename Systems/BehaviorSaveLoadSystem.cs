using System;
using System.IO;
using UnityEngine;
using AdaptiveEnemyAI.Data;
using AdaptiveEnemyAI.Services;
using Saves;

namespace AdaptiveEnemyAI.Systems
{
    /// <summary>
    /// Behavior profile save/load. SavesSystem + ES3 fallback.
    /// </summary>
    public static class BehaviorSaveLoadSystem
    {
        private const string SAVE_KEY = "AdaptiveEnemyAI_BehaviorProfile";

        /// <summary>Save current profile. If preserveMovementFromExisting, keep existing file movement fields and only update combat fields (one-time at combat start).</summary>
        public static void Save(bool preserveMovementFromExisting = false)
        {
            try
            {
                if (string.IsNullOrEmpty(SavesSystem.CurrentFilePath))
                {
                    Debug.LogWarning("[AdaptiveEnemyAI] Save skipped: CurrentFilePath empty (combat stats not saved).");
                    return;
                }
                var collector = PlayerBehaviorCollector.GetInstanceForSave()
                    ?? UnityEngine.Object.FindObjectOfType<PlayerBehaviorCollector>();
                if (collector == null)
                {
                    Debug.LogWarning("[AdaptiveEnemyAI] Save skipped: PlayerBehaviorCollector not found (combat stats not saved).");
                    return;
                }
                var data = collector.ToSaveData();
                if (data == null)
                {
                    Debug.LogWarning("[AdaptiveEnemyAI] Save skipped: ToSaveData() returned null (combat stats not saved).");
                    return;
                }
                if (preserveMovementFromExisting)
                {
                    var existing = LoadData();
                    if (existing != null)
                    {
                        data.MoveStrengthEma = existing.MoveStrengthEma;
                        data.RunRatioEma = existing.RunRatioEma;
                        data.AimChangeVariance = existing.AimChangeVariance;
                        data.DashCountPerMin = existing.DashCountPerMin;
                        data.ShootCountPerMin = existing.ShootCountPerMin;
                        if (existing.Version >= 4)
                        {
                            data.MoveDirDotToEnemyEma = existing.MoveDirDotToEnemyEma;
                            data.LateralMoveRatioEma = existing.LateralMoveRatioEma;
                            data.MoveDirDotVariance = existing.MoveDirDotVariance;
                        }
                    }
                }
                SavesSystem.Save(SAVE_KEY, data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdaptiveEnemyAI] Save failed (combat stats not saved): {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void LoadAndApply()
        {
            try
            {
                var collector = PlayerBehaviorCollector.GetInstanceForSave()
                    ?? UnityEngine.Object.FindObjectOfType<PlayerBehaviorCollector>();
                if (collector == null) return;
                var data = LoadData();
                collector.LoadFromSaveData(data);
            }
            catch (Exception)
            {
                // Ignore load errors (e.g. no save file yet).
            }
        }

        private static BehaviorProfileSaveData? LoadData()
        {
            try
            {
                if (string.IsNullOrEmpty(SavesSystem.CurrentFilePath)) return null;
                var data = SavesSystem.Load<BehaviorProfileSaveData>(SAVE_KEY);
                if (data != null) return data;
                string path = Path.Combine(Application.persistentDataPath, SavesSystem.CurrentFilePath);
                if (!File.Exists(path)) return null;
                try
                {
                    ES3.CacheFile(path);
                    if (ES3.KeyExists(SAVE_KEY, path))
                        return ES3.Load<BehaviorProfileSaveData>(SAVE_KEY, path);
                }
                catch (Exception es3Ex)
                {
                    Debug.LogWarning($"[AdaptiveEnemyAI] ES3 fallback load failed: {es3Ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AdaptiveEnemyAI] LoadData: {ex.Message}");
            }
            return null;
        }
    }
}
