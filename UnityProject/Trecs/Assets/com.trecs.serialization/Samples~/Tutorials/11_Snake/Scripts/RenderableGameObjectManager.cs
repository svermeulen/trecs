using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Trecs.Serialization.Samples.Snake
{
    public partial class RenderableGameObjectManager : IDisposable
    {
        readonly DisposeCollection _subscriptions = new();
        readonly Dictionary<int, Func<GameObject>> _factories = new();
        readonly Dictionary<int, Stack<GameObject>> _pools = new();
        readonly Dictionary<int, GameObject> _activeById = new();
        readonly Dictionary<int, int> _goIdToPrefabId = new();
        readonly Transform _activeParent;
        readonly Transform _inactiveParent;

        NativeUniquePtr<int> _nextId;

        public RenderableGameObjectManager(World world)
        {
            World = world.CreateAccessor(AccessorRole.Fixed);

            _activeParent = new GameObject("Renderables").transform;

            _inactiveParent = new GameObject("RenderablePool").transform;
            _inactiveParent.gameObject.SetActive(false);

            _nextId = World.Heap.AllocNativeUnique(1);

            World
                .Events.EntitiesWithComponents<GameObjectId, PrefabId>()
                .OnAdded(OnEntityAdded)
                .OnRemoved(OnEntityRemoved)
                .AddTo(_subscriptions);

            World.Events.OnDeserializeStarted(OnDeserializeStarted).AddTo(_subscriptions);
            World.Events.OnDeserializeCompleted(OnDeserializeCompleted).AddTo(_subscriptions);
        }

        WorldAccessor World { get; set; }

        public void RegisterFactory(int prefabId, Func<GameObject> factory)
        {
            _factories.Add(prefabId, factory);
        }

        public GameObject Resolve(GameObjectId id) => _activeById[id.Value];

        [ForEachEntity]
        void OnEntityAdded(in Renderable renderable)
        {
            renderable.GameObjectId = Spawn(renderable.PrefabId);
        }

        [ForEachEntity]
        void OnEntityRemoved(in Renderable renderable)
        {
            Debug.Assert(renderable.GameObjectId.Value > 0);

            var go = _activeById[renderable.GameObjectId.Value];
            var wasRemoved = _activeById.Remove(renderable.GameObjectId.Value);
            Debug.Assert(wasRemoved);

            wasRemoved = _goIdToPrefabId.Remove(renderable.GameObjectId.Value);
            Debug.Assert(wasRemoved);
            DespawnGameObject(renderable.PrefabId, go);
        }

        void DespawnAll()
        {
            foreach (var (goId, go) in _activeById)
            {
                var prefabId = _goIdToPrefabId[goId];
                DespawnGameObject(prefabId, go);
            }

            _activeById.Clear();
            _goIdToPrefabId.Clear();
        }

        void OnDeserializeStarted()
        {
            DespawnAll();
        }

        void OnDeserializeCompleted()
        {
            Debug.Assert(_activeById.Count == 0);
            Debug.Assert(_goIdToPrefabId.Count == 0);

            foreach (var renderable in Renderable.Query(World).MatchByComponents())
            {
                var gameObjectId = renderable.GameObjectId.Value;

                Debug.Assert(gameObjectId > 0);

                var go = SpawnGameObject(renderable.PrefabId);

                _activeById.Add(gameObjectId, go);
                _goIdToPrefabId.Add(gameObjectId, renderable.PrefabId);
            }
        }

        GameObjectId Spawn(int prefabId)
        {
            var go = SpawnGameObject(prefabId);

            ref var nextId = ref _nextId.GetMut(World.Heap);
            var id = nextId;
            nextId++;

            _activeById.Add(id, go);
            _goIdToPrefabId.Add(id, prefabId);
            return new GameObjectId { Value = id };
        }

        GameObject SpawnGameObject(int prefabId)
        {
            GameObject go;

            if (_pools.TryGetValue(prefabId, out var stack) && stack.Count > 0)
            {
                go = stack.Pop();
                go.SetActive(true);
            }
            else
            {
                go = _factories[prefabId]();
            }

            go.transform.SetParent(_activeParent, worldPositionStays: false);
            return go;
        }

        void DespawnGameObject(int prefabId, GameObject go)
        {
            go.transform.SetParent(_inactiveParent, worldPositionStays: false);

            if (!_pools.TryGetValue(prefabId, out var stack))
            {
                stack = new Stack<GameObject>();
                _pools[prefabId] = stack;
            }

            stack.Push(go);
        }

        public void Dispose()
        {
            _subscriptions.Dispose();

            // Note here that we have to check if null because
            // the unity game object destroy order is not predictable
            foreach (var (_, go) in _activeById)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _activeById.Clear();
            _goIdToPrefabId.Clear();

            _nextId.Dispose(World);

            foreach (var stack in _pools.Values)
            {
                foreach (var go in stack)
                {
                    if (go != null)
                    {
                        Object.Destroy(go);
                    }
                }
            }

            _pools.Clear();

            if (_activeParent != null)
            {
                Object.Destroy(_activeParent.gameObject);
            }

            if (_inactiveParent != null)
            {
                Object.Destroy(_inactiveParent.gameObject);
            }
        }

        partial struct Renderable : IAspect, IRead<PrefabId>, IWrite<GameObjectId> { }
    }
}
