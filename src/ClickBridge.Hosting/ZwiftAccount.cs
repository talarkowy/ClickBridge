using ClickBridge.Core.Logging;
using ClickBridge.Core.Zwift;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Security.Cryptography.DataProtection;

namespace ClickBridge.Hosting;

/// <summary>Manages the local Zwift session shared by the console and tray versions.</summary>
public sealed class ZwiftAccount(IBridgeLog log) : IDisposable
{
    private const string Tag = "ZWIFT";
    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickBridge", "zwift-session.dat");

    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 64 * 1024,
    };
    private readonly Lock _sessionLock = new();
    private volatile ZwiftUnlockClient? _client;
    private bool _disposed;

    public ZwiftUnlockClient? Client => _client;

    public async Task LoadAsync()
    {
        try
        {
            var encrypted = await File.ReadAllBytesAsync(SessionPath).ConfigureAwait(false);
            var decrypted = await new DataProtectionProvider().UnprotectAsync(encrypted.AsBuffer()).AsTask().ConfigureAwait(false);
            var token = Encoding.UTF8.GetString(decrypted.ToArray());
            if (!string.IsNullOrWhiteSpace(token))
            {
                _client = CreateClient(token);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            log.Warn(Tag, $"Could not load the saved Zwift session ({ex.GetType().Name}). Sign in again.");
        }
    }

    /// <returns>Whether the new session could be saved for the next launch.</returns>
    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        var client = CreateClient(refreshToken: null);
        var token = await client.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        return await StoreAsync(client, token, replace: true, cancellationToken).ConfigureAwait(false);
    }

    public void SignOut()
    {
        lock (_sessionLock)
        {
            ForgetSavedSession();
            _client = null;
        }
    }

    public static void ForgetSavedSession()
    {
        try
        {
            File.Delete(SessionPath);
        }
        catch (DirectoryNotFoundException) { }
    }

    private ZwiftUnlockClient CreateClient(string? refreshToken)
    {
        ZwiftUnlockClient? client = null;
        client = new ZwiftUnlockClient(_http, refreshToken, token => token is null
            ? ForgetRejectedAsync(client!)
            : StoreAsync(client!, token, replace: false, CancellationToken.None));
        return client;
    }

    /// <summary>Zwift rejected the saved session: remove it so the next launch does not load a dead token.</summary>
    private Task ForgetRejectedAsync(ZwiftUnlockClient client)
    {
        lock (_sessionLock)
        {
            if (_disposed || _client != client)
            {
                return Task.CompletedTask;
            }

            try
            {
                ForgetSavedSession();
                log.Warn(Tag, "Zwift rejected the saved session. It was removed; sign in again.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn(Tag, $"Could not remove the rejected Zwift session ({ex.GetType().Name}).");
            }
        }

        return Task.CompletedTask;
    }

    private async Task<bool> StoreAsync(ZwiftUnlockClient client, string token, bool replace, CancellationToken cancellationToken)
    {
        byte[]? protectedBytes = null;
        string? error = null;
        try
        {
            var encrypted = await new DataProtectionProvider("LOCAL=user").ProtectAsync(Encoding.UTF8.GetBytes(token).AsBuffer())
                .AsTask(cancellationToken).ConfigureAwait(false);
            protectedBytes = encrypted.ToArray();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            error = ex.GetType().Name;
        }

        lock (_sessionLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A refresh for an old account must never overwrite a newer sign-in or recreate a deleted session.
            if (_disposed || (!replace && _client != client))
            {
                return false;
            }

            if (protectedBytes is not null)
            {
                error = WriteProtectedSession(protectedBytes);
            }

            if (error is not null)
            {
                log.Warn(Tag, $"Could not save the Zwift session ({error}). The new token is available for this run.");
            }

            if (replace)
            {
                _client = client;
            }

            return error is null;
        }
    }

    private static string? WriteProtectedSession(byte[] bytes)
    {
        var tempPath = SessionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, SessionPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ex.GetType().Name;
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            _disposed = true;
            _client = null;
        }

        _http.Dispose();
    }
}
