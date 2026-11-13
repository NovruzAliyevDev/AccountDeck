using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AccountDeck.Core;

/// <summary>Where a tool keeps its sign-in tokens: a JSON document that can be read and replaced.</summary>
public interface ICredentialStore
{
    JsonObject? Read();
    void Write(JsonObject credentials);
    string Describe { get; }
}

/// <summary>Tokens in a JSON file (Claude Code on Windows and Linux, and every test).</summary>
public sealed class FileCredentialStore(string path) : ICredentialStore
{
    public JsonObject? Read() => FileUtil.ReadJsonObject(path);
    public void Write(JsonObject credentials) => FileUtil.WriteAtomic(path, credentials.ToJsonString(FileUtil.Pretty));
    public string Describe => path;
}

/// <summary>
/// Claude Code on macOS keeps its tokens in the login Keychain (generic password, service
/// "Claude Code-credentials", account = the macOS user name). It falls back to ~/.claude/.credentials.json
/// when the Keychain is unavailable, so that file is read when there is no Keychain item, and kept in step when it exists.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacClaudeCredentialStore(AppPaths paths) : ICredentialStore
{
    public const string Service = "Claude Code-credentials";
    private static string Account => Environment.UserName;
    private readonly FileCredentialStore _file = new(paths.ClaudeCredentialsFile);

    public JsonObject? Read()
    {
        var raw = MacKeychain.Read(Service, Account);
        if (raw is not null)
        {
            try { if (JsonNode.Parse(raw) is JsonObject json) return json; }
            catch (System.Text.Json.JsonException) { }
        }
        return _file.Read();
    }

    public void Write(JsonObject credentials)
    {
        // Compact JSON: the Keychain item must stay a single line for `security -w` to return it as text.
        MacKeychain.Write(Service, Account, credentials.ToJsonString());
        if (File.Exists(paths.ClaudeCredentialsFile)) _file.Write(credentials);
    }

    public string Describe => $"Keychain item \"{Service}\" ({Account})";
}

/// <summary>Reads and writes generic passwords in the login Keychain through /usr/bin/security.</summary>
[SupportedOSPlatform("macos")]
public static class MacKeychain
{
    private const string Tool = "/usr/bin/security";

    /// <summary>The stored text, or null when there is no such item (or the Keychain refused).</summary>
    public static string? Read(string service, string account)
    {
        var (code, output) = Run(["find-generic-password", "-s", service, "-a", account, "-w"], stdin: null);
        if (code != 0) return null;
        var text = output.TrimEnd('\n', '\r');
        // `security -w` prints the value as hex when it contains anything that is not printable text.
        if (text.Length > 0 && text.Length % 2 == 0 && text.All(Uri.IsHexDigit))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromHexString(text));
                if (decoded.TrimStart().StartsWith('{')) return decoded;
            }
            catch (FormatException) { }
        }
        return text;
    }

    /// <summary>Creates or replaces the item, then reads it back to make sure the Keychain took it.</summary>
    public static void Write(string service, string account, string value)
    {
        // Interactive mode reads the command from stdin, so the secret never appears in the process list.
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
        var command = $"add-generic-password -U -s {Quote(service)} -a {Quote(account)} -X {hex}\n";
        Run(["-i"], command);
        if (Read(service, account) != value)
            throw new DeckException($"The macOS Keychain did not accept the sign-in for \"{service}\". " +
                                    "If macOS asked for permission, choose Always Allow and try again.");
    }

    public static bool Delete(string service, string account) =>
        Run(["delete-generic-password", "-s", service, "-a", account], stdin: null).Code == 0;

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static (int Code, string Output) Run(string[] args, string? stdin)
    {
        var info = new ProcessStartInfo(Tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(info)!;
            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }
}

/// <summary>AES-256-GCM with a key supplied by the platform. Output: base64(nonce | tag | ciphertext).</summary>
public sealed class AesGcmProtector(Func<byte[]> key) : ISecretProtector
{
    private const int NonceSize = 12, TagSize = 16;

    public string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key(), TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string cipher)
    {
        var input = Convert.FromBase64String(cipher);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("The saved sign-in is damaged.");
        var plain = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key(), TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// The macOS counterpart of DPAPI: saved sign-ins are encrypted with a random key that lives in this
/// user's login Keychain (service "AccountDeck"), so state.json alone is useless to anyone else.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacKeychainProtector
{
    public const string Service = "AccountDeck";
    private const string Account = "saved-sign-ins-key";

    public static ISecretProtector Create()
    {
        byte[]? cached = null;
        var gate = new object();
        return new AesGcmProtector(() =>
        {
            lock (gate)
            {
                if (cached is not null) return cached;
                if (MacKeychain.Read(Service, Account) is { } stored)
                {
                    try
                    {
                        var key = Convert.FromBase64String(stored);
                        if (key.Length == 32) return cached = key;
                    }
                    catch (FormatException) { }
                    throw new DeckException("The Account Deck key in your Keychain is damaged. Saved sign-ins cannot be read.");
                }
                var fresh = RandomNumberGenerator.GetBytes(32);
                MacKeychain.Write(Service, Account, Convert.ToBase64String(fresh));
                return cached = fresh;
            }
        });
    }
}
