using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples
{
    public class GameObjectRegistry
    {
        readonly Dictionary<int, GameObject> _registry = new();

        // Reserve 0 for null
        int _nextId = 1;

        public GameObjectId Register(GameObject obj)
        {
            var id = _nextId++;
            _registry.Add(id, obj);
            return new GameObjectId { Value = id };
        }

        public GameObject Resolve(GameObjectId id)
        {
            return _registry[id.Value];
        }

        public void Unregister(GameObjectId id)
        {
            _registry.Remove(id.Value);
        }
    }
}
