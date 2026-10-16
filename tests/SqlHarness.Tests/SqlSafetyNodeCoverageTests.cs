using System.Globalization;

using Microsoft.SqlServer.TransactSql.ScriptDom;

using SqlHarness.Core;
using SqlHarness.Core.Dialect;

namespace SqlHarness.Tests;

// 018: every concrete ScriptDom table source and external call is classified.
// ParserType is the highest TSql<digits>Parser in the ScriptDom package.
public class SqlSafetyNodeCoverageTests
{
    // 018: concrete TableReference types absent from AllowedTableReferenceTypes.
    // Denied by omission. Not added to the allow-set.
    private static readonly IReadOnlySet<Type> KnownDeniedTableReferenceTypes = new HashSet<Type>
    {
        typeof(AdHocTableReference),
        typeof(AIGenerateChunksTableReference),
        typeof(AIGenerateFixedChunksTableReference),
        typeof(BulkOpenRowset),
        typeof(InternalOpenRowset),
        typeof(OpenQueryTableReference),
        typeof(OpenRowsetCosmos),
        typeof(OpenRowsetTableReference),
        typeof(OpenXmlTableReference),
        typeof(PredictTableReference),
        typeof(VectorSearchTableReference),
    };

    private static readonly IReadOnlySet<Type> KnownQueryOrOpaqueStatementTypes = new HashSet<Type>
    {
        typeof(DeclareTableVariableStatement),
        typeof(SetVariableStatement),
        typeof(DeclareVariableStatement),
        typeof(TruncateTableStatement),
        typeof(AlterTableAddTableElementStatement),
        typeof(AlterTableDropTableElementStatement),
        typeof(SelectStatement),
        typeof(CreateTableStatement),
        typeof(CreateIndexStatement),
        typeof(DropTableStatement),
        typeof(InsertStatement),
        typeof(UpdateStatement),
        typeof(DeleteStatement),
        typeof(MergeStatement),
        typeof(BeginEndAtomicBlockStatement),
        typeof(IfStatement),
        typeof(BeginEndBlockStatement),
        typeof(ExecuteStatement),
    };

    // New or unsupported T-SQL statements stay denied by the classifier's fail-closed default.
    private static readonly IReadOnlySet<Type> KnownDeniedByOmission = new HashSet<Type>
    {
        // Security, credentials, and database-level operations not explicitly mapped above.
        typeof(AddSensitivityClassificationStatement), typeof(AddSignatureStatement),
        typeof(AlterApplicationRoleStatement), typeof(AlterAssemblyStatement), typeof(AlterAsymmetricKeyStatement),
        typeof(AlterAuthorizationStatement), typeof(AlterAvailabilityGroupStatement), typeof(AlterBrokerPriorityStatement),
        typeof(AlterCertificateStatement), typeof(AlterColumnEncryptionKeyStatement), typeof(AlterCredentialStatement),
        typeof(AlterCryptographicProviderStatement), typeof(AlterDatabaseAuditSpecificationStatement),
        typeof(AlterDatabaseEncryptionKeyStatement), typeof(AlterDatabaseScopedConfigurationClearStatement),
        typeof(AlterDatabaseScopedConfigurationSetStatement), typeof(AlterEndpointStatement), typeof(AlterEventSessionStatement),
        typeof(AlterLoginAddDropCredentialStatement), typeof(AlterLoginEnableDisableStatement), typeof(AlterLoginOptionsStatement),
        typeof(AlterMasterKeyStatement), typeof(AlterMessageTypeStatement), typeof(AlterRemoteServiceBindingStatement),
        typeof(AlterRoleStatement), typeof(AlterSecurityPolicyStatement), typeof(AlterServerAuditSpecificationStatement),
        typeof(AlterServerAuditStatement), typeof(AlterServiceMasterKeyStatement), typeof(AlterServiceStatement),
        typeof(AlterSymmetricKeyStatement), typeof(AlterUserStatement), typeof(AlterXmlSchemaCollectionStatement),
        typeof(BackupCertificateStatement), typeof(BackupMasterKeyStatement), typeof(BackupServiceMasterKeyStatement),
        typeof(CreateApplicationRoleStatement), typeof(CreateAsymmetricKeyStatement), typeof(CreateCertificateStatement),
        typeof(CreateColumnEncryptionKeyStatement), typeof(CreateColumnMasterKeyStatement), typeof(CreateCredentialStatement),
        typeof(CreateColumnStoreIndexStatement),
        typeof(CreateDatabaseAuditSpecificationStatement), typeof(CreateDatabaseEncryptionKeyStatement),
        typeof(CreateMasterKeyStatement), typeof(CreateRoleStatement), typeof(CreateServerAuditSpecificationStatement),
        typeof(CreateServerAuditStatement), typeof(CreateSymmetricKeyStatement), typeof(CreateUserStatement),
        typeof(DenyStatement), typeof(DenyStatement80), typeof(DropApplicationRoleStatement), typeof(DropAsymmetricKeyStatement),
        typeof(DropCertificateStatement), typeof(DropColumnEncryptionKeyStatement), typeof(DropColumnMasterKeyStatement),
        typeof(DropCredentialStatement), typeof(DropDatabaseAuditSpecificationStatement), typeof(DropDatabaseEncryptionKeyStatement),
        typeof(DropMasterKeyStatement), typeof(DropRoleStatement), typeof(DropSecurityPolicyStatement),
        typeof(DropSensitivityClassificationStatement), typeof(DropServerAuditSpecificationStatement),
        typeof(DropServerAuditStatement), typeof(DropSignatureStatement), typeof(DropSymmetricKeyStatement), typeof(DropUserStatement),
        typeof(ExecuteAsStatement), typeof(GrantStatement), typeof(GrantStatement80), typeof(RevokeStatement), typeof(RevokeStatement80),

        // Schema and external-resource statements outside the requested schema tier.
        typeof(AlterExternalDataSourceStatement), typeof(AlterExternalFunctionStatement), typeof(AlterExternalLanguageStatement),
        typeof(AlterExternalLibraryStatement), typeof(AlterExternalModelStatement), typeof(AlterExternalResourcePoolStatement),
        typeof(AlterFederationStatement), typeof(AlterFullTextCatalogStatement), typeof(AlterFullTextIndexStatement),
        typeof(AlterFullTextStopListStatement), typeof(AlterPartitionFunctionStatement), typeof(AlterPartitionSchemeStatement),
        typeof(AlterQueueStatement), typeof(AlterResourceGovernorStatement), typeof(AlterResourcePoolStatement),
        typeof(AlterRouteStatement), typeof(AlterSearchPropertyListStatement), typeof(AlterWorkloadGroupStatement),
        typeof(CreateAggregateStatement), typeof(CreateAssemblyStatement), typeof(CreateAvailabilityGroupStatement),
        typeof(CreateBrokerPriorityStatement), typeof(CreateContractStatement), typeof(CreateCryptographicProviderStatement),
        typeof(CreateDefaultStatement), typeof(CreateEventNotificationStatement), typeof(CreateEventSessionStatement),
        typeof(CreateExternalDataSourceStatement), typeof(CreateExternalFileFormatStatement), typeof(CreateExternalFunctionStatement),
        typeof(CreateExternalLanguageStatement), typeof(CreateExternalLibraryStatement), typeof(CreateExternalModelStatement),
        typeof(CreateExternalResourcePoolStatement), typeof(CreateExternalStreamingJobStatement), typeof(CreateExternalStreamStatement),
        typeof(CreateExternalTableStatement), typeof(CreateFederationStatement), typeof(CreateFullTextCatalogStatement),
        typeof(CreateFullTextIndexStatement), typeof(CreateFullTextStopListStatement), typeof(CreateJsonIndexStatement),
        typeof(CreateMessageTypeStatement), typeof(CreatePartitionFunctionStatement), typeof(CreatePartitionSchemeStatement),
        typeof(CreateOrAlterExternalFunctionStatement),
        typeof(CreateQueueStatement), typeof(CreateRemoteServiceBindingStatement), typeof(CreateResourcePoolStatement),
        typeof(CreateRouteStatement), typeof(CreateRuleStatement), typeof(CreateSearchPropertyListStatement),
        typeof(CreateSecurityPolicyStatement), typeof(CreateSelectiveXmlIndexStatement), typeof(CreateSemanticIndexStatement),
        typeof(CreateServiceStatement), typeof(CreateSpatialIndexStatement), typeof(CreateTypeUdtStatement),
        typeof(CreateVectorIndexStatement), typeof(CreateXmlIndexStatement), typeof(CreateWorkloadClassifierStatement), typeof(CreateWorkloadGroupStatement),
        typeof(CreateXmlSchemaCollectionStatement), typeof(DropAggregateStatement), typeof(DropAssemblyStatement),
        typeof(DropAvailabilityGroupStatement), typeof(DropBrokerPriorityStatement), typeof(DropContractStatement),
        typeof(DropCryptographicProviderStatement), typeof(DropDefaultStatement), typeof(DropEndpointStatement),
        typeof(DropEventNotificationStatement), typeof(DropEventSessionStatement), typeof(DropExternalDataSourceStatement),
        typeof(DropExternalFileFormatStatement), typeof(DropExternalLanguageStatement), typeof(DropExternalLibraryStatement),
        typeof(DropExternalModelStatement), typeof(DropExternalResourcePoolStatement), typeof(DropExternalStreamingJobStatement),
        typeof(DropExternalStreamStatement), typeof(DropExternalTableStatement), typeof(DropFederationStatement),
        typeof(DropFullTextCatalogStatement), typeof(DropFullTextIndexStatement), typeof(DropFullTextStopListStatement),
        typeof(DropPartitionFunctionStatement), typeof(DropPartitionSchemeStatement), typeof(DropQueueStatement),
        typeof(DropRemoteServiceBindingStatement), typeof(DropResourcePoolStatement), typeof(DropRouteStatement),
        typeof(DropRuleStatement), typeof(DropMessageTypeStatement), typeof(DropSearchPropertyListStatement), typeof(DropServiceStatement),
        typeof(DropWorkloadClassifierStatement), typeof(DropWorkloadGroupStatement), typeof(DropXmlSchemaCollectionStatement),
        typeof(EnableDisableTriggerStatement), typeof(EventSessionStatement), typeof(ExternalResourcePoolStatement),
        typeof(IndexDefinition), typeof(RenameEntityStatement), typeof(ResourcePoolStatement),

        // Cursor, conversation, transaction, and other procedural commands remain unsupported.
        typeof(BeginConversationTimerStatement), typeof(BeginDialogStatement), typeof(BeginTransactionStatement),
        typeof(BreakStatement), typeof(CloseCursorStatement), typeof(CloseMasterKeyStatement), typeof(CloseSymmetricKeyStatement),
        typeof(CommitTransactionStatement), typeof(ContinueStatement), typeof(DeallocateCursorStatement),
        typeof(DeclareCursorStatement), typeof(EndConversationStatement), typeof(FetchCursorStatement),
        typeof(GetConversationGroupStatement), typeof(GoToStatement), typeof(KillQueryNotificationSubscriptionStatement),
        typeof(KillStatsJobStatement), typeof(LabelStatement), typeof(MoveConversationStatement), typeof(OpenCursorStatement),
        typeof(OpenMasterKeyStatement), typeof(OpenSymmetricKeyStatement), typeof(PredicateSetStatement), typeof(PrintStatement),
        typeof(RaiseErrorLegacyStatement), typeof(RaiseErrorStatement), typeof(ReadTextStatement), typeof(ReceiveStatement),
        typeof(ReturnStatement), typeof(RevertStatement), typeof(RollbackTransactionStatement), typeof(SaveTransactionStatement),
        typeof(SendStatement), typeof(SetCommandStatement), typeof(SetErrorLevelStatement), typeof(SetIdentityInsertStatement),
        typeof(SetOffsetsStatement), typeof(SetRowCountStatement), typeof(SetStatisticsStatement), typeof(SetTextSizeStatement),
        typeof(SetTransactionIsolationLevelStatement), typeof(SetUserStatement), typeof(ThrowStatement), typeof(TryCatchStatement),
        typeof(UpdateTextStatement), typeof(UseFederationStatement), typeof(WaitForStatement), typeof(WhileStatement),
        typeof(WriteTextStatement), typeof(LineNoStatement),

        // Engine-specific and parser-snippet nodes without an explicit safe classification.
        typeof(BulkInsertStatement), typeof(CheckpointStatement), typeof(CopyStatement), typeof(DiskStatement),
        typeof(InsertBulkStatement), typeof(SelectStatementSnippet), typeof(TSqlStatementSnippet),
    };

    [Fact]
    public void Every_concrete_TableReference_type_is_classified()
    {
        var concrete = ConcreteSubclasses(typeof(TableReference));
        Assert.NotEmpty(concrete);

        var unclassified = concrete
            .Where(type => !SqlSafetyClassifier.AllowedTableReferenceTypes.Contains(type)
                && !KnownDeniedTableReferenceTypes.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Unclassified TableReference types: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void Every_concrete_TSqlStatement_type_has_an_explicit_mutation_decision()
    {
        var concrete = ConcreteSubclasses(typeof(TSqlStatement));
        Assert.NotEmpty(concrete);

        var unclassified = concrete
            .Where(type => !IsMapped(type, SqlMutationStatementMap.SchemaStatementTypes)
                && !IsMapped(type, SqlMutationStatementMap.PrivilegeStatementTypes)
                && !IsMapped(type, SqlMutationStatementMap.RejectedStatementTypes)
                && !KnownQueryOrOpaqueStatementTypes.Contains(type)
                && !KnownDeniedByOmission.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "Unclassified TSqlStatement types: " + string.Join(", ", unclassified));
    }

    private static bool IsMapped(Type type, IReadOnlySet<Type> types) =>
        types.Any(type.IsAssignableTo);

    [Fact]
    public void Every_AI_or_external_call_expression_type_is_denied()
    {
        // 018: Visit(PrimaryExpression) is the Step 4 hook, so the scan uses that base.
        var reflected = ConcreteSubclasses(typeof(PrimaryExpression))
            .Where(type => type.Name.StartsWith("AI", StringComparison.Ordinal)
                || type.Name.Contains("External", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(reflected);

        var missing = reflected
            .Where(type => !SqlSafetyClassifier.DeniedExpressionTypes.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "AI or external call expression types not in DeniedExpressionTypes: " + string.Join(", ", missing));
    }

    // 018: ParserType must be the highest TSql<digits>Parser in the package.
    [Fact]
    public void The_parser_is_the_newest_in_the_package()
    {
        var parsers = typeof(TSqlFragment).Assembly
            .GetTypes()
            .Select(type => (Type: type, Version: ParserVersion(type.Name)))
            .Where(item => item.Version is not null)
            .Select(item => (item.Type, Version: item.Version!.Value))
            .ToArray();
        Assert.NotEmpty(parsers);

        var newest = parsers.MaxBy(item => item.Version).Type;
        Assert.Equal(newest, SqlServerDocument.ParserType);
    }

    private static Type[] ConcreteSubclasses(Type baseType) =>
        typeof(TSqlFragment).Assembly
            .GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && baseType.IsAssignableFrom(type))
            .ToArray();

    // 018: type names matching TSql\d+Parser. The digits are the parser generation.
    private static int? ParserVersion(string name)
    {
        const string prefix = "TSql";
        const string suffix = "Parser";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal)
            || name.Length <= prefix.Length + suffix.Length)
        {
            return null;
        }

        var digits = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : null;
    }
}