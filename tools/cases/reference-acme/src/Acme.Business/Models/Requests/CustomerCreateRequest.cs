namespace Acme.Business.Models.Requests;

public sealed class CustomerCreateRequest
{
    public int Id { get; set; }
    public required string FullName { get; set; }
}
