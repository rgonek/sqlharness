using System.Security.AccessControl;
using System.Text;

using SqlHarness.Core.Targets;

namespace SqlHarness.Tests.Targets;

public sealed class ProfileWriterTests
{
    [Fact]
    public void Update_preserves_sibling_and_rejects_stale_revision()
    {
        var path = Path.GetTempFileName();
        const string original = "{\"a\":{\"server\":\"old\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"},  \"b\": { \"server\":\"sibling\", \"database\":\"d\",\"vars\":{},\"auth\":\"integrated\" }}";
        try
        {
            File.WriteAllText(path, original, new UTF8Encoding(false));
            var revision = ProfileWriter.Revision(File.ReadAllBytes(path));
            var profile = new TargetProfile("new", "d", new Dictionary<string, string>(), "integrated", Mutations: new(Writer: new(SameAsReader: true)));
            var saved = ProfileWriter.Update(path, "a", revision, profile);
            Assert.Equal(ProfileWriteStatus.Success, saved.Status);
            Assert.Equal("new", ProfileStore.Load(path)["a"].Server);
            Assert.Contains("  \"b\": { \"server\":\"sibling\", \"database\":\"d\",\"vars\":{},\"auth\":\"integrated\" }", File.ReadAllText(path));
            Assert.Equal(ProfileWriteStatus.Conflict, ProfileWriter.Update(path, "a", revision, profile).Status);
            Assert.Equal(saved.Revision, ProfileWriter.Revision(File.ReadAllBytes(path)));
            File.WriteAllText(path, "{}");
            Assert.Equal("new", saved.Profiles!["a"].Server);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_original_and_invalid_auth_do_not_write()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{broken");
            var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "sql");
            Assert.Equal(ProfileWriteStatus.Invalid, ProfileWriter.Update(path, "a", ProfileWriter.Revision(File.ReadAllBytes(path)), profile).Status);
            Assert.Equal("{broken", File.ReadAllText(path));
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllText(path);
            Assert.Equal(ProfileWriteStatus.Invalid, ProfileWriter.Update(path, "a", ProfileWriter.Revision(File.ReadAllBytes(path)), profile).Status);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_document_and_missing_profile_leave_document_untouched()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "integrated");
        Assert.Equal(ProfileWriteStatus.Invalid, ProfileWriter.Update(path, "a", "revision", profile).Status);
        try
        {
            File.WriteAllText(path, "{\"b\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(ProfileWriteStatus.NotFound, ProfileWriter.Update(path, "a", ProfileWriter.Revision(bytes), profile).Status);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cancelled_save_does_not_change_original()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), new("new", "d", new Dictionary<string, string>(), "integrated"), cancellation.Token));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Locked_destination_preserves_original_and_cleans_temp_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "targets.json");
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), new("new", "d", new Dictionary<string, string>(), "integrated"));
            Assert.Equal(ProfileWriteStatus.IoError, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("sql", "d", "[", "integrated", null, "vars")]
    [InlineData("sql", "d_{missing}", "x", "integrated", null, "database")]
    [InlineData("postgres", "d", "x", "integrated", null, "auth")]
    [InlineData("sqlserver", "d", "x", "integrated", "require", "sslMode")]
    public void Invalid_configuration_reports_the_field(string engine, string database, string rule, string auth, string? sslMode, string field)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            var profile = new TargetProfile("s", database, new Dictionary<string, string> { ["v"] = rule }, auth, Engine: engine == "sql" ? "sqlserver" : engine, SslMode: sslMode);
            var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), profile);
            Assert.Equal(ProfileWriteStatus.Invalid, result.Status);
            Assert.Equal(field, result.Error?.Field);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Failure_after_temp_creation_keeps_original_and_cleans_up()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "targets.json");
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            var profile = new TargetProfile("new", "d", new Dictionary<string, string>(), "integrated");
            var result = ProfileWriter.UpdateWithTestHook(path, "a", ProfileWriter.Revision(before), profile, () => throw new IOException("private marker"));
            Assert.Equal(ProfileWriteStatus.IoError, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Cancellation_after_temp_creation_cleans_up()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "targets.json");
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            using var cancellation = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => ProfileWriter.UpdateWithTestHook(path, "a", ProfileWriter.Revision(before), new("new", "d", new Dictionary<string, string>(), "integrated"), cancellation.Cancel, cancellation.Token));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"a\":{\"server\":\"s\",\"server\":\"t\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}")]
    [InlineData("{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\",\"unknown\":1}}")]
    public void Invalid_source_document_cannot_be_repaired_by_save(string source)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, source);
            var before = File.ReadAllBytes(path);
            var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), new("s", "d", new Dictionary<string, string>(), "integrated"));
            Assert.Equal(ProfileWriteStatus.Invalid, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_mutation_configuration_reports_mutations_and_never_writes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            var before = File.ReadAllBytes(path);
            var profile = new TargetProfile("s", "d", new Dictionary<string, string>(), "integrated", Mutations: new(Mode: "operator"));
            var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), profile);
            Assert.Equal(ProfileWriteStatus.Invalid, result.Status);
            Assert.Equal("mutations", result.Error?.Field);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"auth\":\"bogus\",\"database\":\"d\",\"vars\":{}")]
    [InlineData("\"auth\":\"integrated\",\"database\":\"d\",\"vars\":{},\"sslMode\":\"require\"")]
    [InlineData("\"auth\":\"integrated\",\"database\":\"d\",\"vars\":{\"v\":\"[\"}")]
    [InlineData("\"auth\":\"integrated\",\"database\":\"d_{missing}\",\"vars\":{}")]
    public void Invalid_sibling_blocks_save_and_preserves_bytes(string invalidFields)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"},\"b\":{\"server\":\"s\"," + invalidFields + "}}");
            var before = File.ReadAllBytes(path);
            var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(before), new("new", "d", new Dictionary<string, string>(), "integrated"));
            Assert.Equal(ProfileWriteStatus.Invalid, result.Status);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_preserves_restricted_file_permissions()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"a\":{\"server\":\"s\",\"database\":\"d\",\"vars\":{},\"auth\":\"integrated\"}}");
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(path);
                var security = info.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
                info.SetAccessControl(security);
                var original = info.GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
                var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(File.ReadAllBytes(path)), new("new", "d", new Dictionary<string, string>(), "integrated"));
                Assert.Equal(ProfileWriteStatus.Success, result.Status);
                Assert.Equal(original, info.GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access));
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var result = ProfileWriter.Update(path, "a", ProfileWriter.Revision(File.ReadAllBytes(path)), new("new", "d", new Dictionary<string, string>(), "integrated"));
                Assert.Equal(ProfileWriteStatus.Success, result.Status);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally { File.Delete(path); }
    }
}