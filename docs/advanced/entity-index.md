
### `EntityIndex`

`EntityIndex` is a transient counterpart to `EntityHandle` that identifies an entity by its buffer position within a group. It's only valid within the current submission cycle — any structural change can invalidate it. In exchange, it skips the per-call handle-to-index lookup that `EntityHandle` does internally. Every entity-targeted method on `EntityHandle` has a matching overload on `EntityIndex`:

```csharp
EntityIndex idx = handle.ToIndex(World);
idx.SetTag<BallTags.Resting>(World);
idx.Component<Position>(World).Write = newPos;
```

