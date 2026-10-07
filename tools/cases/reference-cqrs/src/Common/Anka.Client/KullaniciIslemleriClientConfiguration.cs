using Microsoft.Extensions.Configuration;
namespace Anka.Client;
public static class KullaniciIslemleriClientConfiguration
{
    public static string BaseUrl(IConfiguration configuration) => configuration["KullaniciIslemleri:BaseUrl"] ?? "";
}
