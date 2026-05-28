
# EntityIndex

`EntityIndex` is a transient counterpart to `EntityHandle` that identifies an entity by its buffer position within a group. It is only valid until the next entity submission — any add, remove, or tag change that touches the entity's group can shift buffer positions, invalidating the index. In exchange, it skips the per-call handle-to-index lookup that `EntityHandle` performs internally.

Use `EntityIndex` in hot loops where a handle has already been resolved and you want to perform multiple operations on the same entity without paying the lookup each time.

Every entity-targeted method on `EntityHandle` has a matching overload on `EntityIndex`:

```csharp
// Inside a system, `World` is the system's WorldAccessor.
EntityIndex idx = handle.ToIndex(World);
idx.SetTag<BallTags.Resting>(World);
idx.Component<Position>(World).Write = newPos;
```

The methods accept either a `WorldAccessor` (main thread) or a `NativeWorldAccessor` (jobs). `EntityHandle.ToIndex` also has a `World` (the Trecs `World` class) overload for non-system code.

To convert back to a stable handle when you need to cross a submission boundary:

```csharp
EntityHandle h = idx.ToHandle(World);
```
