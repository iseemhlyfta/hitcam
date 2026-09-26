using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HitCam.Core.Pairing;

public sealed record PairedDevice(string DeviceId, string DeviceName, string TokenHash, DateTimeOffset PairedAt, DateTimeOffset LastSeen);

/// <summary>Remembers paired phones. Only SHA-256 hashes of their tokens are kept.</summary>
public interface IPairingStore
{
    IReadOnlyList<PairedDevice> Devices { get; }

    /// <summary>Creates a new token for the device, replacing any earlier one, and returns it in plain text.</summary>
    string Pair(string deviceId, string deviceName);

    bool Verify(string deviceId, string? token);

    /// <summary>Records that the device connected. Best effort: never throws on storage errors.</summary>
    void Touch(string deviceId);

    bool Remove(string deviceId);
}

public class InMemoryPairingStore(TimeProvider? timeProvider = null) : IPairingStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private PairedDevice[] _items = [];

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_gate) return Array.AsReadOnly(_items); }
    }

    public string Pair(string deviceId, string deviceName)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        Update(items => [.. items.Where(d => d.DeviceId != deviceId), new PairedDevice(deviceId, deviceName, Hash(token), now, now)]);
        return token;
    }

    public bool Verify(string deviceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        PairedDevice? device;
        lock (_gate)
            device = Array.Find(_items, d => d.DeviceId == deviceId);
        if (device is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(device.TokenHash),
            Encoding.ASCII.GetBytes(Hash(token)));
    }

    public void Touch(string deviceId)
    {
        var now = _time.GetUtcNow();
        try
        {
            Update(items => Array.Exists(items, d => d.DeviceId == deviceId)
                ? Array.ConvertAll(items, d => d.DeviceId == deviceId ? d with { LastSeen = now } : d)
                : items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only the "last seen" time is lost; the phone must still be able to connect.
            Trace.TraceWarning($"HitCam: could not save the pairing store: {ex.Message}");
        }
    }

    public bool Remove(string deviceId)
    {
        var removed = false;
        Update(items =>
        {
            removed = Array.Exists(items, d => d.DeviceId == deviceId);
            return removed ? Array.FindAll(items, d => d.DeviceId != deviceId) : items;
        });
        return removed;
    }

    /// <summary>Replaces the list without saving; for loading persisted devices.</summary>
    protected void Restore(IEnumerable<PairedDevice> devices)
    {
        lock (_gate)
            _items = [.. devices];
    }

    /// <summary>
    /// Persists the new list. Called under the lock before it replaces the current one,
    /// so a failure leaves the store unchanged.
    /// </summary>
    protected virtual void Save(IReadOnlyList<PairedDevice> devices)
    {
    }

    private void Update(Func<PairedDevice[], PairedDevice[]> change)
    {
        lock (_gate)
        {
            var next = change(_items);
            if (ReferenceEquals(next, _items))
                return;
            Save(next);
            _items = next;
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>Pairing store persisted as JSON (e.g. %APPDATA%\HitCam\devices.json).</summary>
public sealed class FilePairingStore : InMemoryPairingStore
{
    private readonly string _path;

    public FilePairingStore(string path, TimeProvider? timeProvider = null) : base(timeProvider)
    {
        _path = path;
        try
        {
            if (!File.Exists(path))
                return;
            var devices = JsonSerializer.Deserialize(ReadAll(path), PairingJson.Default.PairedDeviceArray);
            if (devices is not null)
                Restore(devices.Where(d => d is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by another program (antivirus, sync) or no access: the paired phones are in there, so never save
            // an empty list over them. Pairing a new phone fails until the next start.
            Trace.TraceWarning($"HitCam: could not read the pairing store {path}: {ex.Message}");
            _unreadable = true;
        }
        catch (JsonException ex)
        {
            // Start empty: the phones only have to pair again. Keep the unreadable file for inspection,
            // since the next save replaces it.
            Trace.TraceWarning($"HitCam: could not read the pairing store {path}: {ex.Message}");
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private readonly bool _unreadable;

    /// <summary>The file's bytes, retried briefly while another process holds it.</summary>
    private static byte[] ReadAll(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    protected override void Save(IReadOnlyList<PairedDevice> devices)
    {
        if (_unreadable)
            throw new IOException($"{_path} could not be read at start; not replacing it");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        // Write a flushed temp file and swap it in, so a crash or power loss never leaves a half-written store.
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, devices.ToArray(), PairingJson.Default.PairedDeviceArray);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PairedDevice[]))]
internal sealed partial class PairingJson : JsonSerializerContext;
