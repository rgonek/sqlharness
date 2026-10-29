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
}
