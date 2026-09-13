using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ItemStatsSystem.Items;
using AdaptiveEnemyAI.Data;

namespace AdaptiveEnemyAI.Services
{
    /// <summary>
    /// Reads player loadout and updates <see cref="PlayerLoadoutProfile"/>.
    /// Updates only on weapon/slot (armor etc.) change. (Event-driven, no polling)
    /// </summary>
    public sealed class PlayerLoadoutService : MonoBehaviour
    {
        private static PlayerLoadoutProfile _profile = new PlayerLoadoutProfile();
        private static bool _valid;

        /// <summary>Fires once after loadout (weapon/armor) actually changed. Subscribe when reapplying to spawned enemies only.</summary>
        public static event System.Action? OnLoadoutChanged;

        private static float _lastWeaponRange;
        private static bool _lastIsMelee;
        private static string? _lastGunTypeTag;
        private static float _lastBodyArmor;
        private static float _lastHeadArmor;
        private static bool _loadoutEverRefreshed;

        /// <summary>Current profile. Default if not yet collected or no Main.</summary>
        public static PlayerLoadoutProfile Current => _profile;

        /// <summary>Whether refreshed at least once with valid player data.</summary>
        public static bool IsValid => _valid;

        private void OnEnable()
        {
            CharacterMainControl.OnMainCharacterChangeHoldItemAgentEvent += OnHoldItemChanged;
            CharacterMainControl.OnMainCharacterSlotContentChangedEvent += OnSlotContentChanged;
        }

        private void OnDisable()
        {
            CharacterMainControl.OnMainCharacterChangeHoldItemAgentEvent -= OnHoldItemChanged;
            CharacterMainControl.OnMainCharacterSlotContentChangedEvent -= OnSlotContentChanged;
        }

        private void OnHoldItemChanged(CharacterMainControl main, DuckovItemAgent agent)
        {
            if (main != null && main == CharacterMainControl.Main)
                Refresh();
        }

        private void OnSlotContentChanged(CharacterMainControl main, Slot slot)
        {
            if (main != null && main == CharacterMainControl.Main)
                Refresh();
        }

        private void Update()
        {
            // Skip Refresh during scene change/loading (avoid lag before map load)
            if (!LevelManager.LevelInited || LevelManager.Instance == null)
                return;
            var main = CharacterMainControl.Main;
            if (main == null)
            {
                _valid = false;
                return;
            }
            // Refresh once when player just appeared (events handle after that)
            if (!_valid)
                Refresh();
        }

        /// <summary>Refresh loadout profile once. (For external force refresh)</summary>
        public void Refresh()
        {
            var main = CharacterMainControl.Main;
            if (main == null)
            {
                _valid = false;
                return;
            }

            _profile.WeaponRange = 0f;
            _profile.IsMelee = false;
            _profile.GunTypeTag = null;
            _profile.BodyArmor = 0f;
            _profile.HeadArmor = 0f;

            var gun = main.GetGun();
            if (gun != null)
            {
                _profile.WeaponRange = gun.BulletDistance;
                _profile.IsMelee = false;
                _profile.GunTypeTag = GetGunTypeTagFromGun(gun);
            }
            else if (main.GetMeleeWeapon() != null)
            {
                _profile.WeaponRange = 0f;
                _profile.IsMelee = true;
                _profile.GunTypeTag = "Melee";
            }

            if (main.Health != null)
            {
                _profile.BodyArmor = main.Health.BodyArmor;
                _profile.HeadArmor = main.Health.HeadArmor;
            }

            bool changed = _loadoutEverRefreshed && (
                _profile.WeaponRange != _lastWeaponRange ||
                _profile.IsMelee != _lastIsMelee ||
                _profile.GunTypeTag != _lastGunTypeTag ||
                _profile.BodyArmor != _lastBodyArmor ||
                _profile.HeadArmor != _lastHeadArmor);

            _lastWeaponRange = _profile.WeaponRange;
            _lastIsMelee = _profile.IsMelee;
            _lastGunTypeTag = _profile.GunTypeTag;
            _lastBodyArmor = _profile.BodyArmor;
            _lastHeadArmor = _profile.HeadArmor;
            _loadoutEverRefreshed = true;
            _valid = true;

            if (changed)
                OnLoadoutChanged?.Invoke();
        }

        /// <summary>Supported gun/weapon type tags (PST, SMG, AR, BR, SNP, SHT, MAG, PWS, ARR, Rocket, Melee, etc.).</summary>
        private static readonly HashSet<string> KnownWeaponTypeTags = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "Tag_GunType_PST", "Tag_GunType_SMG", "Tag_GunType_AR", "Tag_GunType_BR", "Tag_GunType_SNP",
            "Tag_GunType_SHT", "Tag_GunType_MAG", "Tag_GunType_PWS", "Tag_GunType_ARR", "Tag_GunType_Rocket",
            "Tag_Melee", "Melee"
        };

        /// <summary>Returns one GunType tag from gun agent via Item.Tags. null if none (common). Used for player and enemy.</summary>
        public static string? GetGunTypeTagFromGun(object? gun)
        {
            if (gun == null) return null;
            var itemProp = gun.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            if (itemProp?.GetValue(gun) is not object item || item == null) return null;
            var tagsProp = item.GetType().GetProperty("Tags", BindingFlags.Public | BindingFlags.Instance);
            if (tagsProp?.GetValue(item) is not IEnumerable tags) return null;

            foreach (var t in tags)
            {
                var s = t?.ToString();
                if (string.IsNullOrEmpty(s)) continue;
                if (KnownWeaponTypeTags.Contains(s))
                    return s;
            }
            return null;
        }
    }
}
