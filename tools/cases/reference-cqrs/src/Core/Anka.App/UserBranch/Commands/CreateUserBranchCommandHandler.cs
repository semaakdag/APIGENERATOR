using Anka.App.Abstractions;
using Anka.Client;
namespace Anka.App.UserBranch.Commands;
public sealed class CreateUserBranchCommandHandler : IRequestHandler<CreateUserBranchCommand, int>
{
    private readonly KullaniciIslemleriClient client;
    public CreateUserBranchCommandHandler(KullaniciIslemleriClient client) => this.client = client;
    public Task<int> HandleAsync(CreateUserBranchCommand request, CancellationToken cancellationToken) => Task.FromResult(client.Url.Length);
}
