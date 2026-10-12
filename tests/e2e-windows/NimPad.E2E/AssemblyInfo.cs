using Xunit.Sdk;
using Xunit.v3;

// Every test drives the one real desktop (mouse, keyboard, foreground window), so tests must never run in parallel.
// (xunit.v3 4.x replaces CollectionBehavior(DisableTestParallelization = true) with this attribute.)
[assembly: Parallelization(Mode = ParallelMode.None)]
