using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples
{
    public class GameObjectRegistry
    {
        readonly Dictionary<int, GameObject> _registry = new();

        int _idCounter;

        public GameObjectId Register(GameObject obj)
        {
            var id = _idCounter++;
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
