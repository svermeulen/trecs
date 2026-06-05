using System;
using System.IO;
using UnityEditor;

namespace Trecs.Internal
{
    /// <summary>
    /// Shared user-facing action flows over the recording / snapshot
    /// libraries — guard → prompt → confirm → execute → status — consumed by
    /// both <see cref="TrecsPlayerWindow"/> (Actions ▾ menu) and
    /// <see cref="TrecsSavesWindow"/> (per-row actions). The two windows
    /// previously copy-pasted these dances and kept them in sync by
    /// convention (most fragile case: the load-snapshot confirm's
    /// "don't ask again" EditorPrefs key, declared as identical string
    /// literals in both files under different constant names). This is now
    /// the single home for the flow logic; the windows keep their own
    /// status surfaces and view refreshes, driven by the returned
    /// <see cref="Result"/>.
    /// </summary>
    internal static class TrecsRecordingActions
    {
        // Once the user opts out via the load-snapshot confirm dialog's
        // "Don't ask again" button, suppress the load-during-recording
        // prompt forever. Stored in EditorPrefs so the choice persists
        // across editor sessions — and shared by every surface that loads
        // snapshots, so a dismissal in one window silences them all.
        const string SuppressLoadSnapshotConfirmKey = "Trecs.Snapshots.SuppressLoadConfirm";

        /// <summary>
        /// Outcome of a flow. <see cref="Acted"/> is false when the user
        /// cancelled at a prompt/confirm — nothing happened and there's
        /// nothing to report. Otherwise <see cref="Status"/> carries the
        /// user-facing message and <see cref="Succeeded"/> tells the caller
        /// whether to refresh its view.
        /// </summary>
        public readonly struct Result
        {
            public readonly bool Acted;
            public readonly bool Succeeded;
            public readonly string Status;

            Result(bool acted, bool succeeded, string status)
            {
                Acted = acted;
                Succeeded = succeeded;
                Status = status;
            }

            public static readonly Result Cancelled = default;

            public static Result Ok(string status) => new(true, true, status);

            public static Result Fail(string status) => new(true, false, status);
        }

        public static string SuggestRecordingName() => $"recording-{DateTime.Now:yyyyMMdd-HHmmss}";

        public static string SuggestSnapshotName() => $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}";

        /// <summary>
        /// Load a named <c>.snap</c>, warning first when an active
        /// recording's in-memory buffer would be discarded (the load stops
        /// the recorder and restarts a fresh buffer from the snapshot's
        /// frame — see <see cref="TrecsSnapshotLibrary.LoadSnapshot"/>).
        /// </summary>
        public static Result LoadSnapshot(
            TrecsRecordingSession controller,
            TrecsSnapshotLibrary library,
            string name
        )
        {
            if (controller == null || library == null)
            {
                return Result.Fail("No active world to load into.");
            }
            if (
                controller.AutoRecorder.IsRecording
                && !EditorPrefs.GetBool(SuppressLoadSnapshotConfirmKey, false)
            )
            {
                // DisplayDialogComplex returns 0=ok, 1=cancel, 2=alt.
                var choice = EditorUtility.DisplayDialogComplex(
                    "Load snapshot?",
                    "Loading a snapshot will discard the current in-memory "
                        + "recording buffer (saved files on disk are not "
                        + "affected) and start a fresh recording from the "
                        + "snapshot's frame.",
                    "Load",
                    "Cancel",
                    "Load and don't ask again"
                );
                if (choice == 1)
                {
                    return Result.Cancelled;
                }
                if (choice == 2)
                {
                    EditorPrefs.SetBool(SuppressLoadSnapshotConfirmKey, true);
                }
            }
            try
            {
                return library.LoadSnapshot(name)
                    ? Result.Ok($"Loaded snapshot '{name}'.")
                    : Result.Fail($"Failed to load snapshot '{name}'.");
            }
            catch (Exception e)
            {
                return Result.Fail($"Load snapshot failed: {e.Message}");
            }
        }

        /// <summary>
        /// Prompt for a name (confirming overwrite of an existing file) and
        /// save the world's current frame as a <c>.snap</c>.
        /// </summary>
        public static Result SaveSnapshotPrompted(TrecsSnapshotLibrary library, EditorWindow anchor)
        {
            if (library == null)
            {
                return Result.Fail("No active world.");
            }
            var name = TrecsTextPromptWindow.Prompt(
                "Save snapshot",
                "Snapshot name:",
                SuggestSnapshotName(),
                anchor
            );
            if (string.IsNullOrWhiteSpace(name))
            {
                return Result.Cancelled;
            }
            name = name.Trim();
            if (File.Exists(TrecsSnapshotLibrary.GetSnapshotPath(name)))
            {
                if (
                    !EditorUtility.DisplayDialog(
                        "Overwrite snapshot?",
                        $"A snapshot named '{name}' already exists. Overwrite?",
                        "Overwrite",
                        "Cancel"
                    )
                )
                {
                    return Result.Cancelled;
                }
            }
            try
            {
                return library.SaveSnapshot(name)
                    ? Result.Ok($"Saved snapshot '{name}'.")
                    : Result.Fail($"Failed to save snapshot '{name}'.");
            }
            catch (Exception e)
            {
                return Result.Fail($"Save snapshot failed: {e.Message}");
            }
        }

        /// <summary>
        /// Prompt for a name (confirming overwrite of an existing file) and
        /// save the in-memory recording buffer to it.
        /// </summary>
        public static Result SaveRecordingPrompted(
            TrecsRecordingSession controller,
            EditorWindow anchor,
            string suggestedName
        )
        {
            if (controller == null)
            {
                return Result.Fail("No active world.");
            }
            var name = TrecsTextPromptWindow.Prompt(
                "Save recording as",
                "Save as:",
                suggestedName,
                anchor
            );
            if (string.IsNullOrWhiteSpace(name))
            {
                return Result.Cancelled;
            }
            name = name.Trim();
            if (File.Exists(TrecsRecordingSession.GetRecordingPath(name)))
            {
                if (
                    !EditorUtility.DisplayDialog(
                        "Overwrite recording?",
                        $"A recording named '{name}' already exists. Overwrite it?",
                        "Overwrite",
                        "Cancel"
                    )
                )
                {
                    return Result.Cancelled;
                }
            }
            return SaveRecording(controller, name);
        }

        /// <summary>
        /// Save the in-memory recording buffer to an already-chosen name —
        /// no prompts; callers confirm overwrite themselves when needed.
        /// </summary>
        public static Result SaveRecording(TrecsRecordingSession controller, string name)
        {
            if (controller == null)
            {
                return Result.Fail("No active world.");
            }
            try
            {
                return controller.SaveNamedRecording(name)
                    ? Result.Ok($"Saved '{name}'.")
                    : Result.Fail("Save failed (no keyframes to save?).");
            }
            catch (Exception e)
            {
                return Result.Fail($"Save failed: {e.Message}");
            }
        }

        public static Result LoadRecording(TrecsRecordingSession controller, string name)
        {
            if (controller == null)
            {
                return Result.Fail("No active world to load into.");
            }
            try
            {
                return controller.LoadNamedRecording(name)
                    ? Result.Ok($"Loaded recording '{name}'.")
                    : Result.Fail($"Failed to load recording '{name}'.");
            }
            catch (Exception e)
            {
                return Result.Fail($"Load failed: {e.Message}");
            }
        }

        /// <summary>
        /// Confirm and delete a saved recording file. Routes through the
        /// controller when one is available (it clears the recorder's
        /// backing path when deleting the matching file); falls back to a
        /// raw file delete when no world is active.
        /// </summary>
        public static Result DeleteRecording(TrecsRecordingSession controller, string name)
        {
            if (
                !EditorUtility.DisplayDialog(
                    "Delete recording?",
                    $"Delete saved recording '{name}'? This removes the file from disk; "
                        + "the in-memory buffer is unchanged.",
                    "Delete",
                    "Cancel"
                )
            )
            {
                return Result.Cancelled;
            }
            try
            {
                var ok =
                    controller != null
                        ? controller.DeleteNamedRecording(name)
                        : DeleteSavedFile(TrecsRecordingSession.GetRecordingPath(name));
                return ok
                    ? Result.Ok($"Deleted '{name}' from disk.")
                    : Result.Fail($"Delete failed for '{name}'.");
            }
            catch (Exception e)
            {
                return Result.Fail($"Delete failed: {e.Message}");
            }
        }

        /// <summary>
        /// Delete a saved recording/snapshot file directly, bypassing the
        /// controller/library (no world required). Notifies the saves-changed
        /// event so other windows refresh their lists.
        /// </summary>
        public static bool DeleteSavedFile(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            TrecsRecordingSession.NotifySavesChanged();
            return true;
        }
    }
}
