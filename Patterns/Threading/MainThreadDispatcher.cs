using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace StationeersPlus.Shared
{
    /// <summary>
    ///     Shared main-thread marshaling helper for StationeersPlus mods. Linked into each mod via
    ///     <c>&lt;Compile Include="..\..\..\Patterns\Threading\MainThreadDispatcher.cs" Link="..." /&gt;</c>,
    ///     so every mod compiles its OWN copy with its OWN static queue and instance. The static state is
    ///     per-assembly, not shared across mods (see Patterns/Threading/README.md).
    ///
    ///     Why this exists: Stationeers drives the power tick (PowerTick.ApplyState -> ReceivePower -> ...)
    ///     on a UniTask ThreadPool worker, and Harmony postfixes on those methods inherit that worker
    ///     thread. Any Unity API call (GameObject, Transform, LineRenderer, and the UI rebuilds a
    ///     device-list refresh triggers) from a non-main thread hard-crashes the native player. Enqueue a
    ///     closure from any thread; it runs on the main thread one frame later, drained in Update().
    ///
    ///     Why not the game's own UnityMainThreadDispatcher: its Instance() throws when the
    ///     MainThreadExecutor manager is absent from the scene, and its Enqueue coroutine-wraps actions
    ///     and silently drops target-less delegates. See Research/Patterns/MainThreadDispatcher.md.
    /// </summary>
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        private static MainThreadDispatcher _instance;
        private static Action<string> _onError;

        /// <summary>
        ///     Creates the dispatcher GameObject. Call once from the plugin's Awake. Idempotent.
        /// </summary>
        /// <param name="gameObjectName">Unique per mod, e.g. "PowerGridPlus_MainThreadDispatcher".</param>
        /// <param name="onError">Optional sink for exceptions thrown by queued actions (e.g. plugin log).</param>
        public static void Init(string gameObjectName, Action<string> onError = null)
        {
            if (_instance != null) return;
            _onError = onError;
            var go = new GameObject(gameObjectName);
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        /// <summary>
        ///     Queues a closure to run on the main thread next frame. No-op for a null action or before
        ///     <see cref="Init"/> has run.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null || _instance == null) return;
            Queue.Enqueue(action);
        }

        private void Update()
        {
            while (Queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    _onError?.Invoke($"MainThreadDispatcher action threw: {e}");
                }
            }
        }
    }
}
