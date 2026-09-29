using System;

namespace ZhaDai.Patcher
{
    /// <summary>
    /// Thrown when an injection anchor cannot be located or the result does not match
    /// the expected layout. The message always names the exact anchor that failed so
    /// the operator can act without re-reading the IL.
    /// </summary>
    public sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }

        public PatchException(string message, Exception inner) : base(message, inner) { }
    }
}
