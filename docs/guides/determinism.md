# Determinism

Trecs is designed around deterministic simulation but does not require it. Both determinism and serialization are opt-in — you can use `UnityEngine.Random`, `Time.deltaTime`, and store mutable state in system fields, and the simulation will run fine.

However, you should understand what you're giving up before you opt out.

## What determinism enables

- **Record / replay / scrub** — the editor's rewind buffer and recording sessions rely on deterministic re-simulation. Non-deterministic systems make replays drift from the original run.
- **Snapshots** — save/load via the snapshot system requires that all mutable state lives in serializable components. State stored in system fields is invisible to snapshots and silently lost on restore.
- **Headless testing** — deterministic simulations are straightforward to test headlessly (feed inputs, assert outcomes). Non-deterministic ones require tolerance ranges or mocking.

## Built-in analyzers

The source analyzers TRECS128–130 flag common non-deterministic patterns inside fixed-update systems:

| Code | What it flags |
|------|--------------|
| TRECS128 | `Dictionary<K,V>` / `HashSet<T>` iteration (non-deterministic order) |
| TRECS129 | `NativeHashMap` / `NativeHashSet` iteration (non-deterministic order) |
| TRECS130 | `UnityEngine.Random`, `System.Random`, `Time.deltaTime`, `DateTime.Now` in a fixed-update system |

Since fixed update is the default phase, most systems will trigger these if they use the flagged APIs. You can suppress per-method or project-wide with `#pragma warning disable TRECS130`, but consider whether the trade-off is worth it — re-enabling determinism later in a project is substantially harder than starting with it.

## Presentation-phase escape hatch

If you only need non-deterministic behavior in presentation code (rendering, audio, UI), put that logic in `[ExecuteIn(SystemPhase.Presentation)]` or `[ExecuteIn(SystemPhase.EarlyPresentation)]` systems. The analyzers don't fire outside of Fixed, and the separation keeps the simulation itself clean.

## See also

- [Time & RNG](../advanced/time-and-rng.md) — deterministic alternatives to `UnityEngine.Random` and `Time.deltaTime`.
- [Gotchas](gotchas.md#unityenginerandom-or-timedeltatime-in-fixed-update) — the specific pitfall with `Random` / `deltaTime` in fixed update.
- [Best Practices](best-practices.md) — general guidelines including determinism rules of thumb.
