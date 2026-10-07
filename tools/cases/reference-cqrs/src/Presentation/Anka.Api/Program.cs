using Anka.App.Handlers;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddApp();
var app = builder.Build();
app.MapControllers();
app.Run();
