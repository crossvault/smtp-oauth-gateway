using Spectre.Console.Cli;
using SmtpGateway.Admin.Tui;

var app = new CommandApp();
app.Configure(AdminTuiApp.Configure);

return await app.RunAsync(args);
