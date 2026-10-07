using Anka.App.UserBranch.Commands;
using Anka.App.UserBranch.Queries;
using Anka.Client;
using Anka.DataAccess.Repositories;
using Anka.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;
namespace Anka.App.Handlers;
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApp(this IServiceCollection services)
    {
        services.AddSingleton(new KullaniciIslemleriClientOptions());
        services.AddScoped<KullaniciIslemleriClient>();
        services.AddScoped<IKullaniciSubeRepository, KullaniciSubeRepository>();
        services.AddScoped<CreateUserBranchCommandHandler>();
        services.AddScoped<GetUserBranchesQueryHandler>();
        return services;
    }
}
