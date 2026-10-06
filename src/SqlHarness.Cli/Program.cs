using SqlHarness.Cli;
using SqlHarness.Core;

var config = SqlHarnessConfigLoader.Load();
if (config.Warning is not null)
    Console.Error.WriteLine(config.Warning);

var module = new JournalingModule(
    new SqlHarnessModule(),
    () => ActivityJournal.Open(config.Config.Journal, Console.Error),
    () => SessionIdentities.Cli(ProcessInfo.Current));

return await SqlHarnessCli.Create(module).RunAsync(args);