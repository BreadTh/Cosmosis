namespace BreadTh.Cosmosis.Data.ValueObjects;

/// <summary>
/// The .id "primary key" of the document/record.
/// Cosmos operations take multiple string parameters that are easy to mix up at call sites.
/// Wrapping them prevents accidentally passing a partition key where a document id is expected.
/// </summary>
public readonly struct CosmosDocumentId(string value)
{
    public string Value { get; } = value;
}