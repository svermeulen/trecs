using UnityEngine;
using UnityEngine.SceneManagement;

namespace Trecs.Samples
{
    public class SampleCycler : MonoBehaviour
    {
        static SampleCycler _instance;

        public void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void Update()
        {
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.DownArrow))
            {
                CycleScene(1);
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.UpArrow))
            {
                CycleScene(-1);
            }
        }

        static void CycleScene(int direction)
        {
            int count = SceneManager.sceneCountInBuildSettings;
            if (count <= 0)
            {
                return;
            }
            int current = SceneManager.GetActiveScene().buildIndex;
            int next = ((current + direction) % count + count) % count;
            SceneManager.LoadScene(next);
        }
    }
}
