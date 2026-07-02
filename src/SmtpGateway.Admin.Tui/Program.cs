using Spectre.Console.Cli;
using SmtpGateway.Admin.Tui;

// No arguments -> launch the polished interactive menu shell. Any argument at all (including
// '--help') keeps the existing Spectre.Console.Cli command path exactly as before, so scripting
// behaviour is untouched.
if (args.Length == 0)
{
    return await InteractiveShell.RunAsync();
}

var app = new CommandApp();
app.Configure(AdminTuiApp.Configure);

return await app.RunAsync(args);
