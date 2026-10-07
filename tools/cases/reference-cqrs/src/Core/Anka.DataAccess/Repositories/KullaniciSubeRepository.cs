using Anka.DataAccess.Repositories.Interfaces;
using Anka.Domain.Entities;
namespace Anka.DataAccess.Repositories;
public sealed class KullaniciSubeRepository : IKullaniciSubeRepository
{
    public Task<IReadOnlyList<KullaniciSube>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KullaniciSube>>([]);
}
