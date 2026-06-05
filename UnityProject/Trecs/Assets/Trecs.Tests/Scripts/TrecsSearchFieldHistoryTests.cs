using NUnit.Framework;
using UnityEditor;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Covers the search-field history extracted from TrecsHierarchyWindow:
    // record dedupe rules (head no-op, prefix-replace, promote), the cap,
    // Up/Down recall with draft restore, recall-driven value changes not
    // re-recording, and EditorPrefs persistence. Uses a test-only prefs
    // key, deleted around each test so runs can't pollute the user's real
    // hierarchy-window history (or each other).
    [TestFixture]
    public class TrecsSearchFieldHistoryTests
    {
        const string PrefsKey = "Trecs.Tests.SearchFieldHistory";

        [SetUp]
        public void SetUp() => EditorPrefs.DeleteKey(PrefsKey);

        [TearDown]
        public void TearDown() => EditorPrefs.DeleteKey(PrefsKey);

        static TrecsSearchFieldHistory NewHistory(int max = 20) => new(PrefsKey, max);

        // Recalls one step and returns what the history applied (null when
        // navigation didn't move). Mimics the window's applyText callback,
        // including the re-entrant NotifyQueryChanged the search field's
        // value-changed event fires.
        static string Recall(TrecsSearchFieldHistory h, int delta, string currentText)
        {
            string applied = null;
            h.Recall(
                delta,
                currentText,
                t =>
                {
                    applied = t;
                    h.NotifyQueryChanged(t);
                }
            );
            return applied;
        }

        [Test]
        public void EmptyQuery_NotRecorded()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("");
            h.NotifyQueryChanged(null);
            NAssert.That(h.Count, Is.EqualTo(0));
        }

        [Test]
        public void IdenticalHead_NotDuplicated()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("player");
            h.NotifyQueryChanged("player");
            NAssert.That(h.Count, Is.EqualTo(1));
        }

        [Test]
        public void IncrementalTyping_ReplacesHeadViaPrefixRule()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("tag:e");
            h.NotifyQueryChanged("tag:en");
            h.NotifyQueryChanged("tag:enemy");
            NAssert.That(h.Count, Is.EqualTo(1));
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("tag:enemy"));
        }

        [Test]
        public void ExistingEntry_PromotedNotDuplicated()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("one");
            h.NotifyQueryChanged("two");
            h.NotifyQueryChanged("one");
            NAssert.That(h.Count, Is.EqualTo(2));
            // "one" was promoted back to most-recent.
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("one"));
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("two"));
        }

        [Test]
        public void Cap_DropsOldestEntries()
        {
            var h = NewHistory(max: 3);
            h.NotifyQueryChanged("one");
            h.NotifyQueryChanged("two");
            h.NotifyQueryChanged("three");
            h.NotifyQueryChanged("four");
            NAssert.That(h.Count, Is.EqualTo(3));
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("four"));
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("three"));
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("two"));
            // Past oldest: clamped, no movement.
            NAssert.That(Recall(h, +1, ""), Is.Null);
        }

        [Test]
        public void Recall_UpThenDownRestoresDraft()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("older");
            h.NotifyQueryChanged("newer");

            NAssert.That(Recall(h, +1, "my draft"), Is.EqualTo("newer"));
            NAssert.That(Recall(h, +1, "newer"), Is.EqualTo("older"));
            NAssert.That(Recall(h, -1, "older"), Is.EqualTo("newer"));
            // Forward past the newest entry restores the original draft.
            NAssert.That(Recall(h, -1, "newer"), Is.EqualTo("my draft"));
        }

        [Test]
        public void Recall_DownWithNoNavInProgress_IsNoOp()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("entry");
            NAssert.That(Recall(h, -1, "typing"), Is.Null);
        }

        [Test]
        public void Recall_EmptyHistory_IsNoOp()
        {
            var h = NewHistory();
            NAssert.That(Recall(h, +1, "typing"), Is.Null);
        }

        [Test]
        public void RecallDrivenValueChange_DoesNotRecordOrExitNav()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("one");
            h.NotifyQueryChanged("two");

            // The Recall helper's applyText re-enters NotifyQueryChanged
            // (like the real search field's value-changed event). The
            // recalled entry must not be re-recorded/promoted...
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("two"));
            NAssert.That(h.Count, Is.EqualTo(2));
            // ...and the navigation cursor survives, so the next Up
            // continues deeper instead of restarting at the newest entry.
            NAssert.That(Recall(h, +1, "two"), Is.EqualTo("one"));
        }

        [Test]
        public void ManualEditDuringNav_ExitsNavAndRecords()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("one");
            h.NotifyQueryChanged("two");
            NAssert.That(Recall(h, +1, ""), Is.EqualTo("two"));

            // User types something new mid-navigation.
            h.NotifyQueryChanged("fresh");
            // Nav exited: the next Up starts from the newest entry again.
            NAssert.That(Recall(h, +1, "fresh"), Is.EqualTo("fresh"));
        }

        [Test]
        public void Persistence_RoundTripsThroughEditorPrefs()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("one");
            h.NotifyQueryChanged("two");

            var reloaded = NewHistory();
            NAssert.That(reloaded.Count, Is.EqualTo(2));
            NAssert.That(Recall(reloaded, +1, ""), Is.EqualTo("two"));
            NAssert.That(Recall(reloaded, +1, "two"), Is.EqualTo("one"));
        }

        [Test]
        public void Clear_EmptiesAndPersists()
        {
            var h = NewHistory();
            h.NotifyQueryChanged("one");
            h.Clear();
            NAssert.That(h.Count, Is.EqualTo(0));
            NAssert.That(NewHistory().Count, Is.EqualTo(0));
        }
    }
}
