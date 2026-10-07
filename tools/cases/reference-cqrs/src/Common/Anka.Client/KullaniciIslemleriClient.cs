namespace Anka.Client;
public sealed class KullaniciIslemleriClient
{
    private readonly KullaniciIslemleriClientOptions options;
    public KullaniciIslemleriClient(KullaniciIslemleriClientOptions options) => this.options = options;
    public string Url => options.BaseUrl;
}
