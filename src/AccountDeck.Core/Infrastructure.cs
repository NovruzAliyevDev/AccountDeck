using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string cipher);
}

/// <summary>Encrypts saved sign-ins with Windows DPAPI, so only this Windows user can read them.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AccountDeck.v1");

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string cipher) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
}

public sealed class StateStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppState Load()
    {
        if (!File.Exists(paths.StateFile)) return new AppState();
        try
        {
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(paths.StateFile), Options) ?? new AppState();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection instead of silently losing it.
            File.Copy(paths.StateFile, paths.StateFile + ".corrupt", true);
            return new AppState();
        }
    }

    public void Save(AppState state)
    {
        if (state.Log.Count > 200) state.Log.RemoveRange(0, state.Log.Count - 200);
        FileUtil.WriteAtomic(paths.StateFile, JsonSerializer.Serialize(state, Options));
    }
}

public static class FileUtil
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes through a temp file so a crash never leaves a half-written file behind.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".accountdeck-tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static JsonObject? ReadJsonObject(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Lets tests skip the waiting between retries.</summary>
    public static int? RetryDelayOverrideMs { get; set; }

    /// <summary>
    /// Renames a folder, retrying while another program still holds files in it
    /// (a closing app can keep handles open for a few seconds).
    /// </summary>
    public static void MoveDirectory(string from, string to, int attempts = 24, int delayMs = 500)
    {
        if (RetryDelayOverrideMs is { } d) delayMs = d;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i >= attempts)
                    throw new DeckException(
                        "Another program is still using Claude's files, so the account could not be switched. " +
                        "Close any terminals or editors that were opened from Claude and try again.", e);
                Thread.Sleep(delayMs);
            }
        }
    }

    public static string Sha256Short(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}
