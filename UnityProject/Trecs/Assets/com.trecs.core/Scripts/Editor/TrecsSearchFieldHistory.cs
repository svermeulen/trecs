using System;
using System.Collections.Generic;
using UnityEditor;

namespace Trecs
{
    /// <summary>
    /// Most-recent-first list of distinct search queries the user has
    /// typed during this and prior sessions, with Up/Down-arrow recall.
    /// Persisted via EditorPrefs as a newline-joined string (JsonUtility
    /// doesn't serialize List&lt;string&gt; at the top level). Extracted
    /// from <see cref="TrecsHierarchyWindow"/>; reusable by any window
    /// with a search field (pass a window-specific prefs key).
    ///
    /// Wiring contract: route every search-field value change through
    /// <see cref="NotifyQueryChanged"/>, and drive arrow keys through
    /// <see cref="Recall"/> — its <c>applyText</c> callback sets the field
    /// value, and the re-entrant value-changed event that fires is
    /// recognized as programmatic (not recorded, recall position kept).
    /// </summary>
    internal sealed class TrecsSearchFieldHistory
    {
        readonly List<string> _entries = new();
        readonly string _prefsKey;
        readonly int _maxEntries;

        // -1 means the user isn't navigating history. >= 0 indexes
        // into _entries (0 = most recent).
        int _index = -1;

        // What the user was typing before they hit Up the first time.
        // Restored when they Down-arrow back past the newest entry.
        string _draft = string.Empty;

        // Suppresses record-on-value-change while Recall is applying a
        // history entry programmatically.
        bool _suppressRecord;

        public TrecsSearchFieldHistory(string prefsKey, int maxEntries = 20)
        {
            _prefsKey = prefsKey;
            _maxEntries = maxEntries;
            Load();
        }

        public int Count => _entries.Count;

        public void Clear()
        {
            _entries.Clear();
            _index = -1;
            Save();
        }

        /// <summary>
        /// Call from the search field's value-changed handler with the new
        /// query. A manual edit (vs. an in-flight <see cref="Recall"/>)
        /// exits history navigation and records the query.
        /// </summary>
        public void NotifyQueryChanged(string query)
        {
            if (_suppressRecord)
            {
                return;
            }
            _index = -1;
            Record(query);
        }

        /// <summary>
        /// Step through history: positive delta = older (Up), negative =
        /// newer (Down). Stepping forward past the newest entry restores
        /// the draft captured when navigation began. <paramref name="currentText"/>
        /// is the field's current value (the draft candidate);
        /// <paramref name="applyText"/> writes the recalled query back to
        /// the field, with record suppression active for its duration.
        /// </summary>
        public void Recall(int delta, string currentText, Action<string> applyText)
        {
            if (_entries.Count == 0)
            {
                return;
            }
            int newIndex;
            if (_index < 0)
            {
                if (delta < 0)
                {
                    return; // Down with no nav in progress: no-op.
                }
                _draft = currentText ?? string.Empty;
                newIndex = 0;
            }
            else
            {
                newIndex = _index + delta;
            }
            if (newIndex >= _entries.Count)
            {
                return; // Past oldest — clamp.
            }
            _suppressRecord = true;
            try
            {
                if (newIndex < 0)
                {
                    // Stepped forward past the newest entry — restore the
                    // user's draft and exit nav.
                    _index = -1;
                    applyText(_draft);
                }
                else
                {
                    _index = newIndex;
                    applyText(_entries[newIndex]);
                }
            }
            finally
            {
                _suppressRecord = false;
            }
        }

        // Adds the query at the front of the history list. Dedupe rules:
        //   - Empty queries skipped.
        //   - Identical to the current head → no-op (avoids spamming on
        //     refocus).
        //   - Current head is a strict prefix of the new query → replace
        //     the head (so typing "tag:e" → "tag:en" → "tag:enemy" leaves
        //     a single "tag:enemy" entry, not three partial ones).
        //   - An identical entry deeper in the list is promoted to the
        //     front rather than duplicated.
        //   - Otherwise prepend.
        // Capped at _maxEntries.
        void Record(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return;
            }
            if (_entries.Count > 0)
            {
                var head = _entries[0];
                if (head == query)
                {
                    return;
                }
                if (query.StartsWith(head, StringComparison.Ordinal))
                {
                    _entries[0] = query;
                    Save();
                    return;
                }
                // Promote an existing identical entry to the front rather
                // than duplicating it (lets the user revisit a query
                // without churning recent history).
                int existing = _entries.IndexOf(query);
                if (existing > 0)
                {
                    _entries.RemoveAt(existing);
                    _entries.Insert(0, query);
                    Save();
                    return;
                }
            }
            _entries.Insert(0, query);
            if (_entries.Count > _maxEntries)
            {
                _entries.RemoveRange(_maxEntries, _entries.Count - _maxEntries);
            }
            Save();
        }

        void Load()
        {
            _entries.Clear();
            var raw = EditorPrefs.GetString(_prefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }
            foreach (var line in raw.Split('\n'))
            {
                if (!string.IsNullOrEmpty(line))
                {
                    _entries.Add(line);
                }
            }
        }

        void Save()
        {
            EditorPrefs.SetString(_prefsKey, string.Join("\n", _entries));
        }
    }
}
