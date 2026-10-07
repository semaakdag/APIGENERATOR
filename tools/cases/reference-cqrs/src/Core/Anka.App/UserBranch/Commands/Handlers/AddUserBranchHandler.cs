using Microsoft.Extensions.Configuration;
namespace Anka.App.UserBranch.Commands.Handlers;
public sealed class AddUserBranchHandler
{
    private readonly IConfiguration configuration;
    public AddUserBranchHandler(IConfiguration configuration) => this.configuration = configuration;
    public Task<AddUserBranchResponse> HandleAsync(AddUserBranchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AddUserBranchResponse(configuration["Seed"]?.Length ?? 0));
}
