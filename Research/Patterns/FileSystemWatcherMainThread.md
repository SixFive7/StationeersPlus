---
title: FileSystemWatcher main-thread boundary
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/InspectorPlus/RESEARCH.md:25-29 (F0004, primary)
  - Mods/InspectorPlus/InspectorPlus/Plugin.cs:78-81 (F0380)
related:
  - ./MainThreadDispatcher.md
  - ./UnityFakeNull.md
tags: [threading, unity]
---

# FileSystemWatcher main-thread boundary

`System.IO.FileSystemWatcher` events fire on a .NET thread-pool thread. Any Unity API call from the callback crashes the native player. File-share races let the event fire while the writer still holds the file open. Both bite anything using `FileSystemWatcher` inside a Unity plugin.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0004 (Mods/InspectorPlus/RESEARCH.md:25-29, primary):

> - The `FileSystemWatcher` fires on a thread-pool thread, not the Unity main thread. Any Unity API call from the watcher callback crashes. `MainThreadDispatcher` exists for exactly this reason.
> - `FileSystemWatcher.Created` can fire while the writer still holds the file open. The plugin opens the request file with `FileShare.ReadWrite` and retries for a short window on `IOException`.

F0380 (Mods/InspectorPlus/InspectorPlus/Plugin.cs:78-81, corroborating):

> FileSystemWatcher fires on a background thread. Queue the work onto Unity's main thread so we can safely access GameObjects and Components.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two separate concerns; address both.

### Thread boundary

Route the callback through `MainThreadDispatcher` (see `./MainThreadDispatcher.md`) before touching any Unity API (`GameObject.Find`, transform lookups, component access). The dispatcher's `ConcurrentQueue<Action>` is safe to enqueue from the FSW thread; the dequeue and Unity call happen on the main thread one frame later.

### File-share race

`FileSystemWatcher.Created` fires as soon as the OS reports the file exists, which can be before the writer has flushed and closed the handle. Open with `FileShare.ReadWrite` so the writer's still-open handle doesn't block the read, and retry on `IOException` for a short window. Pattern:

```csharp
for (int attempt = 0; attempt < maxAttempts; attempt++)
{
    try
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // read
        return;
    }
    catch (IOException)
    {
        if (attempt == maxAttempts - 1) throw;
        Thread.Sleep(backoffMs);
    }
}
```

Apply the same logic to `Changed` events if the writer truncates-then-writes in two steps.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0004: primary source covering both thread boundary and file-share race, both pitfalls in one finding from InspectorPlus.
- F0380: plugin code comment confirming the thread-boundary rule with a brief statement of the fix (enqueue onto Unity's main thread).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0004 primary source provides both the thread and file-share findings; F0380 confirms.

## Open questions

None at creation.
