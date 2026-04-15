using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Owns the Unity GameObjects that visualize snake entities and keeps
    /// them in sync with ECS state across bookmark loads and recording
    /// playback. This is the load-bearing piece that lets the Snake sample
    /// demonstrate state restoration: when a bookmark is loaded the
    /// deserializer overwrites all entity component data, so any existing
    /// GameObjectId mappings become stale. The manager subscribes to the
    /// world's OnDeserializeStarted/OnDeserializeCompleted events, destroys
    /// every GameObject it owns, and rebuilds them from the rehydrated
    /// entity set — rewriting each entity's GameObjectId to match the new
    /// dictionary key.
    /// </summary>
    public class SnakeGameObjectManager : IDisposable
    {
        readonly Dictionary<int, GameObject> _byId = new();
        readonly DisposeCollection _subscriptions = new();
        readonly WorldAccessor _ecs;
        readonly Material _headMaterial;
        readonly Material _segmentMaterial;
        readonly Material _foodMaterial;
        readonly Transform _entityRoot;

        int _nextId;

        public SnakeGameObjectManager(World world)
        {
            _ecs = world.CreateAccessor(nameof(SnakeGameObjectManager));

            _headMaterial = SampleUtil.CreateMaterial(new Color(0.2f, 0.9f, 0.4f));
            _segmentMaterial = SampleUtil.CreateMaterial(new Color(0.4f, 0.7f, 0.3f));
            _foodMaterial = SampleUtil.CreateMaterial(new Color(0.9f, 0.7f, 0.2f));

            // Parent for visualization GameObjects so they don't clutter the
            // scene root.
            _entityRoot = new GameObject("SnakeEntities").transform;

            _ecs.Events.OnDeserializeStarted(OnDeserializeStarted).AddTo(_subscriptions);
            _ecs.Events.OnDeserializeCompleted(OnDeserializeCompleted).AddTo(_subscriptions);
        }

        public GameObjectId CreateHead() => Create(_headMaterial, scale: 1.0f, name: "Head");

        public GameObjectId CreateSegment() =>
            Create(_segmentMaterial, scale: 0.85f, name: "Segment");

        public GameObjectId CreateFood() => Create(_foodMaterial, scale: 0.6f, name: "Food");

        public void Destroy(GameObjectId id)
        {
            var go = _byId[id.Value];
            _byId.Remove(id.Value);
            UnityEngine.Object.Destroy(go);
        }

        public GameObject Resolve(GameObjectId id) => _byId[id.Value];

        GameObjectId Create(Material material, float scale, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(_entityRoot, worldPositionStays: false);
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            // Strip the collider — we don't need physics and they slow down
            // CreatePrimitive churn during rapid bookmark loads.
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());

            var id = _nextId++;
            _byId.Add(id, go);
            return new GameObjectId(id);
        }

        void OnDeserializeStarted()
        {
            // Destroy every visualization GameObject we own. The deserializer
            // is about to overwrite all entity component data, so any
            // existing GameObjectId mappings cannot be trusted to remain
            // valid. We rebuild from scratch in OnDeserializeCompleted.
            foreach (var go in _byId.Values)
            {
                UnityEngine.Object.Destroy(go);
            }
            _byId.Clear();
        }

        void OnDeserializeCompleted()
        {
            // For each tagged entity now in ECS, create a fresh GameObject
            // and rewrite the entity's GameObjectId to point to the new one.
            // Order doesn't matter — each entity gets its own unique id.
            RecreateFor<SnakeTags.SnakeHead>(CreateHead);
            RecreateFor<SnakeTags.SnakeSegment>(CreateSegment);
            RecreateFor<SnakeTags.SnakeFood>(CreateFood);
        }

        void RecreateFor<TTag>(Func<GameObjectId> factory)
            where TTag : struct, ITag
        {
            foreach (var entityIndex in _ecs.Query().WithTags<TTag>().EntityIndices())
            {
                var newId = factory();
                _ecs.Component<GameObjectId>(entityIndex).Write = newId;
            }
        }

        public void Dispose()
        {
            _subscriptions.Dispose();
            foreach (var go in _byId.Values)
            {
                UnityEngine.Object.Destroy(go);
            }
            _byId.Clear();
            if (_entityRoot != null)
            {
                UnityEngine.Object.Destroy(_entityRoot.gameObject);
            }
        }
    }
}
