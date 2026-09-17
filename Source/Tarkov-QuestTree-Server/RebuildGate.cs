using System;
using System.Threading.Tasks;

namespace QuestTreeServer
{
    /// <summary>
    /// The pause after a failed payload build, and the background retry that ends it. A failure
    /// is answered with the route's empty shape but never cached: one transient fault at boot
    /// used to mean an empty tree or no pins until the server restarted. The pause keeps a
    /// persistent fault from being rebuilt on every request, and the retry runs off any request
    /// so a GET is never the one to pay for a multi-second build. Was a verbatim copy in each
    /// of the two cached builders.
    /// </summary>
    internal sealed class RebuildGate
    {
        private readonly int _seconds;
        private DateTime _retryAfter = DateTime.MinValue;

        public RebuildGate(int seconds) => _seconds = seconds;

        public bool Paused => DateTime.UtcNow < _retryAfter;

        /// <summary>An explicit rebuild ignores the pause: new data is a reason to try.</summary>
        public void Clear() => _retryAfter = DateTime.MinValue;

        public void PauseThenRewarm(Action rebuild, Action<string> warn)
        {
            _retryAfter = DateTime.UtcNow.AddSeconds(_seconds);

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(_seconds + 1));
                try
                {
                    rebuild();
                }
                catch (Exception ex)
                {
                    warn(ex.Message);
                }
            });
        }
    }
}
