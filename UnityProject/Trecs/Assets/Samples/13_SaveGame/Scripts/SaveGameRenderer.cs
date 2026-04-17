using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Samples.SaveGame
{
    /// <summary>
    /// Renders the Sokoban board as 3D primitives synced each variable
    /// frame from current ECS state. Loading a bookmark just works —
    /// the next frame re-reads the restored world state.
    ///
    /// Rendering order by height:
    ///   - Ground plane          (y = 0)
    ///   - Target markers        (flat disks on the ground)
    ///   - Walls / boxes / player (cubes above the ground)
    /// </summary>
    public partial class SaveGameRenderer : IDisposable
    {
        readonly WorldAccessor _world;

        readonly Transform _root;
        readonly GameObject _ground;
        readonly List<GameObject> _wallPool = new();
        readonly List<GameObject> _targetPool = new();
        readonly List<GameObject> _boxPool = new();
        readonly GameObject _playerCube;

        readonly Material _wallMat;
        readonly Material _targetMat;
        readonly Material _boxMat;
        readonly Material _boxOnTargetMat;

        public SaveGameRenderer(World world)
        {
            _world = world.CreateAccessor(nameof(SaveGameRenderer));

            int gridSize = SaveGameSceneInitializer.GridSize;

            _root = new GameObject("SaveGameBoard").transform;
            _root.transform.position = new(7, 0, 6);

            _ground = CreateGround(gridSize);
            _ground.transform.SetParent(_root, worldPositionStays: false);

            _wallMat = SampleUtil.CreateMaterial(new Color(0.3f, 0.3f, 0.35f));
            _targetMat = SampleUtil.CreateMaterial(new Color(0.9f, 0.2f, 0.25f));
            _boxMat = SampleUtil.CreateMaterial(new Color(0.75f, 0.55f, 0.3f));
            _boxOnTargetMat = SampleUtil.CreateMaterial(new Color(0.45f, 0.85f, 0.4f));

            _playerCube = CreateCube(new Color(0.3f, 0.7f, 0.95f), 0.85f, "Player");
            _playerCube.transform.SetParent(_root, worldPositionStays: false);
        }

        public void Tick()
        {
            SyncWalls();
            SyncTargets();
            SyncBoxesAndRecolorByTargetOverlap();
            SyncPlayer();
        }

        void SyncWalls()
        {
            int index = 0;
            foreach (var wall in WallView.Query(_world).WithTags<SaveGameTags.Wall>())
            {
                var go = GetOrAllocWall(index++);
                go.SetActive(true);
                go.transform.localPosition = ToWorldPos(wall.GridPos, yOffset: 0.5f);
            }
            for (int i = index; i < _wallPool.Count; i++)
            {
                _wallPool[i].SetActive(false);
            }
        }

        void SyncTargets()
        {
            int index = 0;
            foreach (var target in TargetView.Query(_world).WithTags<SaveGameTags.Target>())
            {
                var go = GetOrAllocTarget(index++);
                go.SetActive(true);
                go.transform.localPosition = ToWorldPos(target.GridPos, yOffset: 0.025f);
            }
            for (int i = index; i < _targetPool.Count; i++)
            {
                _targetPool[i].SetActive(false);
            }
        }

        void SyncBoxesAndRecolorByTargetOverlap()
        {
            var targetCells = new HashSet<int2>();
            foreach (var t in TargetView.Query(_world).WithTags<SaveGameTags.Target>())
            {
                targetCells.Add(t.GridPos);
            }

            int index = 0;
            foreach (var box in BoxView.Query(_world).WithTags<SaveGameTags.Box>())
            {
                var go = GetOrAllocBox(index++);
                go.SetActive(true);
                go.transform.localPosition = ToWorldPos(box.GridPos, yOffset: 0.4f);
                var mat = targetCells.Contains(box.GridPos) ? _boxOnTargetMat : _boxMat;
                go.GetComponent<Renderer>().sharedMaterial = mat;
            }
            for (int i = index; i < _boxPool.Count; i++)
            {
                _boxPool[i].SetActive(false);
            }
        }

        void SyncPlayer()
        {
            bool found = false;
            foreach (var player in PlayerView.Query(_world).WithTags<SaveGameTags.Player>())
            {
                _playerCube.SetActive(true);
                _playerCube.transform.localPosition = ToWorldPos(player.GridPos, yOffset: 0.425f);
                found = true;
                break;
            }
            if (!found)
            {
                _playerCube.SetActive(false);
            }
        }

        GameObject GetOrAllocWall(int index)
        {
            while (_wallPool.Count <= index)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Wall_{_wallPool.Count}";
                go.transform.localScale = Vector3.one;
                go.GetComponent<Renderer>().sharedMaterial = _wallMat;
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(_root, worldPositionStays: false);
                _wallPool.Add(go);
            }
            return _wallPool[index];
        }

        GameObject GetOrAllocTarget(int index)
        {
            while (_targetPool.Count <= index)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Target_{_targetPool.Count}";
                go.transform.localScale = new Vector3(0.8f, 0.05f, 0.8f);
                go.GetComponent<Renderer>().sharedMaterial = _targetMat;
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(_root, worldPositionStays: false);
                _targetPool.Add(go);
            }
            return _targetPool[index];
        }

        GameObject GetOrAllocBox(int index)
        {
            while (_boxPool.Count <= index)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Box_{_boxPool.Count}";
                go.transform.localScale = Vector3.one * 0.8f;
                go.GetComponent<Renderer>().sharedMaterial = _boxMat;
                UnityEngine.Object.Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(_root, worldPositionStays: false);
                _boxPool.Add(go);
            }
            return _boxPool[index];
        }

        static GameObject CreateCube(Color color, float scale, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().sharedMaterial = SampleUtil.CreateMaterial(color);
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        static GameObject CreateGround(int gridSize)
        {
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
            plane.name = "GridPlane";
            plane.transform.localScale = new Vector3(gridSize / 10f, 1f, gridSize / 10f);
            plane.transform.position = new Vector3(
                (gridSize - 1) * 0.5f,
                0f,
                (gridSize - 1) * 0.5f
            );
            plane.GetComponent<Renderer>().sharedMaterial = SampleUtil.CreateMaterial(
                new Color(0.15f, 0.15f, 0.18f)
            );
            UnityEngine.Object.Destroy(plane.GetComponent<Collider>());
            return plane;
        }

        static Vector3 ToWorldPos(int2 gridPos, float yOffset)
        {
            return new Vector3(gridPos.x, yOffset, gridPos.y);
        }

        public void Dispose()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root.gameObject);
            }
        }

        partial struct PlayerView : IAspect, IRead<GridPos> { }

        partial struct BoxView : IAspect, IRead<GridPos> { }

        partial struct TargetView : IAspect, IRead<GridPos> { }

        partial struct WallView : IAspect, IRead<GridPos> { }
    }
}
