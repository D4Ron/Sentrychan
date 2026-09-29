namespace Sentrychan.Tests;

/// <summary>
/// Tests that change process-wide state (environment variables such as SENTRYCHAN_DATA_DIR)
/// join this collection so they never run alongside each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentCollection
{
    public const string Name = "Process environment";
}
