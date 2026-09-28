using System.Reflection;

using Spectre.Console.Cli;

using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli;

public static class SqlHarnessCli
{
    public static SqlHarnessApp Create(ISqlHarnessModule module, TextWriter? output = null, TextReader? stdin = null, bool? stdinRedirected = null, Stream? planStdin = null)
    {
        var registrar = new Registrar();
        var outputContext = new OutputContext(output ?? Console.Out);
        registrar.Add(module); registrar.Add(outputContext); registrar.Add(new Renderer());
        registrar.Add(new CliInput(stdin ?? Console.In, stdinRedirected ?? Console.IsInputRedirected));
        registrar.Add(new PlanInput(planStdin ?? Console.OpenStandardInput()));
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.SetApplicationName("sqlharness");
            c.SetApplicationVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
            c.SetExceptionHandler((exception, _) =>
            {
                if (outputContext.Mode == OutputMode.Text)
                    return -1;
                const string safeMessage = "Invalid command line arguments.";
                new Renderer().RenderError(SqlHarnessExitCode.Safety, safeMessage, outputContext.Mode, outputContext.Command, outputContext.Capture, agentOptions: outputContext.AgentOptions);
                outputContext.Capture.Flush();
                return (int)SqlHarnessExitCode.Safety;
            });
            // Relaxed parsing would ignore --matrix on measure and query instead of rejecting it.
            c.UseStrictParsing();
            c.AddCommand<QueryCommand>("query").WithDescription("Run a bounded SQL query.");
            c.AddCommand<MeasureCommand>("measure").WithDescription("Measure query performance across repeated runs.");
            c.AddCommand<CompareCommand>("compare").WithDescription("Compare baseline and candidate performance and results.");
            c.AddCommand<GainCommand>("gain").WithDescription("Report local output savings estimates.");
            c.AddCommand<PlanCommand>("plan").WithDescription("Distill a saved execution plan offline.");
            c.AddCommand<ArtifactCommand>("artifact").WithDescription("Read safe sections of a saved benchmark artifact.");
            c.AddCommand<SchemaCommand>("schema").WithDescription("Inspect database tables, columns, and relations.");
            c.AddCommand<PingCommand>("ping").WithDescription("Check a database connection and target identity.");
            c.AddCommand<CountsCommand>("counts").WithDescription("Inspect row counts for database tables.");
            c.AddCommand<SpaceCommand>("space").WithDescription("Inspect database file and table storage.");
            c.AddCommand<WatchCommand>("watch").WithDescription("Poll a bounded read-only query until a condition is met.");
            c.AddCommand<SnapshotCommand>("snapshot").WithDescription("Capture or compare a named query result.");
            c.AddCommand<QueryStoreTopCommand>("qstop").WithDescription("Rank SQL Server Query Store consumers.");
            c.AddCommand<IndexesCommand>("indexes").WithDescription("Inspect SQL Server missing-index evidence.");
            c.AddCommand<CapabilitiesCommand>("capabilities").WithDescription("Describe local commands, engines, limits, and output modes.");
            c.AddCommand<DoctorCommand>("doctor").WithDescription("Check local installation and profile-file availability without connecting.");
            c.AddCommand<ValidateCommand>("validate").WithDescription("Classify SQL offline using a closed profile; never connects.");
        });
        return new SqlHarnessApp(app, outputContext);
    }
    private sealed class Registrar : ITypeRegistrar
    {
        private readonly Dictionary<Type, object> _services = [];
        public void Add(object service)
        {
            _services[service.GetType()] = service;
            foreach (var contract in service.GetType().GetInterfaces()) _services[contract] = service;
        }
        public ITypeResolver Build() => new Resolver(_services);
        public void Register(Type service, Type implementation) => _services[service] = implementation;
        public void RegisterInstance(Type service, object implementation) => _services[service] = implementation;
        public void RegisterLazy(Type service, Func<object> factory) => _services[service] = factory;
    }
    private sealed class Resolver(Dictionary<Type, object> services) : ITypeResolver, IDisposable
    {
        public object? Resolve(Type? type)
        {
            if (type is null) return null;
            if (services.TryGetValue(type, out var value)) return value switch { Type t => Create(t), Func<object> f => f(), _ => value };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            if (type.IsInterface || type.IsAbstract) return null;
            return Create(type);
        }
        private object Create(Type type) => Activator.CreateInstance(type, type.GetConstructors().Single().GetParameters().Select(p => Resolve(p.ParameterType)).ToArray())!;
        public void Dispose() { }
    }
}

public sealed class SqlHarnessApp(CommandApp app, OutputContext output)
{
    public Task<int> RunAsync(IEnumerable<string> args)
    {
        var normalized = args.ToArray();
        output.Configure(normalized);
        if (normalized.Length >= 2 && string.Equals(normalized[0], "plan", StringComparison.OrdinalIgnoreCase) && normalized[1] == "-")
            normalized = [normalized[0], .. normalized.Skip(2)];
        return app.RunAsync(normalized);
    }
}
