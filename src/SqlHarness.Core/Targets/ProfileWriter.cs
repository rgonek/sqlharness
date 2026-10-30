using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;

using SqlHarness.Core.Auth;
using SqlHarness.Core.Postgres;

namespace SqlHarness.Core.Targets;

public enum ProfileWriteStatus { Success, Invalid, Conflict, NotFound, IoError, SecurityMetadata }
public sealed record ProfileFieldError(string Field, string Message);
public sealed record ProfileWriteResult(ProfileWriteStatus Status, string? Revision = null, IReadOnlyDictionary<string, TargetProfile>? Profiles = null, ProfileFieldError? Error = null);

public static class ProfileWriter
{
    public static bool EditingSupported => OperatingSystem.IsWindows();
    public const string EditingUnavailableReason = "Profile editing is available only on Windows.";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static ProfileWriteResult Update(string path, string name, string expectedRevision, TargetProfile profile)
        => UpdateCore(path, name, expectedRevision, profile, CancellationToken.None, null, EditingSupported);

    public static ProfileWriteResult Update(string path, string name, string expectedRevision, TargetProfile profile, CancellationToken cancellationToken)
        => UpdateCore(path, name, expectedRevision, profile, cancellationToken, null, EditingSupported);

    internal static ProfileWriteResult UpdateWithTestHook(string path, string name, string expectedRevision, TargetProfile profile, Action onTempCreated, CancellationToken cancellationToken = default, bool? editingSupported = null)
        => UpdateCore(path, name, expectedRevision, profile, cancellationToken, onTempCreated, editingSupported ?? EditingSupported);

    private static ProfileWriteResult UpdateCore(string path, string name, string expectedRevision, TargetProfile profile, CancellationToken cancellationToken, Action? onTempCreated, bool editingSupported)
    {
        if (!editingSupported) return new(ProfileWriteStatus.SecurityMetadata);
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? temp = null;
            try
            {
                byte[] original;
                try { original = File.ReadAllBytes(path); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(ProfileWriteStatus.Invalid); }
                IReadOnlyDictionary<string, TargetProfile> current;
                try { current = ProfileStore.ParseForEditing(original); }
                catch (Exception e) when (e is JsonException or NotSupportedException or SqlHarnessSafetyException) { return new(ProfileWriteStatus.Invalid); }
                if (Revision(original) != expectedRevision) return new(ProfileWriteStatus.Conflict);
                if (!current.ContainsKey(name)) return new(ProfileWriteStatus.NotFound);
                var validationError = ValidateProfile(profile);
                if (validationError is not null) return new(ProfileWriteStatus.Invalid, Error: validationError);
                var reader = new Utf8JsonReader(original);
                reader.Read();
                long start = 0, end = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var property = reader.GetString();
                    reader.Read();
                    var position = reader.TokenStartIndex;
                    reader.Skip();
                    if (property == name) { start = position; end = reader.BytesConsumed; break; }
                }
                if (end == 0) return new(ProfileWriteStatus.NotFound);
                var replacement = JsonSerializer.SerializeToUtf8Bytes(profile, Options);
                var candidate = new byte[checked((int)(original.Length - (end - start) + replacement.Length))];
                original.AsSpan(0, (int)start).CopyTo(candidate);
                replacement.CopyTo(candidate.AsSpan((int)start));
                original.AsSpan((int)end).CopyTo(candidate.AsSpan((int)start + replacement.Length));
                IReadOnlyDictionary<string, TargetProfile> savedProfiles;
                try { savedProfiles = ProfileStore.ParseForEditing(candidate); }
                catch (Exception e) when (e is JsonException or NotSupportedException or SqlHarnessSafetyException) { return new(ProfileWriteStatus.Invalid); }
                temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".sqlharness-{Guid.NewGuid():N}.tmp");
                using (var stream = CreateProtectedTemp(path, temp)) { onTempCreated?.Invoke(); stream.Write(candidate); stream.Flush(true); }
                if (Revision(File.ReadAllBytes(path)) != expectedRevision) return new(ProfileWriteStatus.Conflict);
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temp, path, null);
                temp = null;
                return new(ProfileWriteStatus.Success, Revision(candidate), savedProfiles);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(ProfileWriteStatus.IoError); }
            finally { if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
    }

    private static FileStream CreateProtectedTemp(string path, string temp)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        return new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    internal static ProfileFieldError? ValidateProfile(TargetProfile profile)
    {
        if (profile is null) return new("profile", "Profile is required.");
        if (string.IsNullOrWhiteSpace(profile.Server)) return new("server", "Server is required.");
        if (string.IsNullOrWhiteSpace(profile.Database)) return new("database", "Database is required.");
        SqlEngine engine;
        try { engine = SqlEngineNames.Parse(profile.Engine); }
        catch (SqlHarnessSafetyException) { return new("engine", "Invalid engine."); }
        AuthSpec auth;
        try { auth = AuthSpec.Parse(profile.Auth, profile.SqlUser, profile.PasswordEnvVar, profile.TrustServerCertificate); }
        catch (SqlHarnessSafetyException) { return new("auth", "Invalid authentication settings."); }
        if (engine == SqlEngine.Postgres && auth.Strategy != AuthStrategy.Sql) return new("auth", "Postgres requires SQL authentication.");
        try { PostgresTransportPolicy.Resolve(engine, profile.TrustServerCertificate, profile.SslMode, profile.RootCertificate); }
        catch (SqlHarnessSafetyException) { return new("sslMode", "Invalid TLS settings."); }
        try { ProfileStore.ValidateMutations(profile); }
        catch (JsonException) { return new("mutations", "Invalid mutation settings."); }
        if (profile.Vars is null) return new("vars", "Variable rules are required.");
        var template = profile.Database;
        foreach (var (key, pattern) in profile.Vars)
        {
            if (string.IsNullOrWhiteSpace(key) || pattern is null) return new("vars", "Invalid variable rule.");
            try { _ = TargetResolver.CompileVariableRule(pattern); }
            catch (ArgumentException) { return new("vars", "Invalid variable rule."); }
            template = template.Replace($"{{{key}}}", "", StringComparison.Ordinal);
        }
        if (TargetResolver.HasUnresolvedPlaceholder(template)) return new("database", "Database contains an unresolved placeholder.");
        return null;
    }
}