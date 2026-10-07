using Anka.App.Handlers;
using Anka.Client;
using Anka.Components;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<LogHelper>();
builder.Services.Configure<KullaniciIslemleriClientOptions>(
    builder.Configuration.GetSection("KullaniciIslemleri"));
builder.Services.AddApp();
var app = builder.Build();
app.MapControllers();
app.Run();
