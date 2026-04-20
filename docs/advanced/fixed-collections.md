# Fixed Collections

Components must be unmanaged structs, so a component field can't hold a `List<T>` or an array directly. When you need a small bounded collection inside a component — equipment slots on a character, active buffs on a unit, a small list of tracked targets — `Trecs.Collections` provides two size-specialized value types that store their elements inline.

| Type | Shape | Use when |
|---|---|---|
| [`FixedArray<N>`](#fixedarrayn) | Fixed length, every slot always live | The count is the capacity — no runtime "how many?" |
| [`FixedList<N>`](#fixedlistn) | Fixed capacity, variable `Count` | The count varies per entity up to a known bound |

Both require `T : unmanaged`. Available sizes: `2`, `4`, `8`, `16`, `32`, `64`, `128`, `256`.

## Read / write split

The indexer on both types returns `ref readonly T` — you read with `arr[i]`, but you can't write through it. Writes go through a separate `.Mut(i)` extension method that returns `ref T`:

```csharp
ref readonly var x = ref arr[i];  // readonly ref (no copy, works through `in` too)
arr.Mut(i) = 5;                    // write (requires mutable reference to arr)
ref var r = ref arr.Mut(i);        // mutable ref for in-place updates
r += 1;
```

This split makes `in FixedArray<N>` / `in FixedList<N>` parameters genuinely safe: readers can index them freely (no defensive copy, no hidden mutation), and writes fail to compile because `Mut` is a `this ref` extension that can't be called through a readonly reference.

```csharp
void Foo(in FixedArray16<int> arr)
{
    int x = arr[0];      // OK
    arr.Mut(0) = 5;      // COMPILE ERROR: arr is readonly, can't call `Mut`
}
```

## `FixedArray<N>`

A fixed-length array whose storage lives inline in its container. `default(FixedArray8<float3>)` gives you 8 zeroed slots; there's nothing to initialize.

```csharp
using Trecs.Collections;

public struct Waypoints : IEntityComponent
{
    public FixedArray8<float3> Points;
}

ref Waypoints wp = ref world.Component<Waypoints>(entityIndex).Write;
wp.Points.Mut(0) = new float3(1, 0, 0);
ref readonly float3 p = ref wp.Points[2];   // ref readonly — no element copy
ref float3 m = ref wp.Points.Mut(3);         // mutable ref
m.x += 1.0f;
```

**Members:** `Length`, readonly indexer, `==` / `!=`.
**Extension:** `Mut(i)` → `ref T`.

## `FixedList<N>`

A `FixedList<N><T>` is a `FixedArray<N><T>` plus a `Count` that tracks how many slots are live. `Capacity` is fixed at `N`; `Count` starts at `0` and grows with `Add` up to `Capacity`.

```csharp
public struct ContactPoints : IEntityComponent
{
    public FixedList16<EntityHandle> Contacts;
}

ref ContactPoints cp = ref world.Component<ContactPoints>(entityIndex).Write;
cp.Contacts.Add(otherHandle);

// Remove-during-iterate: walk backwards to avoid index skips.
for (int i = cp.Contacts.Count - 1; i >= 0; i--)
{
    if (ShouldRemove(cp.Contacts[i]))
        cp.Contacts.RemoveAtSwapBack(i);
}
```

**Members:** `Count`, `Capacity`, `IsEmpty`, readonly indexer, `==` / `!=`.
**Extensions:** `Mut(i)`, `Add`, `Clear`, `RemoveAt`, `RemoveAtSwapBack`.

| Operation | Cost | Preserves order? |
|---|---|---|
| `Add(item)` | O(1) | — |
| `Clear()` | O(1) (just resets `Count`; does not zero the buffer) | — |
| `RemoveAt(i)` | O(N) (shifts trailing elements) | Yes |
| `RemoveAtSwapBack(i)` | O(1) (overwrites slot `i` with the last element) | No |
| `Mut(i) = x` | O(1) | — |

`==` compares `Count` and the live slots only — bytes past `Count` (leftover from prior `Clear` / `RemoveAt` calls, or uninitialized) are ignored.

## Choosing a size

Type names pick the footprint. A `FixedArray256<float4x4>` is **16 KB** — per entity, whether every slot is used or not. Pick the smallest variant that covers your worst case, and round up when the worst case doesn't hit a power of two. If you'd want a `FixedList<100>`, use `FixedList128` and accept the slack.  Memory is usually pretty cheap.

## When to reach for something else

- **The upper bound varies widely across entities, or is usually far below the cap.** A `FixedArray256<T>` that's typically empty wastes storage on every entity in the group. Use a [heap pointer](heap.md) to an external `NativeList<T>` or a managed `List<T>` instead.

## Relation to Unity's `FixedList*Bytes`

Unity's `Unity.Collections` package ships `FixedList32Bytes<T>` through `FixedList4096Bytes<T>` — the same idea on a different axis. The quick differences:

| | Trecs | Unity `FixedList*Bytes` |
|---|---|---|
| Sizing axis | Element count (`FixedList16<T>` = 16 slots, always) | Total bytes (`FixedList64Bytes<T>` = `~62 / sizeof(T)` slots) |
| Indexer | Returns `ref readonly T`; writes via `.Mut(i)` | Returns `T` by value (copies per access); use `.ElementAt(i)` for `ref T` |
| `in` parameter safety | Reads safe, writes compile-error | Indexer copies the whole struct per access |
| API surface | Minimal: `Add`, `Clear`, `RemoveAt`, `RemoveAtSwapBack`, `Mut` | Extensive: `IndexOf`, `Contains`, `Sort`, `IEnumerable<T>`, cross-size equality |
| Count-less variant | `FixedArray<N>` | none |

Either works as an inline component buffer. Trecs's types read more naturally at ECS call sites — the element-count axis matches how you think about the bound, and the readonly-indexer / `Mut`-write split keeps `in` parameters safe without defensive copies. If your codebase is already using Unity's types, mixing the two is fine; they're independent.
