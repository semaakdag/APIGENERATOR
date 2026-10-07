using Acme.Business.Models.Requests;
using Acme.Business.Models.Responses;

namespace Acme.Business.Services.Customers;

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerResponse>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<CustomerResponse?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<CustomerResponse?> CreateAsync(CustomerCreateRequest request, CancellationToken cancellationToken = default);
    Task<CustomerResponse?> UpdateAsync(int id, CustomerUpdateRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
