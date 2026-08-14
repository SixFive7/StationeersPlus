// Compiler-required plumbing that netstandard2.0 does not ship.
//
// The plugin half of the rig targets net472 (the game's Mono runtime), so this
// assembly must build for netstandard2.0. That target predates init-only setters
// and the nullable-analysis attributes, but the C# compiler only needs the types
// to EXIST, not to come from the BCL. Declaring them here is the standard trick
// and costs nothing at runtime.
//
// Guarded so the net10.0 build uses the real BCL types instead of duplicating them.

#if !NET10_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    /// <summary>
    ///     Required by the compiler for every <c>init</c> accessor. Every response record in
    ///     this assembly uses init-only properties so a deserialized wire payload cannot be
    ///     mutated after the fact and then re-asserted against.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    ///     Lets <c>Endpoints.TryResolve</c> tell the compiler that its out parameter is
    ///     non-null when it returns true.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }
}

#endif
