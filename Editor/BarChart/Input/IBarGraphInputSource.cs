namespace GCAllocBreakdown.BarChart.Input
{
    /// <summary>
    /// Implemented by input sources passed to <see cref="BarGraphElement.SetInputSource"/>.
    ///
    /// <see cref="BarGraphElement.SetInputSource"/> calls <see cref="Initialize"/>
    /// automatically, injecting the internal <see cref="BarGraphEventBus"/>.
    /// Callers never need to access the event bus directly.
    /// </summary>
    public interface IBarGraphInputSource
    {
        /// <summary>
        /// Called by <see cref="BarGraphElement.SetInputSource"/> immediately
        /// after the source is registered.  The input source should store this
        /// reference and use it to fire events in response to raw input.
        /// </summary>
        void Initialize(BarGraphEventBus eventBus);
    }
}
