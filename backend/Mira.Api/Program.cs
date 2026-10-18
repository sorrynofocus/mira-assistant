using Mira.Api;
using Mira.Core;
using Mira.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Local overrides are private; environment variables and arguments take precedence.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddEnvironmentVariables(prefix: "MIRA_");
builder.Configuration.AddCommandLine(args);

LocalBindingPolicy.Configure(builder);
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IConfiguration>()
        .GetSection(MiraOptions.SectionName).Get<MiraOptions>() ?? new MiraOptions();
    options.Validate();
    return options;
});
builder.Services.AddSingleton<IChatProvider, MockChatProvider>();
builder.Services.AddSingleton<ITranscriptionProvider, MockTranscriptionProvider>();
builder.Services.AddSingleton<ISpeechProvider, MockSpeechProvider>();
builder.Services.AddSingleton<IGovernanceService, DenyAllGovernanceService>();

var app = builder.Build();
LocalBindingPolicy.Validate(app.Configuration);
app.Services.GetRequiredService<MiraOptions>();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
