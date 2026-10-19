using System.Reflection;

using SqlHarness.Core;
using SqlHarness.Core.Targets;

namespace SqlHarness.Tests;

public sealed class SqlHarnessModuleConnectionConfigTests
{
    [Fact]
    public void Configured_timeout_reaches_both_provider_factories_created_by_module()
    {
        const int timeoutSeconds = 75;
        var module = new SqlHarnessModule(
            () => new Dictionary<string, TargetProfile>(),
            new CompareConfig(),
            new ConnectionConfig { TimeoutSeconds = timeoutSeconds });

        var timedFactory = GetPrivateField<ISqlSessionFactory>(module, "_sessionFactory");
        var engineFactory = GetPrivateField<ISqlSessionFactory>(timedFactory, "_inner");
        var sqlServerFactory = GetPrivateField<ISqlSessionFactory>(engineFactory, "_sqlServer");
        var postgresFactory = GetPrivateField<ISqlSessionFactory>(engineFactory, "_postgres");

        Assert.Equal(timeoutSeconds, GetPrivateField<int>(sqlServerFactory, "_connectTimeoutSeconds"));
        Assert.Equal(timeoutSeconds, GetPrivateField<int>(postgresFactory, "_connectTimeoutSeconds"));
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field.GetValue(instance));
    }
}