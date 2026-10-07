using Acme.Core.Entities;

namespace Acme.DataAccess.Repositories.Interfaces;

public interface ICustomerRepository
{
    Task<IReadOnlyList<Customer>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Customer?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<Customer> AddAsync(Customer entity, CancellationToken cancellationToken = default);
    Task<Customer?> UpdateAsync(Customer entity, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
