using System;
using System.Linq;
using UnityEditor;
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
    /// Setup: Assign the sample scenes to the SceneAssets list in the inspector.
    /// The names are serialized for runtime use; the scenes must still be added
    /// to Build Settings for the player to load them.
    ///
    /// Controls:
    ///   Right Arrow / N  — Next sample
    ///   Left Arrow  / P  — Previous sample
    ///   1-9              — Jump to one of the first nine samples by number
    ///                      (use the arrow keys to reach samples 10+)
    ///   Escape           — Return to first scene
    /// </summary>
    public class SampleCycler : MonoBehaviour
    {
#if UNITY_EDITOR
        [SerializeField]
        SceneAsset[] _sceneAssets;
#endif

        [SerializeField, HideInInspector]
        string[] _sceneNames;

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
        }

        void Start()
        {
            LoadScene(0);
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            _sceneNames =
                _sceneAssets?.Where(s => s != null).Select(s => s.name).ToArray()
                ?? Array.Empty<string>();
        }
#endif

        int FindCurrentSceneIndex()
        {
            string currentName = SceneManager.GetActiveScene().name;

            for (int i = 0; i < _sceneNames.Length; i++)
            {
                if (_sceneNames[i] == currentName)
                    return i;
            }

            return 0;
        }

        void Update()
        {
            if (_sceneNames.Length == 0)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.N))
            {
                LoadScene((_currentIndex + 1) % _sceneNames.Length);
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.P))
            {
                LoadScene((_currentIndex - 1 + _sceneNames.Length) % _sceneNames.Length);
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                LoadScene(0);
            }
            else
            {
                // Number keys 1-9 jump to sample by index
                for (int i = 0; i < 9 && i < _sceneNames.Length; i++)
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
            if (index == _currentIndex || index < 0 || index >= _sceneNames.Length)
            {
                return;
            }

            _currentIndex = index;
            SceneManager.LoadScene(_sceneNames[index]);
        }

        void OnGUI()
        {
            if (_sceneNames == null || _sceneNames.Length == 0)
                return;

            string sceneName =
                _currentIndex < _sceneNames.Length ? _sceneNames[_currentIndex] : "Unknown";

            string title = $"[{_currentIndex + 1}/{_sceneNames.Length}] {sceneName}";
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

            DrawNavButtons();
        }

        void DrawNavButtons()
        {
            const float buttonWidth = 120;
            const float buttonHeight = 60;
            const float spacing = 20;
            const float bottomMargin = 30;

            float totalWidth = buttonWidth * 2 + spacing;
            float startX = (Screen.width - totalWidth) / 2f;
            float y = Screen.height - buttonHeight - bottomMargin;

            var prevRect = new Rect(startX, y, buttonWidth, buttonHeight);
            var nextRect = new Rect(startX + buttonWidth + spacing, y, buttonWidth, buttonHeight);

            if (GUI.Button(prevRect, "\u25C0 Prev", NavButtonStyle))
            {
                LoadScene((_currentIndex - 1 + _sceneNames.Length) % _sceneNames.Length);
            }

            if (GUI.Button(nextRect, "Next \u25B6", NavButtonStyle))
            {
                LoadScene((_currentIndex + 1) % _sceneNames.Length);
            }
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

        GUIStyle _navButtonStyle;

        GUIStyle NavButtonStyle
        {
            get
            {
                if (_navButtonStyle == null)
                {
                    _navButtonStyle = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = 22,
                        fontStyle = FontStyle.Bold,
                    };
                }
                return _navButtonStyle;
            }
        }
    }
}
