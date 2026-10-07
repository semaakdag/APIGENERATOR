using Anka.Domain.Entities;
namespace Anka.DataAccess.Repositories.Interfaces;
public interface IKullaniciSubeRepository { Task<IReadOnlyList<KullaniciSube>> GetAllAsync(CancellationToken cancellationToken = default); }
