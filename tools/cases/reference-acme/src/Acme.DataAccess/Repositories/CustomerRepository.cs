using Acme.Core.Entities;
using Acme.DataAccess.Repositories.Interfaces;

namespace Acme.DataAccess.Repositories;

public sealed class CustomerRepository : ICustomerRepository
{
    private static readonly List<Customer> Items = new();

    public Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Customer>>(Items.ToList());
    public Task<Customer?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(item => item.Id == id));
    public Task<Customer> AddAsync(Customer entity, CancellationToken cancellationToken = default) { Items.Add(entity); return Task.FromResult(entity); }
    public Task<Customer?> UpdateAsync(Customer entity, CancellationToken cancellationToken = default) => Task.FromResult<Customer?>(entity);
    public Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Items.RemoveAll(item => item.Id == id) > 0);
}
