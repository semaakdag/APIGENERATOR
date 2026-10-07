namespace Acme.Core.Entities;

public sealed class Customer
{
    public int Id { get; set; }
    public required string FullName { get; set; }
}
