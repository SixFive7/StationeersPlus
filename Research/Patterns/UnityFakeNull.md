---
title: Unity fake-null
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/InspectorPlus/RESEARCH.md:25-29 (F0004)
related:
  - ./FileSystemWatcherMainThread.md
tags: [unity]
---

# Unity fake-null

`obj == null` returns `true` on a destroyed `UnityEngine.Object` even when the managed C# wrapper is still alive. Reflection walks that dereference fields on such wrappers crash with `MissingReferenceException`. Required reading for any code that walks arbitrary object graphs (serializers, diagnostic dumpers).

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Unity overrides `==` on `UnityEngine.Object` to compare against "native-object-destroyed" as well as C# null. A destroyed `GameObject` / `Component` / `Transform` returns `obj == null` as `true` even though the managed wrapper is a live reference. Code that null-checks with `== null` is safe. Code that dereferences after passing `is object` or after a `referenceEquals(obj, null) == false` check is not.

The trap bites reflection walkers and serializers because their generic "recurse into non-null fields" logic uses reference-null checks, not Unity's override.

F0004 (Mods/InspectorPlus/RESEARCH.md:25-29):

> Reflection against Unity fake-null is a known trap (`obj == null` returns `true` even when the managed wrapper is still alive). The walker checks `UnityEngine.Object`-derived values via `!obj` before dereferencing.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Before dereferencing any `UnityEngine.Object`-derived value that reflection surfaces, gate on Unity's own equality. Two equivalent forms:

```csharp
if (!value)  // invokes Unity's implicit bool conversion via operator==
    return;  // destroyed or null

// or explicitly:
if (value == null)
    return;
```

A reference-null check (`object.ReferenceEquals(value, null)`) is NOT equivalent and will let through destroyed wrappers.

When the field type is not statically known (a generic `object` from reflection), test the runtime type first:

```csharp
if (value is UnityEngine.Object unityObj && !unityObj)
    return;  // destroyed Unity object
```

Apply this rule to every recursion frame in a walker, not just the entry point. A destroyed child can hang off a live parent.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; content lifted verbatim from F0004.

## Open questions

None at creation.
