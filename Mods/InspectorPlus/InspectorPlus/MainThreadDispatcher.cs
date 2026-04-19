using System;
using System.Collections.Generic;
using UnityEngine;

namespace InspectorPlus
{
    /// <summary>
    /// Queues callbacks from background threads and runs them on Unity's main thread.
    /// FileSystemWatcher events fire on a threadpool thread; Unity API calls must
    /// happen on the main thread.
    /// </summary>
    internal class MainThreadDispatcher : MonoBehaviour
    {
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static MainThreadDispatcher _instance;

        internal static void Initialize()
        {
            if (_instance != null) return;
            var go = new GameObject("InspectorPlus_MainThreadDispatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }

        void Update()
        {
            lock (_queue)
            {
                while (_queue.Count > 0)
                {
                    var action = _queue.Dequeue();
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        InspectorPlusPlugin.Log.LogError($"MainThreadDispatcher error: {ex}");
                    }
                }
            }
        }
    }
}
