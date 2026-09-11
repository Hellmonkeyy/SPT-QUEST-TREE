using System;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using UnityEngine;
using UnityEngine.UI;

namespace QuestTree.UI
{
    /// <summary>
    /// Loads a trader's portrait onto an Image.
    /// </summary>
    ///
    /// <remarks>
    /// The trader tabs have done this since 1.2.0; the tree's chain markers need the same thing and
    /// have no session of their own, so the session is parked here on Show rather than threaded
    /// through the graph view, the layout and the marker layer to reach one call.
    ///
    /// The cancellation pattern is the tabs' and matters for the same reason: a graph rebuild
    /// destroys these objects while a load may still be in flight, and CancelOnDestroy ties a real
    /// token to the object's lifetime so the load stops doing wasted work the moment its target goes
    /// away - rather than only having its failure observed afterwards.
    /// </remarks>
    internal static class TraderAvatars
    {
        /// <summary>The live session, set when the panel opens. Null out of game, and every caller
        /// treats that as "no avatar" rather than as an error.</summary>
        public static IEftSession Session { get; set; }

        /// <summary>Starts loading a trader's portrait onto <paramref name="image"/>. Does nothing
        /// when there is no session or no such trader, which is the honest answer for a modded
        /// trader the session does not carry.</summary>
        public static void Assign(string traderId, Image image)
        {
            if (image == null || string.IsNullOrEmpty(traderId)) return;

            var trader = Session?.Traders?.FirstOrDefault(t => t.Id == traderId);
            if (trader == null) return;

            var cancelOnDestroy = image.gameObject.GetComponent<CancelOnDestroy>()
                                  ?? image.gameObject.AddComponent<CancelOnDestroy>();

            try
            {
                trader.GetAndAssignAvatar(image, cancelOnDestroy.Token)
                    .ContinueWith(
                        t => Plugin.LogSource?.LogWarning(
                            $"QuestTree: trader avatar load failed for '{traderId}': {t.Exception?.GetBaseException().Message}"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                Plugin.LogSource?.LogWarning($"QuestTree: could not start the trader avatar load for '{traderId}': {ex.Message}");
            }
        }
    }
}
