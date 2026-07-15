using System.Reflection;

using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("sqlharness");
    config.SetApplicationVersion(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
});

return app.Run(args);