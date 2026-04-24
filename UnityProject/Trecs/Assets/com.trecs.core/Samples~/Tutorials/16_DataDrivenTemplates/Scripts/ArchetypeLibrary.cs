using System;
using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.DataDrivenTemplates
{
    /// <summary>
    /// One entity archetype, composed by name from the registries in
    /// <see cref="ArchetypeLoader"/>. Component and tag names must match entries
    /// in those registries — the loader asserts that they do.
    /// </summary>
    [Serializable]
    public class DataDrivenArchetype
    {
        public string Name = "Unnamed";
        public List<string> ComponentNames = new();
        public List<string> TagNames = new();

        // Initial values a content designer can set in the inspector. Only
        // applied to components the archetype actually declares; extra entries
        // are ignored. This keeps the data file self-contained — no per-type
        // setup code is needed beyond ArchetypeLoader's registry.
        public Color Color = Color.white;
        public float Scale = 1f;
        public Vector3 Position = Vector3.zero;
        public Vector3 OrbitParams = new(2f, 1f, 0f); // Radius, Speed, Phase
        public Vector2 BobParams = new(1f, 1f); // Amplitude, Speed
    }

    /// <summary>
    /// A library of data-driven archetypes. Edit this asset to add or remove
    /// entity types without modifying any C# source — the loader will build
    /// <see cref="Template"/> instances at startup from whatever is defined here.
    /// </summary>
    [CreateAssetMenu(menuName = "Trecs Samples/Archetype Library", fileName = "ArchetypeLibrary")]
    public class ArchetypeLibrary : ScriptableObject
    {
        public List<DataDrivenArchetype> Archetypes = new();
    }
}
