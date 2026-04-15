using System;
using UnityEngine;

namespace Trecs.Samples.Events
{
    /// <summary>
    /// Demonstrates the complete Trecs events/observer API.
    ///
    /// Key concepts shown:
    /// 1. OnAdded   — fires when entities are created in a group
    /// 2. OnRemoved — fires when entities are destroyed from a group
    /// 3. OnMoved   — fires when entities transition between groups (MoveTo)
    /// 4. Chaining  — OnAdded + OnRemoved on a single subscription
    /// 5. Priority  — WithPriority() controls handler execution order (lower = first)
    /// 6. Disposal  — DisposeCollection for batch cleanup of all subscriptions
    ///
    /// Note: OnAdded/OnRemoved do NOT fire for state transitions (MoveTo).
    /// Only OnMoved fires for transitions. This means cleanup logic in
    /// OnRemoved handlers won't accidentally trigger during state changes.
    /// </summary>
    public class EventTracker : IDisposable
    {
        readonly DisposeCollection _disposables = new();
        readonly WorldAccessor _accessor;
        readonly GameObjectRegistry _gameObjectRegistry;

        int _totalAddEvents;
        int _totalRemoveEvents;
        int _totalTransitions;

        public EventTracker(World world, GameObjectRegistry gameObjectRegistry)
        {
            _accessor = world.CreateAccessor();
            _gameObjectRegistry = gameObjectRegistry;

            // ─── DEMO 1: Chained OnAdded + OnRemoved ────────────────
            // InGroupsWithTags<Cube> matches ALL groups containing the
            // Cube tag, regardless of state (Growing or Shrinking).
            // A single subscription can chain multiple callback types.
            //
            // OnAdded fires on entity creation, OnRemoved fires on entity
            // destruction. Neither fires for state transitions (MoveTo).
            // Use OnMoved to handle transitions specifically.
            _accessor
                .Events.InGroupsWithTags<CubeTags.Cube>()
                .OnAdded(OnCubeEnteredGroup)
                .OnRemoved(OnCubeLeftGroup)
                .AddTo(_disposables);

            // ─── DEMO 2: OnMoved for state transitions ──────────────
            // Subscribe to the Shrinking group specifically. OnMoved fires
            // ONLY when entities arrive via MoveTo (state transition), not
            // on initial creation or destruction. This is the right callback
            // for reacting to state changes.
            _accessor
                .Events.InGroupsWithTags<CubeTags.Cube, CubeTags.Shrinking>()
                .OnMoved(OnCubeStartedShrinking)
                .AddTo(_disposables);

            // ─── DEMO 3: Priority ordering ──────────────────────────
            // Two subscriptions on the same group and event type. The
            // handler with lower priority number runs first.
            // Priority 0 colors cubes green; priority 10 logs confirmation.
            _accessor
                .Events.InGroupsWithTags<CubeTags.Cube, CubeTags.Growing>()
                .WithPriority(0)
                .OnAdded(OnGrowingAdded_HighPriority)
                .AddTo(_disposables);

            _accessor
                .Events.InGroupsWithTags<CubeTags.Cube, CubeTags.Growing>()
                .WithPriority(10)
                .OnAdded(OnGrowingAdded_LowPriority)
                .AddTo(_disposables);
        }

        // ─── Callback implementations ───────────────────────────────

        void OnCubeEnteredGroup(Group group, EntityRange indices)
        {
            int count = indices.End - indices.Start;
            _totalAddEvents += count;

            // Entity data is accessible in callbacks — entities are already
            // in their group when OnAdded fires.
            for (int i = indices.Start; i < indices.End; i++)
            {
                var entityIndex = new EntityIndex(i, group);
                var position = _accessor.Component<Position>(entityIndex).Read.Value;
                Debug.Log(
                    $"[Events] OnAdded: cube entered group at "
                        + $"({position.x:F1}, {position.y:F1}, {position.z:F1}). "
                        + $"Total add events: {_totalAddEvents}"
                );
            }
        }

        void OnCubeLeftGroup(Group group, EntityRange indices)
        {
            int count = indices.End - indices.Start;
            _totalRemoveEvents += count;
            Debug.Log(
                $"[Events] OnRemoved: {count} cube(s) left a group. "
                    + $"Total remove events: {_totalRemoveEvents}"
            );
        }

        void OnCubeStartedShrinking(Group fromGroup, Group toGroup, EntityRange indices)
        {
            int count = indices.End - indices.Start;
            _totalTransitions += count;

            // In OnMoved, entities are in toGroup. Change color to red
            // when cubes begin shrinking.
            for (int i = indices.Start; i < indices.End; i++)
            {
                var entityIndex = new EntityIndex(i, toGroup);
                var goId = _accessor.Component<GameObjectId>(entityIndex).Read;
                var go = _gameObjectRegistry.Resolve(goId);
                go.GetComponent<Renderer>().material.color = Color.red;
            }

            Debug.Log(
                $"[Events] OnMoved: {count} cube(s) transitioned to Shrinking. "
                    + $"Total transitions: {_totalTransitions}"
            );
        }

        void OnGrowingAdded_HighPriority(Group group, EntityRange indices)
        {
            // Priority 0: runs FIRST. Color growing cubes green.
            for (int i = indices.Start; i < indices.End; i++)
            {
                var entityIndex = new EntityIndex(i, group);
                var goId = _accessor.Component<GameObjectId>(entityIndex).Read;
                var go = _gameObjectRegistry.Resolve(goId);
                go.GetComponent<Renderer>().material.color = Color.green;
            }
        }

        void OnGrowingAdded_LowPriority(Group group, EntityRange indices)
        {
            // Priority 10: runs AFTER the priority-0 handler.
            // By the time this runs, cubes are already colored green.
            Debug.Log(
                "[Events] Priority demo: this handler (priority 10) "
                    + "ran after the green-coloring handler (priority 0)"
            );
        }

        /// <summary>
        /// Always dispose event subscriptions to prevent memory leaks.
        /// DisposeCollection cleans up all subscriptions in reverse order
        /// with a single Dispose() call.
        /// </summary>
        public void Dispose()
        {
            _disposables.Dispose();
        }
    }
}
