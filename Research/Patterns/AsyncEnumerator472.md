---
title: Manual async-enumerator drain on .NET Framework 4.7.2
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/LLM/LlmEngine.cs:127-129 (F0349)
related: []
tags: [threading]
---

# Manual async-enumerator drain on .NET Framework 4.7.2

Stationeers targets .NET Framework 4.7.2 (per the Unity 2021.2 toolchain). `await foreach` and `IAsyncEnumerable.ToBlockingEnumerable()` are not available. Draining an `IAsyncEnumerable<T>` requires manual use of `GetAsyncEnumerator()` + `MoveNextAsync()`.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0349 (Plans/LLM/LLM/LlmEngine.cs:127-129):

```text
            // InferAsync returns IAsyncEnumerable<string>. On .NET Framework 4.7.2
            // there is no await foreach or ToBlockingEnumerable(), so we drain it
            // manually using the async enumerator and blocking on each MoveNextAsync.
```

Libraries like LLamaSharp (used by the LLM integration mod) return `IAsyncEnumerable<T>` idiomatically. Consuming code on the game-side toolchain cannot use the newer syntax.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
var enumerator = asyncEnumerable.GetAsyncEnumerator();
try
{
    while (true)
    {
        ValueTask<bool> moveNext = enumerator.MoveNextAsync();
        bool hasNext = moveNext.IsCompleted
            ? moveNext.Result
            : moveNext.AsTask().GetAwaiter().GetResult();
        if (!hasNext) break;

        var item = enumerator.Current;
        // process item
    }
}
finally
{
    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
}
```

Rules:

- Use `.AsTask().GetAwaiter().GetResult()` to block on `ValueTask<bool>` synchronously. Blocking is unavoidable inside a non-async method body under this toolchain.
- Always call `DisposeAsync()` in a `finally` block; skipping the dispose leaks the underlying native/IO resources.
- `ValueTask<T>.IsCompleted` check before the synchronous fallback avoids an allocation when the move-next completes synchronously (common case for in-memory producers).

### Related: background-thread boundary

If the drain runs on a background worker, any Unity API interaction from `process item` must go through `./MainThreadDispatcher.md`. See also `./FileSystemWatcherMainThread.md` for the analogous event-driven case.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0349).

## Open questions

None at creation.
