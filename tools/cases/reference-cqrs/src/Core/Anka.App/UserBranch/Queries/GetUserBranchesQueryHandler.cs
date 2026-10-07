using Anka.App.Abstractions;
using Anka.DataAccess.Repositories.Interfaces;
using Anka.Domain.Entities;
namespace Anka.App.UserBranch.Queries;
public sealed class GetUserBranchesQueryHandler : IRequestHandler<GetUserBranchesQuery, IReadOnlyList<KullaniciSube>>
{
    private readonly IKullaniciSubeRepository repository;
    public GetUserBranchesQueryHandler(IKullaniciSubeRepository repository) => this.repository = repository;
    public Task<IReadOnlyList<KullaniciSube>> HandleAsync(GetUserBranchesQuery request, CancellationToken cancellationToken) => repository.GetAllAsync(cancellationToken);
}
