using System;
using Assets.Scripts.Util;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     Tiny main-thread pump wrapper around the game's <c>UnityMainThreadDispatcher</c> (the same
    ///     mechanism vanilla <c>Cable.Break</c> / <c>CableFuse.Break</c> use to self-marshal, and the
    ///     one <see cref="VoltageTierEnforcer"/> already uses for burns). Headless-safe: when the
    ///     dispatcher does not exist yet, <see cref="TryEnqueue"/> reports failure and the caller
    ///     decides how to degrade.
    /// </summary>
    internal static class MainThread
    {
        internal static bool TryEnqueue(Action action)
        {
            if (action == null) return false;
            try
            {
                if (!UnityMainThreadDispatcher.Exists()) return false;
                UnityMainThreadDispatcher.Instance().Enqueue(action);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
