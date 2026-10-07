using Acme.Business.Models.Requests;
using Acme.Business.Models.Responses;
using Acme.Core.Entities;
using Acme.DataAccess.Repositories.Interfaces;

namespace Acme.Business.Services.Customers;

public sealed class CustomerService : ICustomerService
{
    private readonly ICustomerRepository repository;

    public CustomerService(ICustomerRepository repository)
    {
        this.repository = repository;
    }

    public async Task<IReadOnlyList<CustomerResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetAllAsync(cancellationToken)).Select(Map).ToList();

    public async Task<CustomerResponse?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await repository.GetByIdAsync(id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<CustomerResponse?> CreateAsync(CustomerCreateRequest request, CancellationToken cancellationToken = default)
    {
        var entity = new Customer { Id = request.Id, FullName = request.FullName };
        return Map(await repository.AddAsync(entity, cancellationToken));
    }

    public async Task<CustomerResponse?> UpdateAsync(int id, CustomerUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var existing = await repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        existing.FullName = request.FullName;
        var updated = await repository.UpdateAsync(existing, cancellationToken);
        return updated is null ? null : Map(updated);
    }

    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => repository.DeleteAsync(id, cancellationToken);

    private static CustomerResponse Map(Customer entity) => new() { Id = entity.Id, FullName = entity.FullName };
}
