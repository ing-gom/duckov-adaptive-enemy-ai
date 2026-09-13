using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov;
using AdaptiveEnemyAI.Settings;

namespace AdaptiveEnemyAI.Systems
{
    /// <summary>
    /// Draws sight/sound range with LineRenderer in debug. Always visible in Game view.
    /// - Enemy: create circle once when first detected after spawn (fixed position).
    /// - Player: update position every frame to follow player.
    /// Circles drawn 1.5m above ground for visibility.
    /// </summary>
    [DefaultExecutionOrder(33000)]
    public class DebugPerceptionRangeDrawer : MonoBehaviour
    {
        private const int CircleSegments = 48;
        private const float CircleHeightOffset = 1.5f;
        private const float ScanInterval = 0.5f;
        private const float ReferenceSoundRadiusForHearing = 15f;
        private const float PlayerQuackSoundRadius = 15f;
        private const float LineWidth = 0.15f;

        private Transform _root;
        private readonly HashSet<int> _drawnEnemyInstanceIds = new HashSet<int>();
        private float _nextScanTime;
        private Transform _playerQuackCircle;
        private Transform _playerGunCircle;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearAllCircles();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _drawnEnemyInstanceIds.Clear();
            _playerQuackCircle = null;
            _playerGunCircle = null;
            ClearAllCircles();
        }

        private void EnsureRoot()
        {
            if (_root != null) return;
            var go = new GameObject("AdaptiveEnemyAI_DebugPerceptionCircles");
            go.transform.SetParent(transform, worldPositionStays: false);
            _root = go.transform;
        }

        private void ClearAllCircles()
        {
            _playerQuackCircle = null;
            _playerGunCircle = null;
            if (_root != null && _root.gameObject != null)
            {
                UnityEngine.Object.Destroy(_root.gameObject);
                _root = null;
            }
        }

        private Shader GetLineShader()
        {
            return Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Standard");
        }

        private void LateUpdate()
        {
            bool drawSound = AdaptiveAISettings.DebugDrawEnemySoundRange;
            bool drawSight = AdaptiveAISettings.DebugDrawEnemySightRange;
            bool drawPlayerSound = AdaptiveAISettings.DebugDrawPlayerSoundRange;
            if (!drawPlayerSound)
            {
                if (_playerQuackCircle != null || _playerGunCircle != null)
                    ClearPlayerCircles();
            }
            if (!drawSound && !drawSight && !drawPlayerSound) { ClearAllCircles(); return; }
            // Skip heavy work (FindObjectsOfType etc.) during scene change/loading (avoid lag before map load)
            if (!LevelManager.LevelInited) return;

            var main = CharacterMainControl.Main;
            if (main == null) return;

            EnsureRoot();

            if (drawPlayerSound)
            {
                EnsurePlayerSoundCircles(main);
                UpdatePlayerCirclePositions(main);
            }

            float now = Time.time;
            if (drawSound || drawSight)
            {
                if (now < _nextScanTime) return;
                _nextScanTime = now + ScanInterval;

                var ais = UnityEngine.Object.FindObjectsOfType<AICharacterController>();
                for (int i = 0; i < ais.Length; i++)
                {
                    var ai = ais[i];
                    if (ai == null) continue;
                    var c = ai.CharacterMainControl;
                    if (c == null || c == main) continue;
                    if (c.Team == Teams.player) continue;

                    int id = ai.GetInstanceID();
                    if (_drawnEnemyInstanceIds.Contains(id)) continue;
                    _drawnEnemyInstanceIds.Add(id);

                    Vector3 pos = ai.transform.position;
                    pos.y += CircleHeightOffset;
                    if (drawSound)
                        CreateCircleGameObject("EnemySound", pos, ReferenceSoundRadiusForHearing * ai.hearingAbility, Color.cyan);
                    if (drawSight)
                        CreateCircleGameObject("EnemySight", pos, ai.sightDistance, Color.yellow);
                }
            }
        }

        private void ClearPlayerCircles()
        {
            if (_playerQuackCircle != null && _playerQuackCircle.gameObject != null)
                UnityEngine.Object.Destroy(_playerQuackCircle.gameObject);
            if (_playerGunCircle != null && _playerGunCircle.gameObject != null)
                UnityEngine.Object.Destroy(_playerGunCircle.gameObject);
            _playerQuackCircle = null;
            _playerGunCircle = null;
        }

        private void EnsurePlayerSoundCircles(CharacterMainControl main)
        {
            if (_playerQuackCircle == null)
            {
                var go = CreateCircleGameObject("PlayerQuack", Vector3.zero, PlayerQuackSoundRadius, new Color(1f, 0.6f, 0f));
                if (go != null) _playerQuackCircle = go.transform;
            }
            float gunRadius = GetPlayerGunSoundRange(main);
            if (gunRadius > 0f)
            {
                if (_playerGunCircle == null)
                {
                    var go = CreateCircleGameObject("PlayerGun", Vector3.zero, gunRadius, Color.red);
                    if (go != null) _playerGunCircle = go.transform;
                }
            }
            else
            {
                if (_playerGunCircle != null && _playerGunCircle.gameObject != null)
                    UnityEngine.Object.Destroy(_playerGunCircle.gameObject);
                _playerGunCircle = null;
            }
        }

        private void UpdatePlayerCirclePositions(CharacterMainControl main)
        {
            Vector3 pos = main.transform.position;
            pos.y += CircleHeightOffset;
            if (_playerQuackCircle != null)
                UpdateCircleToPosition(_playerQuackCircle, pos, PlayerQuackSoundRadius);
            if (_playerGunCircle != null)
            {
                float gunRadius = GetPlayerGunSoundRange(main);
                if (gunRadius > 0f)
                    UpdateCircleToPosition(_playerGunCircle, pos, gunRadius);
            }
        }

        private static void UpdateCircleToPosition(Transform circleTransform, Vector3 center, float radius)
        {
            circleTransform.position = center;
            var lr = circleTransform.GetComponent<LineRenderer>();
            if (lr == null) return;
            var positions = new Vector3[CircleSegments + 1];
            for (int i = 0; i <= CircleSegments; i++)
            {
                float a = (float)i / CircleSegments * Mathf.PI * 2f;
                positions[i] = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            }
            lr.SetPositions(positions);
        }

        private GameObject CreateCircleGameObject(string label, Vector3 center, float radius, Color color)
        {
            if (_root == null) return null;
            var go = new GameObject("Circle_" + label);
            go.transform.SetParent(_root, worldPositionStays: false);
            go.transform.position = center;

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.positionCount = CircleSegments + 1;
            lr.startWidth = LineWidth;
            lr.endWidth = LineWidth;
            lr.startColor = color;
            lr.endColor = color;
            var shader = GetLineShader();
            if (shader != null)
            {
                var mat = new Material(shader) { color = color };
                lr.material = mat;
            }

            var positions = new Vector3[CircleSegments + 1];
            for (int i = 0; i <= CircleSegments; i++)
            {
                float a = (float)i / CircleSegments * Mathf.PI * 2f;
                positions[i] = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            }
            lr.SetPositions(positions);
            return go;
        }

        private static float GetPlayerGunSoundRange(CharacterMainControl main)
        {
            if (main == null) return 0f;
            var gun = main.GetGun();
            if (gun == null) return 0f;
            return gun.SoundRange;
        }
    }
}
