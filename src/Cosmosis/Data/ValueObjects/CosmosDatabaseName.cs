namespace BreadTh.Cosmosis.Data.ValueObjects;

/// <summary>
/// The cosmos database name (not the Azure resource name).
/// Cosmos operations take multiple string parameters that are easy to mix up at call sites.
/// Wrapping them prevents accidentally passing a container name where a database name is expected.
/// </summary>
public readonly struct CosmosDatabaseName(string value)
{
    public string Value { get; } = value;
}