using Xunit;

namespace NimPad.Core.Tests.Support;

/// <summary>
/// Timing-sensitive tests. The collection runs on its own, never alongside other test classes, so CPU contention
/// from parallel tests cannot skew the measurements.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance (runs alone)";
}
