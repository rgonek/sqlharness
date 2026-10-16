using SqlHarness.Cli;
using SqlHarness.Core;

var config = SqlHarnessConfigLoader.Load();
if (config.Warning is not null && !SqlHarnessCli.PrintsOwnConfigWarning(args))
    Console.Error.WriteLine(config.Warning);

var module = new JournalingModule(
    new SqlHarnessModule(config.Config.Compare),
    () => ActivityJournal.Open(config.Config.Journal, Console.Error),
    () => SessionIdentities.Cli(ProcessInfo.Current),
    diagnostics: config.Config.Diagnostics);

return await SqlHarnessCli.Create(module, compareConfig: config.Config.Compare,
    mutationApprovalTimeoutSeconds: config.Config.Mutations.ApprovalTimeoutSeconds, config: config.Config).RunAsync(args);