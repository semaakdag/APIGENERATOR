namespace Acme.Business.Models.Responses;

public sealed class CustomerResponse
{
    public int Id { get; set; }
    public required string FullName { get; set; }
}
