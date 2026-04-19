using System;
using System.Reflection;

namespace EquipmentPlus
{
    /// <summary>
    /// Small helper for accessing private fields on game objects.
    /// Used to reach ConfigCartridge's internal _displayTextMesh field.
    /// </summary>
    internal static class ReflectionUtils
    {
        private const BindingFlags AllFlags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;

        internal static bool TryGetField<T>(object target, string fieldName, out T value)
        {
            value = default;
            if (target == null || string.IsNullOrEmpty(fieldName))
                return false;

            var fi = target.GetType().GetField(fieldName, AllFlags);
            if (fi == null)
                return false;

            try
            {
                var raw = fi.GetValue(target);
                if (raw is T typed)
                {
                    value = typed;
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
