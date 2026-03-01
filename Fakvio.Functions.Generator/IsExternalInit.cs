// Required for record and init-only property support in netstandard2.0.
// The C# compiler emits references to this type for init-only property setters.
// Since netstandard2.0 doesn't include it, we define it ourselves.

#if NETSTANDARD2_0

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

#endif
