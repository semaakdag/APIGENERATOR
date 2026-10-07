namespace Acme.Business.Models.Requests;

public sealed class CustomerUpdateRequest
{
    public required string FullName { get; set; }
}
