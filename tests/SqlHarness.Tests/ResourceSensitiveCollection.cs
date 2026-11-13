namespace SqlHarness.Tests;

// These tests create real process trees or saturate a single SQLite journal.
// Preserve their internal concurrency without competing with unrelated suites
// for the shared runner's startup and IO capacity.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResourceSensitiveCollection
{
    public const string Name = "Resource-sensitive process and journal tests";
}