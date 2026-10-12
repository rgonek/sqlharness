using System.IO.Pipelines;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using SqlHarness.Cli;
using SqlHarness.Core;
using SqlHarness.Core.Targets;
using SqlHarness.Mcp.Tools;

namespace SqlHarness.Mcp.Tests;

/// <summary>
/// T2 startup contract: no database, no interactive auth, and stdout carries
/// only protocol frames. Bad profiles/vars fail before the handshake on
/// stderr without echoing values.
/// </summary>
[Collection("McpScopeHome")]
public sealed class McpStartupTests
{
    private const string HandshakeVersion = "2025-11-25";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static IReadOnlyDictionary<string, TargetProfile> StartupProfiles() =>
        new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            // azure-cli would need an interactive login when connecting; a
            // successful handshake therefore proves startup never connects.
            ["mcp-azure"] = new TargetProfile(
                "mcp-unreachable.invalid",
                "mcp-frozen-db",
                new Dictionary<string, string>(),
                "azure-cli"),
            ["mcp-sql"] = new TargetProfile(
                "mcp-unreachable.invalid",
                "mcp-frozen-db",
                new Dictionary<string, string>(),
                "sql",
                SqlUser: "mcp-user",
                PasswordEnvVar: "SQLHARNESS_MCP_T2_TEST_PASSWORD"),
        };

    [Fact]
    public async Task Startup_completes_handshake_without_database_auth_or_stdout_banner()
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        int reads = 0;
        Func<IReadOnlyDictionary<string, TargetProfile>> loader = () =>
        {
            reads++;
            return StartupProfiles();
        };
        var log = new StringWriter();
        var options = new McpServerOptions { Profile = "mcp-azure" };

        Task<int> hostTask = McpHost.RunAsync(
            options,
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            log,
            loader,
            cts.Token);

        var clientOptions = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
            ProtocolVersion = HandshakeVersion,
        };
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream(),
                NullLoggerFactory.Instance),
            clientOptions,
            NullLoggerFactory.Instance,
            cts.Token);

        // A stdout banner or any non-protocol bytes would break framing and
        // fail this handshake; success proves stdout carries only frames.
        Assert.Equal(HandshakeVersion, client.NegotiatedProtocolVersion);
        Assert.Equal(McpHost.ServerName, client.ServerInfo.Name);

        // T3 wires the explicit 10-tool catalog on the frozen scope: tools/list
        // serves exactly those tools and nothing else (no assembly scanning,
        // so no reload/switch-target surface can exist either).
        var served = await client.ListToolsAsync(cancellationToken: cts.Token);
        Assert.Equal(
            McpToolCatalog.ToolNames.Order(StringComparer.Ordinal),
            served.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());

        // The frozen snapshot is read exactly once, at startup.
        Assert.Equal(1, reads);

        await cts.CancelAsync();
        Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask);
    }

    [Fact]
    public async Task Startup_leaks_no_secrets_or_arguments_to_stderr()
    {
        const string secret = "t2-marker-secret-9f31";
        Environment.SetEnvironmentVariable("SQLHARNESS_MCP_T2_TEST_PASSWORD", secret);
        try
        {
            using var cts = new CancellationTokenSource(Budget);
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var log = new StringWriter();
            var options = new McpServerOptions { Profile = "mcp-sql" };

            Task<int> hostTask = McpHost.RunAsync(
                options,
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream(),
                log,
                () => StartupProfiles(),
                cts.Token);

            var clientOptions = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "sqlharness-mcp-tests", Version = "1.0.0" },
                ProtocolVersion = HandshakeVersion,
            };
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    clientToServer.Writer.AsStream(),
                    serverToClient.Reader.AsStream(),
                    NullLoggerFactory.Instance),
                clientOptions,
                NullLoggerFactory.Instance,
                cts.Token);

            await cts.CancelAsync();
            Assert.Equal((int)SqlHarnessExitCode.Success, await hostTask);

            var text = log.ToString();
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp-user", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp-unreachable.invalid", text, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp-frozen-db", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SQLHARNESS_MCP_T2_TEST_PASSWORD", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLHARNESS_MCP_T2_TEST_PASSWORD", null);
        }
    }

    [Fact]
    public async Task Unknown_profile_fails_before_handshake_on_stderr_without_value_echo()
    {
        using var cts = new CancellationTokenSource(Budget);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var log = new StringWriter();
        var options = new McpServerOptions
        {
            Profile = "no-such-profile",
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant"] = "t2-marker-value-4d77",
            },
        };

        var exit = await McpHost.RunAsync(
            options,
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            log,
            () => StartupProfiles(),
            cts.Token);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        var text = log.ToString();
        Assert.Contains("sqlharness-mcp", text, StringComparison.Ordinal);
        Assert.Contains("no-such-profile", text, StringComparison.Ordinal);
        Assert.DoesNotContain("t2-marker-value-4d77", text, StringComparison.Ordinal);
        // No server was created, so nothing may reach the stdout stream.
        Assert.False(serverToClient.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Startup_errors_name_profile_and_missing_vars_and_redact_known_password()
    {
        const string secret = "t2-marker-startup-secret-63da";
        const string passwordVariable = "SQLHARNESS_MCP_T2_STARTUP_SECRET";
        Environment.SetEnvironmentVariable(passwordVariable, secret);
        try
        {
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["selected-profile"] = new TargetProfile(
                    "mcp-unreachable.invalid",
                    "db-{tenant}",
                    new Dictionary<string, string> { [secret] = ".*" },
                    "sql",
                    SqlUser: "mcp-user",
                    PasswordEnvVar: passwordVariable),
            };
            var log = new StringWriter();
            var exit = await McpHost.RunAsync(
                new McpServerOptions { Profile = "selected-profile" },
                new MemoryStream(),
                new MemoryStream(),
                log,
                () => profiles);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Contains("selected-profile", log.ToString(), StringComparison.Ordinal);
            Assert.Contains("missing required profile vars", log.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", log.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Startup_rejected_input_root_is_named_and_known_password_is_redacted()
    {
        const string secret = "t2-marker-root-secret-19a7";
        const string passwordVariable = "SQLHARNESS_MCP_T2_ROOT_SECRET";
        Environment.SetEnvironmentVariable(passwordVariable, secret);
        try
        {
            var root = Path.Combine(Path.GetTempPath(), secret, "missing-root");
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                ["mcp-sql"] = new TargetProfile(
                    "mcp-unreachable.invalid",
                    "mcp-frozen-db",
                    new Dictionary<string, string>(),
                    "sql",
                    SqlUser: "mcp-user",
                    PasswordEnvVar: passwordVariable),
            };
            var log = new StringWriter();
            var exit = await McpHost.RunAsync(
                new McpServerOptions { Profile = "mcp-sql", InputRoots = [root] },
                new MemoryStream(),
                new MemoryStream(),
                log,
                () => profiles);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Contains("input root", log.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", log.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public async Task Startup_authentication_error_names_reason_and_redacts_profile_name_secret()
    {
        const string secret = "t2-marker-auth-secret-9b41";
        const string passwordVariable = "SQLHARNESS_MCP_T2_AUTH_SECRET";
        Environment.SetEnvironmentVariable(passwordVariable, secret);
        try
        {
            var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
            {
                [secret] = new TargetProfile(
                    "mcp-unreachable.invalid",
                    "mcp-frozen-db",
                    new Dictionary<string, string>(),
                    "invalid-auth",
                    PasswordEnvVar: passwordVariable),
            };
            var log = new StringWriter();
            var exit = await McpHost.RunAsync(
                new McpServerOptions { Profile = secret },
                new MemoryStream(),
                new MemoryStream(),
                log,
                () => profiles);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Contains("authentication", log.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("strategy", log.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", log.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, log.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordVariable, null);
        }
    }

    [Fact]
    public void Mcp_stderr_logger_renders_and_redacts_message_and_exception()
    {
        const string secret = "t2-marker-logger-secret-73f0";
        var log = new StringWriter();
        var logger = new McpHost.McpStderrLoggerFactory(log, [secret]).CreateLogger("test");
        var exception = new InvalidOperationException($"failure {secret}");

        logger.Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            new Microsoft.Extensions.Logging.EventId(17),
            $"message {secret}",
            exception,
            (state, failure) => $"{state}; {failure?.Message}");

        Assert.Contains("message [REDACTED]", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("failure [REDACTED]", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_vars_fail_before_handshake_without_value_echo()
    {
        using var cts = new CancellationTokenSource(Budget);
        var profiles = new Dictionary<string, TargetProfile>(StringComparer.Ordinal)
        {
            ["mcp-vars"] = new TargetProfile(
                "mcp-unreachable.invalid",
                "db-{tenant}",
                new Dictionary<string, string> { ["tenant"] = "^acme$" },
                "integrated"),
        };
        var log = new StringWriter();
        var options = new McpServerOptions
        {
            Profile = "mcp-vars",
            Vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant"] = "t2-marker-evil-8b12",
            },
        };

        var exit = await McpHost.RunAsync(
            options,
            new Pipe().Reader.AsStream(),
            new Pipe().Writer.AsStream(),
            log,
            () => profiles,
            cts.Token);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.DoesNotContain("t2-marker-evil-8b12", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_rejects_unsafe_direct_with_exit_2_and_empty_stdout()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(
            new SqlHarnessModule(),
            output,
            new StringReader(""),
            stdinRedirected: false,
            mcpError: error);

        var exit = await app.RunAsync(["mcp", "serve", "mcp-azure", "--unsafe-direct"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("--unsafe-direct", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Serve_rejects_out_of_spec_options_with_exit_2_leaving_stdout_empty()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(
            new SqlHarnessModule(),
            output,
            new StringReader(""),
            stdinRedirected: false,
            mcpError: error);

        var exit = await app.RunAsync(["mcp", "serve", "mcp-azure", "--json"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Contains("Invalid command line arguments", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Serve_requires_a_profile_with_exit_2()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var app = SqlHarnessCli.Create(
            new SqlHarnessModule(),
            output,
            new StringReader(""),
            stdinRedirected: false,
            mcpError: error);

        var exit = await app.RunAsync(["mcp", "serve"]);

        Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Serve_rejects_incompatible_or_missing_scope_mode_options()
    {
        string[][] invalidArgs =
        [
            ["--request-scope"],
            ["--request-scope", "--allow-profile", "sample-a", "sample-a"],
            ["--allow-profile", "sample-a"],
            ["sample-a", "--request-scope", "--allow-profile", "sample-a"],
        ];
        foreach (var args in invalidArgs)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var app = SqlHarnessCli.Create(
                new SqlHarnessModule(),
                output,
                new StringReader(""),
                stdinRedirected: false,
                mcpError: error);

            var exit = await app.RunAsync(["mcp", "serve", .. args]);

            Assert.Equal((int)SqlHarnessExitCode.Safety, exit);
            Assert.Equal(string.Empty, output.ToString());
            Assert.DoesNotContain("sample-a", error.ToString(), StringComparison.Ordinal);
        }
    }
}
