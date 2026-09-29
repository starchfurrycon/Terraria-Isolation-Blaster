using System;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// Test-only stand-in for the real runtime plugin.
    ///
    /// It exists so `verify` and `patch-copy` can be exercised end to end before
    /// `src/ZhaDai.Runtime` is written. It must stay signature-compatible with the
    /// documented contract in ../../README.md and must not be shipped as the real payload.
    /// </summary>
    public static class Hooks
    {
        /// <summary>Bumped from a hook so offline tests can prove the call ran.</summary>
        public static int CallCount;

        /// <summary>Records the name of the most recent hook invocation.</summary>
        public static string LastHook;

        public static void BeforePlayerUpdate()
        {
            CallCount++;
            LastHook = "BeforePlayerUpdate";
        }

        public static void AfterPlayerUpdate()
        {
            CallCount++;
            LastHook = "AfterPlayerUpdate";
        }

        /// <summary>Returns false so the vanilla item check body still runs.</summary>
        public static bool BeforeItemCheck()
        {
            CallCount++;
            LastHook = "BeforeItemCheck";
            return false;
        }
    }
}
