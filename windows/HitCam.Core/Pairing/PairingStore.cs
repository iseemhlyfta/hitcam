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

    void Touch(string deviceId);

    bool Remove(string deviceId);
}

public class InMemoryPairingStore(TimeProvider? timeProvider = null) : IPairingStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    protected List<PairedDevice> Items { get; } = [];

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_gate) return Items.ToArray(); }
    }

    public string Pair(string deviceId, string deviceName)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Items.RemoveAll(d => d.DeviceId == deviceId);
            Items.Add(new PairedDevice(deviceId, deviceName, Hash(token), now, now));
            Save();
        }
        return token;
    }

    public bool Verify(string deviceId, string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        PairedDevice? device;
        lock (_gate)
            device = Items.Find(d => d.DeviceId == deviceId);
        if (device is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(device.TokenHash),
            Encoding.ASCII.GetBytes(Hash(token)));
    }

    public void Touch(string deviceId)
    {
        lock (_gate)
        {
            var index = Items.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0)
                return;
            Items[index] = Items[index] with { LastSeen = _time.GetUtcNow() };
            Save();
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            var removed = Items.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    /// <summary>Called under the lock after every change.</summary>
    protected virtual void Save()
    {
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
        if (!File.Exists(path))
            return;
        try
        {
            using var stream = File.OpenRead(path);
            var devices = JsonSerializer.Deserialize(stream, PairingJson.Default.PairedDeviceArray);
            if (devices is not null)
                Items.AddRange(devices);
        }
        catch (JsonException)
        {
            // A corrupt file only means the phones have to pair again.
        }
    }

    protected override void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(Items.ToArray(), PairingJson.Default.PairedDeviceArray));
        File.Move(temp, _path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PairedDevice[]))]
internal sealed partial class PairingJson : JsonSerializerContext;
