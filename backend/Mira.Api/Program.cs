using Mira.Api;
using Mira.Core;

if (args is [AccountCommands.CreateUserCommand, ..])
{
    return await AccountCommands.CreateUserAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddMiraConfiguration(args);
LocalBindingPolicy.Configure(builder);
builder.AddMiraWeb();

var app = builder.Build();
LocalBindingPolicy.Validate(app.Configuration);
app.Services.GetRequiredService<MiraOptions>();
await app.InitializeMiraDatabaseAsync();

app.MapMiraEndpoints();

await app.RunAsync();
return 0;

public partial class Program { }
