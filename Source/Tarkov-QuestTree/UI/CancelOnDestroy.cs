using System.Threading;
using UnityEngine;

namespace QuestTree.UI
{
    /// <summary>Cancels a CancellationTokenSource when its GameObject is destroyed. The trader
    /// avatar loads are async and the rows and tab icons they land on are destroyed on every
    /// rebuild; this ties a load's lifetime to its target's so a late result never lands on a
    /// dead Image, and the load itself stops doing wasted work. Was two identical nested classes.</summary>
    internal sealed class CancelOnDestroy : MonoBehaviour
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;

        public void OnDestroy()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
