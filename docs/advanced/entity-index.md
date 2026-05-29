
# EntityIndex

`EntityIndex` is a transient counterpart to `EntityHandle` that identifies an entity by its buffer position within a group. It is only valid until the next entity submission — any add, remove, or tag change that touches the entity's group can shift buffer positions, invalidating the index.

In exchange, it skips the handle-to-index resolution that `EntityHandle` does internally. **That resolution is nearly free** — it's a single index into a flat array (the handle's id selects the entry directly), not a hash-map lookup or a pointer chase. So `EntityIndex` saves almost nothing while exposing you to a real invalidation hazard.

**Prefer `EntityHandle` in almost all cases.** Reach for `EntityIndex` only when profiling points at that resolution inside a tight inner loop — where you've already resolved a handle and perform several operations on the same entity within a single submission.

Every entity-targeted method on `EntityHandle` has a matching overload on `EntityIndex`:

```csharp
// Inside a system, `World` is the system's WorldAccessor.
EntityIndex idx = handle.ToIndex(World);
idx.SetTag<BallTags.Resting>(World);
idx.Component<Position>(World).Write = newPos;
```

The methods accept either a `WorldAccessor` (main thread) or a `NativeWorldAccessor` (jobs).

To convert back to a stable handle when you need to cross a submission boundary:

```csharp
EntityHandle h = idx.ToHandle(World);
```
