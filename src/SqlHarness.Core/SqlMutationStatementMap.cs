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

    internal static IReadOnlySet<Type> PrivilegeStatementTypes { get; } = new HashSet<Type>();

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