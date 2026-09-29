using System;

namespace ZhaDai.Runtime
{
    /// <summary>
    /// The only surface the patched Terraria.exe calls into. Three public static methods, no
    /// parameters, exactly as <c>ZhaDai.Patcher.HookContract</c> declares them; the offline verifier
    /// checks this type by name against the built assembly, so renaming anything here breaks the
    /// patch loudly rather than silently.
    ///
    /// The bodies are deliberately trivial. Everything else lives in <see cref="Host"/>, which is
    /// free to change without touching the contract.
    /// </summary>
    public static class Hooks
    {
        /// <summary>Frame start, before the local player updates. The executor decides here.</summary>
        public static void BeforePlayerUpdate()
        {
            Guard(Host.Frame);
        }

        /// <summary>End of the player update. Used to close out the frame.</summary>
        public static void AfterPlayerUpdate()
        {
            Guard(Host.EndFrame);
        }

        /// <summary>
        /// Runs a host entry point with the game protected from us. The try/catch inside the host cannot cover
        /// everything: if a type the host needs fails to load -- a missing ZhaDai.Automation.dll next to the
        /// game, say -- the failure happens while the host method is being compiled, so it surfaces *here*,
        /// in the caller. A player who installs half the files should get a game that still runs, not a crash.
        /// </summary>
        private static void Guard(Action entry)
        {
            try
            {
                entry();
            }
            catch (Exception exception)
            {
                try
                {
                    Console.Error.WriteLine("ZhaDai 钩子出错（游戏继续）：" + exception);
                }
                catch (Exception)
                {
                    // Nothing left to do: the host is already down for this frame.
                }
            }
        }

        /// <summary>
        /// Item check seam. Returning <c>false</c> hands the frame straight back to the vanilla body,
        /// which is what this build wants: the executor does not take over item logic, it only holds
        /// the use button and lets the game throw the dynamite itself. The hook exists so a future
        /// version can answer <c>true</c> here without repatching the game.
        /// </summary>
        public static bool BeforeItemCheck()
        {
            return false;
        }
    }
}
