using Anka.App.UserBranch.Commands;
using Anka.App.UserBranch.Queries;
using Microsoft.AspNetCore.Mvc;
namespace Anka.Api.Controllers;
[ApiController]
[Route("api/[controller]")]
public sealed class UserBranchController : ControllerBase
{
    private readonly CreateUserBranchCommandHandler create;
    private readonly GetUserBranchesQueryHandler list;
    public UserBranchController(CreateUserBranchCommandHandler create, GetUserBranchesQueryHandler list) { this.create = create; this.list = list; }
    [HttpGet] public async Task<IActionResult> GetAll(CancellationToken cancellationToken) => Ok(await list.HandleAsync(new GetUserBranchesQuery(), cancellationToken));
    [HttpPost] public async Task<IActionResult> Create(CreateUserBranchCommand command, CancellationToken cancellationToken) => Ok(await create.HandleAsync(command, cancellationToken));
}
