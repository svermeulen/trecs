// Companion docs: https://svermeulen.github.io/trecs/samples/05-job-system/

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Trecs.Samples.JobSystem
{
    public class JobSystemCompositionRoot : CompositionRootBase
    {
        public int MinParticleCount = 100;
        public int MaxParticleCount = 100_000;
        public int MaxParticleChangePerFrame = 200;
        public float AreaSize = 20f;
        public float MaxSpeed = 5f;
        public float ParticleSize = 0.15f;
        public Color ParticleColor = new(0.2f, 0.8f, 1f);
        public TMP_Text StatusText;

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            var renderer = new RendererSystem();

            renderer.RegisterRenderable(
                TagSet<SampleTags.Particle>.Value,
                SampleUtil.CreateIcosphereMesh(),
                SampleUtil.CreateUnlitIndirectMaterial(ParticleColor),
                MaxParticleCount
            );

            var world = new WorldBuilder()
                .AddEntityTypes(
                    new[]
                    {
                        SampleTemplates.Globals.Template,
                        SampleTemplates.ParticleEntity.Template,
                    }
                )
                .Build();

            world.AddSystems(
                new ISystem[]
                {
                    new InputSystem(MinParticleCount, MaxParticleCount),
                    new ParticleSpawnerSystem(
                        AreaSize,
                        MaxSpeed,
                        ParticleSize,
                        ParticleColor,
                        MaxParticleChangePerFrame
                    ),
                    new ParticleMoveSystem(),
                    new ParticleBoundSystem(AreaSize),
                    new TextDisplaySystem(StatusText),
                    renderer,
                }
            );

            initializables = new() { world.Initialize };
            tickables = new() { world.Tick };
            lateTickables = new() { world.LateTick };
            disposables = new() { renderer.Dispose, world.Dispose };
        }
    }
}
