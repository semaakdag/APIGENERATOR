using Microsoft.Extensions.Configuration;
namespace Anka.App.UserBranch.Commands.Handlers;
public sealed class DeleteUserBranchHandler
{
    private readonly IConfiguration configuration;
    public DeleteUserBranchHandler(IConfiguration configuration) => this.configuration = configuration;
    public Task<DeleteUserBranchResponse> HandleAsync(DeleteUserBranchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DeleteUserBranchResponse(configuration["Seed"] is not null));
}
