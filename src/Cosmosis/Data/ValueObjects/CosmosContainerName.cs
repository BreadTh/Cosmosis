namespace BreadTh.Cosmosis.Data.ValueObjects;

/// <summary>
/// The cosmos container name.
/// Cosmos operations take multiple string parameters that are easy to mix up at call sites.
/// Wrapping them prevents accidentally passing a database name where a container name is expected.
/// </summary>
public readonly struct CosmosContainerName(string value)
{
    public string Value { get; } = value;
}