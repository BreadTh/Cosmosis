namespace BreadTh.Cosmosis.Data.Dto;

public sealed class CosmosisUpdateOptions : BaseCosmosisOptions
{
    public int MaxETagMismatchRetries { get; set; } = 20;
}