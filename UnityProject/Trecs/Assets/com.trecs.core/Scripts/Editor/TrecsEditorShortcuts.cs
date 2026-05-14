using System;
using System.Collections.Generic;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// Editor shortcuts for driving the Trecs game-state controller from the
    /// keyboard, regardless of which window has focus. No default key bindings
    /// — assign via Edit > Shortcuts > Trecs.
    ///
    /// Operates on the unique <see cref="TrecsGameStateController"/> if there
    /// is exactly one. With multiple worlds, logs a warning and does nothing
    /// (the user should use the per-window controls in that case).
    /// </summary>
    static class TrecsEditorShortcuts
    {
        const string ShortcutCategory = "Trecs";

        static readonly Dictionary<World, WorldAccessor> _accessorCache = new();

        static TrecsEditorShortcuts()
        {
            TrecsEditorAccessorNames.Register("TrecsEditorShortcuts");
            WorldRegistry.WorldUnregistered += world => _accessorCache.Remove(world);
        }

        [Shortcut(ShortcutCategory + "/Toggle Fixed Pause")]
        public static void ToggleFixedPause()
        {
            if (!TryGetRunner(out var runner))
            {
                return;
            }
            runner.FixedIsPaused = !runner.FixedIsPaused;
        }

        [Shortcut(ShortcutCategory + "/Step Fixed Frame")]
        public static void StepFixedFrame()
        {
            if (!TryGetRunner(out var runner))
            {
                return;
            }
            if (!runner.FixedIsPaused)
            {
                runner.FixedIsPaused = true;
            }
            runner.StepFixedFrame();
        }

        [Shortcut(ShortcutCategory + "/Jump To Previous Anchor")]
        public static void JumpToPreviousAnchor()
        {
            if (!TryGetUniqueController(out var controller))
            {
                return;
            }
            controller.JumpToPreviousAnchor();
        }

        [Shortcut(ShortcutCategory + "/Jump To Next Anchor")]
        public static void JumpToNextAnchor()
        {
            if (!TryGetUniqueController(out var controller))
            {
                return;
            }
            controller.JumpToNextAnchor();
        }

        [Shortcut(ShortcutCategory + "/Reset Auto Recording")]
        public static void ResetAutoRecording()
        {
            if (!TryGetUniqueController(out var controller))
            {
                return;
            }
            controller.ResetAutoRecording();
        }

        static bool TryGetUniqueController(out TrecsGameStateController controller)
        {
            controller = null;
            var all = TrecsGameStateRegistry.All;
            if (all.Count == 0)
            {
                return false;
            }
            if (all.Count > 1)
            {
                Debug.LogWarning(
                    $"Trecs editor shortcut: ambiguous — {all.Count} active controllers; use the window controls instead."
                );
                return false;
            }
            controller = all[0];
            return true;
        }

        static bool TryGetRunner(out SystemRunner runner)
        {
            runner = null;
            if (!TryGetUniqueController(out var controller))
            {
                return false;
            }
            var world = controller.World;
            if (world == null || world.IsDisposed)
            {
                return false;
            }
            if (!_accessorCache.TryGetValue(world, out var accessor))
            {
                accessor = world.CreateAccessor(AccessorRole.Unrestricted, "TrecsEditorShortcuts");
                _accessorCache[world] = accessor;
            }
            try
            {
                runner = accessor.GetSystemRunner();
                return runner != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
