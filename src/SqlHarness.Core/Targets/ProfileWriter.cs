using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlHarness.Core.Auth;

namespace SqlHarness.Core.Targets;

public enum ProfileWriteStatus { Success, Invalid, Conflict, NotFound, IoError }
public sealed record ProfileWriteResult(ProfileWriteStatus Status, string? Revision = null);

public static class ProfileWriter
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Revision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static ProfileWriteResult Update(string path, string name, string expectedRevision, TargetProfile profile, CancellationToken cancellationToken = default)
    {
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? temp = null;
            try
            {
                if (!File.Exists(path)) return new(ProfileWriteStatus.Invalid);
                var original = File.ReadAllBytes(path);
                IReadOnlyDictionary<string, TargetProfile> current;
                try { current = ProfileStore.Parse(original); }
                catch (Exception e) when (e is JsonException or NotSupportedException or SqlHarnessSafetyException) { return new(ProfileWriteStatus.Invalid); }
                if (Revision(original) != expectedRevision) return new(ProfileWriteStatus.Conflict);
                if (!current.ContainsKey(name)) return new(ProfileWriteStatus.NotFound);
                if (!ValidProfile(profile)) return new(ProfileWriteStatus.Invalid);
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
                try { ProfileStore.Parse(candidate); }
                catch (Exception e) when (e is JsonException or NotSupportedException or SqlHarnessSafetyException) { return new(ProfileWriteStatus.Invalid); }
                temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".sqlharness-{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(candidate); stream.Flush(true); }
                if (Revision(File.ReadAllBytes(path)) != expectedRevision) return new(ProfileWriteStatus.Conflict);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, path, true);
                temp = null;
                return new(ProfileWriteStatus.Success, Revision(candidate));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new(ProfileWriteStatus.IoError); }
            finally { if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
    }

    private static bool ValidProfile(TargetProfile profile)
    {
        try
        {
            if (profile is null || profile.Vars is null) return false;
            var auth = AuthSpec.Parse(profile.Auth, profile.SqlUser, profile.PasswordEnvVar, profile.TrustServerCertificate);
            if (SqlEngineNames.Parse(profile.Engine) == SqlEngine.Postgres && auth.Strategy != AuthStrategy.Sql) return false;
            foreach (var (key, pattern) in profile.Vars)
            {
                if (string.IsNullOrWhiteSpace(key) || pattern is null ) return false;
                _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            var template = profile.Database;
            foreach (var key in profile.Vars.Keys) template = template.Replace($"{{{key}}}", "", StringComparison.Ordinal);
            return !template.Contains('{') && !template.Contains('}');
        }
        catch (Exception e) when (e is ArgumentException or SqlHarnessSafetyException or NullReferenceException) { return false; }
    }
}
