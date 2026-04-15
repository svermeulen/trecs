using System.Collections.Generic;
using Trecs.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Trecs.Samples
{
    /// <summary>
    /// Persistent scene cycler for navigating between Trecs samples.
    ///
    /// Add this component to a GameObject in any scene. It persists across
    /// scene loads via DontDestroyOnLoad with singleton protection.
    ///
    /// Setup: Ensure all sample scenes are added to Build Settings
    /// (File → Build Settings → drag scenes into the list).
    ///
    /// Controls:
    ///   Right Arrow / N  — Next sample
    ///   Left Arrow  / P  — Previous sample
    ///   1-9              — Jump to sample by number
    ///   Escape           — Return to first scene
    /// </summary>
    public class SampleCycler : MonoBehaviour
    {
        static readonly TrecsLog _log = new(nameof(SampleCycler));
        readonly List<string> _sceneNames = new();

        static SampleCycler _instance;

        int _currentIndex = -1;

        void Awake()
        {
            if (_instance != null)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            DiscoverScenesFromBuildSettings();
        }

        void Start()
        {
            LoadScene(0);
        }

        void DiscoverScenesFromBuildSettings()
        {
            int count = SceneManager.sceneCountInBuildSettings;

            Assert.That(_sceneNames.Count == 0);

            for (int i = 0; i < count; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);

                if (name != "AllSamples")
                {
                    _sceneNames.Add(name);
                }
            }
        }

        int FindCurrentSceneIndex()
        {
            string currentName = SceneManager.GetActiveScene().name;

            for (int i = 0; i < _sceneNames.Count; i++)
            {
                if (_sceneNames[i] == currentName)
                    return i;
            }

            return 0;
        }

        void Update()
        {
            if (_sceneNames.Count == 0)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.N))
            {
                LoadScene((_currentIndex + 1) % _sceneNames.Count);
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.P))
            {
                LoadScene((_currentIndex - 1 + _sceneNames.Count) % _sceneNames.Count);
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                LoadScene(0);
            }
            else
            {
                // Number keys 1-9 jump to sample by index
                for (int i = 0; i < 9 && i < _sceneNames.Count; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        LoadScene(i);
                        break;
                    }
                }
            }
        }

        void LoadScene(int index)
        {
            if (index == _currentIndex || index < 0 || index >= _sceneNames.Count)
            {
                return;
            }

            _currentIndex = index;
            SceneManager.LoadScene(_sceneNames[index]);
        }

        void OnGUI()
        {
            if (_sceneNames == null || _sceneNames.Count == 0)
                return;

            string sceneName =
                _currentIndex < _sceneNames.Count ? _sceneNames[_currentIndex] : "Unknown";

            string title = $"[{_currentIndex + 1}/{_sceneNames.Count}] {sceneName}";
            string controls = "\u2190 \u2192 or N/P to cycle  |  1-9 to jump  |  Esc for first";

            var content = new GUIContent(title + "    " + controls);
            var size = TitleStyle.CalcSize(content);

            var bgRect = new Rect(10, 10, size.x + 20, size.y + 10);
            var labelRect = new Rect(bgRect.x + 10, bgRect.y + 5, size.x, size.y);

            // Semi-transparent background for readability over arbitrary scene contents.
            var prevColor = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.DrawTexture(bgRect, Texture2D.whiteTexture);
            GUI.color = prevColor;

            // Draw twice with a 1px horizontal offset for faux-bold. Unity's default
            // IMGUI font (LegacyRuntime.ttf) has no real bold variant, so FontStyle.Bold
            // alone is not visibly bold.
            GUI.Label(labelRect, content, TitleStyle);
            GUI.Label(
                new Rect(labelRect.x + 1, labelRect.y, labelRect.width, labelRect.height),
                content,
                TitleStyle
            );
        }

        // Lazy-initialized styles to avoid allocation in OnGUI hot path
        GUIStyle _titleStyle;
        GUIStyle _hintStyle;

        GUIStyle TitleStyle
        {
            get
            {
                if (_titleStyle == null)
                {
                    _titleStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 22,
                        fontStyle = FontStyle.Bold,
                    };
                    // Assign textColor explicitly after construction. The
                    // `normal = { textColor = ... }` object-initializer pattern
                    // is unreliable for GUIStyles inherited from GUI.skin and
                    // can silently leave the editor skin's default (dark) color.
                    _titleStyle.normal.textColor = Color.white;
                }
                return _titleStyle;
            }
        }

        GUIStyle HintStyle
        {
            get
            {
                if (_hintStyle == null)
                {
                    _hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                    _hintStyle.normal.textColor = new Color(1, 1, 1, 0.5f);
                }
                return _hintStyle;
            }
        }
    }
}
