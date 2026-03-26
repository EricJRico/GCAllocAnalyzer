using GCAllocBreakdown.BarChart.Core;

namespace GCAllocBreakdown.BarChart.Input
{
    /// <summary>
    /// A behaviour that responds to <see cref="BarGraphEventBus"/>.
    ///
    /// Implement this interface and pass an instance to
    /// <see cref="BarGraphElement.AddHandler"/> to opt into specific chart
    /// interactions.  Only subscribe to the actions your handler needs —
    /// unused actions have no cost.
    /// </summary>
    public interface IBarGraphHandler
    {
        /// <summary>
        /// Called by <see cref="BarGraphElement.AddHandler"/>.
        /// Subscribe to whichever <see cref="BarGraphEventBus"/> events this
        /// handler cares about.
        /// </summary>
        void Register(BarGraphEventBus actions, BarGraphElement element);

        /// <summary>
        /// Called by <see cref="BarGraphElement.RemoveHandler{T}"/>.
        /// Unsubscribe from all events subscribed in <see cref="Register"/>.
        /// </summary>
        void Unregister(BarGraphEventBus actions);
    }
}
