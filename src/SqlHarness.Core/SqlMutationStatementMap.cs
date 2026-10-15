using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlHarness.Core;

internal static class SqlMutationStatementMap
{
    internal static IReadOnlySet<Type> SchemaStatementTypes { get; } = new HashSet<Type>
    {
        typeof(CreateTableStatement),
        typeof(AlterTableStatement),
        typeof(DropTableStatement),
        typeof(CreateIndexStatement),
        typeof(AlterIndexStatement),
        typeof(DropIndexStatement),
        typeof(CreateViewStatement),
        typeof(AlterViewStatement),
        typeof(CreateOrAlterViewStatement),
        typeof(DropViewStatement),
        typeof(CreateProcedureStatement),
        typeof(AlterProcedureStatement),
        typeof(CreateOrAlterProcedureStatement),
        typeof(DropProcedureStatement),
        typeof(CreateFunctionStatement),
        typeof(AlterFunctionStatement),
        typeof(CreateOrAlterFunctionStatement),
        typeof(DropFunctionStatement),
        typeof(CreateTriggerStatement),
        typeof(AlterTriggerStatement),
        typeof(CreateOrAlterTriggerStatement),
        typeof(DropTriggerStatement),
        typeof(CreateSchemaStatement),
        typeof(AlterSchemaStatement),
        typeof(DropSchemaStatement),
        typeof(CreateTypeTableStatement),
        typeof(CreateTypeUddtStatement),
        typeof(DropTypeStatement),
        typeof(CreateSequenceStatement),
        typeof(AlterSequenceStatement),
        typeof(DropSequenceStatement),
        typeof(CreateSynonymStatement),
        typeof(DropSynonymStatement),
        typeof(CreateStatisticsStatement),
        typeof(UpdateStatisticsStatement),
        typeof(DropStatisticsStatement),
        typeof(TruncateTableStatement),
    };

    internal static IReadOnlySet<Type> PrivilegeStatementTypes { get; } = new HashSet<Type>
    {
        typeof(GrantStatement),
        typeof(DenyStatement),
        typeof(RevokeStatement),
        typeof(CreateRoleStatement),
        typeof(AlterRoleStatement),
        typeof(DropRoleStatement),
        typeof(CreateUserStatement),
        typeof(AlterUserStatement),
        typeof(DropUserStatement),
        typeof(CreateApplicationRoleStatement),
        typeof(AlterApplicationRoleStatement),
        typeof(DropApplicationRoleStatement),
    };

    internal static IEnumerable<string> Grantees(TSqlStatement statement)
    {
        switch (statement)
        {
            case SecurityStatement securityStatement:
                foreach (var principal in securityStatement.Principals)
                {
                    if (principal.Identifier?.Value is { Length: > 0 } name)
                        yield return string.Equals(name, "public", StringComparison.OrdinalIgnoreCase) ? "public" : name;
                }
                break;
            case AlterRoleStatement { Action: AddMemberAlterRoleAction { Member.Value: { Length: > 0 } member } }:
                yield return member;
                break;
            case CreateUserStatement { Name.Value: { Length: > 0 } name }:
                yield return name;
                break;
            case AlterUserStatement { Name.Value: { Length: > 0 } name }:
                yield return name;
                break;
            case IfStatement ifStatement:
                foreach (var grantee in Grantees(ifStatement.ThenStatement))
                    yield return grantee;
                if (ifStatement.ElseStatement is { } elseStatement)
                {
                    foreach (var grantee in Grantees(elseStatement))
                        yield return grantee;
                }
                break;
            case BeginEndBlockStatement block:
                foreach (var inner in block.StatementList.Statements)
                {
                    foreach (var grantee in Grantees(inner))
                        yield return grantee;
                }
                break;
        }
    }

    internal static IReadOnlySet<Type> RejectedStatementTypes { get; } = new HashSet<Type>
    {
        typeof(CreateLoginStatement),
        typeof(AlterLoginStatement),
        typeof(DropLoginStatement),
        typeof(AlterServerRoleStatement),
        typeof(CreateServerRoleStatement),
        typeof(DropServerRoleStatement),
        typeof(AlterDatabaseStatement),
        typeof(CreateDatabaseStatement),
        typeof(DropDatabaseStatement),
        typeof(BackupStatement),
        typeof(RestoreStatement),
        typeof(ShutdownStatement),
        typeof(KillStatement),
        typeof(DbccStatement),
        typeof(ReconfigureStatement),
        typeof(CreateEndpointStatement),
        typeof(AlterServerConfigurationStatement),
        typeof(AlterServerConfigurationSetBufferPoolExtensionStatement),
        typeof(AlterServerConfigurationSetDiagnosticsLogStatement),
        typeof(AlterServerConfigurationSetExternalAuthenticationStatement),
        typeof(AlterServerConfigurationSetFailoverClusterPropertyStatement),
        typeof(AlterServerConfigurationSetHadrClusterStatement),
        typeof(AlterServerConfigurationSetSoftNumaStatement),
        typeof(RestoreMasterKeyStatement),
        typeof(RestoreServiceMasterKeyStatement),
        typeof(UseStatement),
    };
}