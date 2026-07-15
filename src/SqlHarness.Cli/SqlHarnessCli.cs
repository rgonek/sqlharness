using System.Reflection;
using Spectre.Console.Cli;
using SqlHarness.Cli.Commands;
using SqlHarness.Cli.Infrastructure;
using SqlHarness.Core;

namespace SqlHarness.Cli;

public static class SqlHarnessCli
{
    public static SqlHarnessApp Create(ISqlHarnessModule module, TextWriter? output = null, TextReader? stdin = null, bool? stdinRedirected = null)
    {
        var registrar = new Registrar();
        registrar.Add(module); registrar.Add(new OutputContext(output ?? Console.Out)); registrar.Add(new Renderer());
        registrar.Add(new CliInput(stdin ?? Console.In, stdinRedirected ?? Console.IsInputRedirected));
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.SetApplicationName("sqlharness");
            c.SetApplicationVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");
            c.AddCommand<QueryCommand>("query"); c.AddCommand<MeasureCommand>("measure");
            c.AddCommand<CompareCommand>("compare"); c.AddCommand<GainCommand>("gain");
            c.AddCommand<PlanCommand>("plan");
        });
        return new SqlHarnessApp(app);
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

public sealed class SqlHarnessApp(CommandApp app)
{
    public Task<int> RunAsync(IEnumerable<string> args)
    {
        var normalized = args.ToArray();
        if (normalized.Length >= 2 && string.Equals(normalized[0], "plan", StringComparison.OrdinalIgnoreCase) && normalized[1] == "-")
            normalized = [normalized[0], .. normalized.Skip(2)];
        return app.RunAsync(normalized);
    }
}
