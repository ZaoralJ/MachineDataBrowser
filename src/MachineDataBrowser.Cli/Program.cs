using System.CommandLine;
using MachineDataBrowser.Cli;
using Spectre.Console;

// Ctrl+C / SIGTERM cancel the command; closing the session properly (OPC UA CloseSession) can take a few seconds.
var invocation = new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(10) };
// Rich output (tables, trees, live view) only for a terminal; redirected output stays plain for scripts.
var terminal = Console.IsOutputRedirected ? null : AnsiConsole.Console;
return await Commands.Build(Console.Out, Console.Error, terminal).Parse(args).InvokeAsync(invocation).ConfigureAwait(false);
