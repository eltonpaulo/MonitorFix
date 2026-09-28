using Xunit;

namespace MonitorToneFix.Tests;

/// <summary>
/// The tests that drive a real picom must not run in parallel with each other: X11
/// allows a single compositing manager, so two of them at once make one lose the race
/// and fail for a reason that has nothing to do with what it is testing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CompositorCollection
{
    public const string Name = "compositor";
}
