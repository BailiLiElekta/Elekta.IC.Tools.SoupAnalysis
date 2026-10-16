using System.Text.Json;
using Elekta.IC.Tools.SoupAnalysis.Application;
using Elekta.IC.Tools.SoupAnalysis.Domain;

namespace Elekta.IC.Tools.SoupAnalysis.Infrastructure.Configuration;

public sealed class SettingsStore(string settingsPath) : ISettingsStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string? RecoveryNotice { get; private set; }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            RecoveryNotice = null;
            if (!File.Exists(settingsPath) && !File.Exists(settingsPath + ".bak")) return UserSettings.Default;
            try { return await ReadAsync(settingsPath, cancellationToken); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException)
            {
                if (!File.Exists(settingsPath + ".bak"))
                    throw new InvalidDataException("Settings could not be read and no last-good backup is available.");
                var recovered = await ReadAsync(settingsPath + ".bak", cancellationToken);
                RecoveryNotice = "User settings were recovered from the last-good backup; the latest preferences may have been lost.";
                return recovered;
            }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        settings = ConfigurationJson.Validate(settings);
        await gate.WaitAsync(cancellationToken);
        var temporary = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupTemporary = temporary + ".bak";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, ConfigurationJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var validCurrent = false;
            if (File.Exists(settingsPath))
            {
                try { _ = await ReadAsync(settingsPath, cancellationToken); validCurrent = true; }
                catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { }
                File.Replace(temporary, settingsPath, validCurrent ? settingsPath + ".bak" : null);
            }
            else
            {
                if (!File.Exists(settingsPath + ".bak"))
                {
                    await File.WriteAllBytesAsync(backupTemporary, await File.ReadAllBytesAsync(temporary, cancellationToken), cancellationToken);
                    File.Move(backupTemporary, settingsPath + ".bak");
                }
                File.Move(temporary, settingsPath);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(backupTemporary)) File.Delete(backupTemporary);
            gate.Release();
        }
    }

    private static async Task<UserSettings> ReadAsync(string path, CancellationToken token) =>
        ConfigurationJson.Validate(ConfigurationJson.Read<UserSettings>(await File.ReadAllBytesAsync(path, token)));
}