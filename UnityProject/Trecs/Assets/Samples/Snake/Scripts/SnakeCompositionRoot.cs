using System;
using System.Collections.Generic;
using System.Text;
using Trecs.Serialization;
using UnityEngine;

namespace Trecs.Samples.Snake
{
    /// <summary>
    /// Composition root for the Snake sample. Demonstrates Trecs input
    /// systems, bookmark serialization, and recording playback together
    /// in a small interactive game whose state is visually distinct
    /// frame-to-frame so the value of bookmarks and recordings is
    /// immediately obvious.
    ///
    /// Also draws the HUD via OnGUI so the scene file does not need to
    /// reference any Canvas/TMP_Text — the new sample is intentionally
    /// minimal so it can be loaded without TextMesh Pro asset wiring.
    /// </summary>
    public class SnakeCompositionRoot : CompositionRootBase
    {
        public SnakeSettings Settings = new();

        World _world;
        WorldAccessor _ecs;
        RecordAndPlaybackController _recordController;
        readonly StringBuilder _hudSb = new();

        public override void Construct(
            out List<Action> initializables,
            out List<Action> tickables,
            out List<Action> lateTickables,
            out List<Action> disposables
        )
        {
            _world = new WorldBuilder()
                .SetSettings(
                    new WorldSettings
                    {
                        RandomSeed = Settings.RandomSeed,
                        // Required so recordings replay deterministically: when
                        // false, native add/remove ordering can vary across
                        // runs which would corrupt checksums during playback.
                        RequireDeterministicSubmission = true,
                    }
                )
                .AddTemplate(SnakeTemplates.SnakeGlobals.Template)
                .AddTemplate(SnakeTemplates.SnakeHeadEntity.Template)
                .AddTemplate(SnakeTemplates.SnakeSegmentEntity.Template)
                .AddTemplate(SnakeTemplates.SnakeFoodEntity.Template)
                .Build();

            var goManager = new SnakeGameObjectManager(_world);
            var serialization = TrecsSerialization.Create(_world);
            _recordController = new RecordAndPlaybackController(
                serialization,
                _world,
                sampleName: "Snake"
            );
            var sceneInit = new SnakeSceneInitializer(Settings, _world, goManager);

            var inputSystem = new SnakeInputSystem();

            _world.AddSystems(
                new ISystem[]
                {
                    inputSystem,
                    new SnakeMovementSystem(Settings, goManager),
                    new FoodConsumeSystem(goManager),
                    new SegmentTrimSystem(goManager),
                    new FoodSpawnSystem(Settings, goManager),
                    new SnakeRendererSystem(goManager),
                }
            );

            initializables = new()
            {
                _world.Initialize,
                sceneInit.Initialize,
                _world.SubmitEntities,
                () => _ecs = _world.CreateAccessor("HudReader"),
            };

            tickables = new() { _recordController.Tick, inputSystem.Tick, _world.Tick };

            lateTickables = new() { _world.LateTick };

            disposables = new()
            {
                _recordController.Dispose,
                goManager.Dispose,
                _world.Dispose,
                serialization.Dispose,
            };
        }

        void OnGUI()
        {
            if (_ecs == null)
            {
                return;
            }

            int score = _ecs.GlobalComponent<Score>().Read.Value;
            int length = _ecs.GlobalComponent<SnakeLength>().Read.Value;
            int frame = _ecs.FixedFrame;

            var direction = _ecs.Query()
                .WithTags<SnakeTags.SnakeHead>()
                .Single()
                .Get<Direction>()
                .Read.Value;

            _hudSb.Clear();
            _hudSb.AppendLine("Trecs Snake — inputs, bookmarks & recordings");
            _hudSb.AppendLine();
            _hudSb.AppendLine($"Score:    {score}");
            _hudSb.AppendLine($"Length:   {length}");
            _hudSb.AppendLine($"Frame:    {frame}");
            _hudSb.AppendLine($"Heading:  {DirectionName(direction.x, direction.y)}");
            _hudSb.AppendLine($"Mode:     {_recordController.State}");
            _hudSb.AppendLine();
            _hudSb.AppendLine("Controls");
            _hudSb.AppendLine("Arrows  Move");
            _hudSb.AppendLine("F5      Start recording");
            _hudSb.AppendLine("F6      Stop recording / playback");
            _hudSb.AppendLine("F7      Play recording");
            _hudSb.AppendLine("F8      Save bookmark");
            _hudSb.AppendLine("F9      Load bookmark");

            var content = new GUIContent(_hudSb.ToString());
            var size = HudStyle.CalcSize(content);
            var rect = new Rect(12, 12, size.x + 16, size.y + 12);

            // Translucent background panel for legibility over the grid.
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prevColor;

            GUI.Label(new Rect(rect.x + 8, rect.y + 6, size.x, size.y), content, HudStyle);
        }

        static string DirectionName(int x, int y)
        {
            if (x > 0)
            {
                return "Right";
            }
            if (x < 0)
            {
                return "Left";
            }
            if (y > 0)
            {
                return "Up";
            }
            if (y < 0)
            {
                return "Down";
            }
            return "?";
        }

        GUIStyle _hudStyle;
        GUIStyle HudStyle
        {
            get
            {
                if (_hudStyle == null)
                {
                    _hudStyle = new GUIStyle(GUI.skin.label) { fontSize = 16 };
                    _hudStyle.normal.textColor = Color.white;
                }
                return _hudStyle;
            }
        }
    }
}
