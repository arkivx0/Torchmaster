using System.Runtime.InteropServices.WindowsRuntime;
using Lanternly.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
namespace Lanternly.Win;

/// <summary>IGatt over WinRT Bluetooth LE.</summary>
public sealed class WinGatt : IGatt
{
    readonly BluetoothLEDevice _dev; readonly GattDeviceService _svc; readonly Dictionary<Guid, GattCharacteristic> _ch = [];
    public string Mac { get; }
    WinGatt(string mac, BluetoothLEDevice d, GattDeviceService s) { Mac = mac; _dev = d; _svc = s; }

    public static async Task<WinGatt> ConnectAsync(string mac)
    {
        var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(Convert.ToUInt64(mac.Replace(":", ""), 16))
                  ?? throw new IOException("bulb not found over Bluetooth");
        var r = await dev.GetGattServicesForUuidAsync(Ble.Service, BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success || r.Services.Count == 0) { dev.Dispose(); throw new IOException($"mesh service not available ({r.Status})"); }
        return new WinGatt(mac, dev, r.Services[0]);
    }

    async Task<GattCharacteristic> Ch(Guid id)
    {
        if (_ch.TryGetValue(id, out var c)) return c;
        var r = await _svc.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0) throw new IOException("characteristic missing");
        return _ch[id] = r.Characteristics[0];
    }

    public async Task WriteAsync(Guid ch, byte[] data, bool withResponse)
    {
        var r = await (await Ch(ch)).WriteValueWithResultAsync(data.AsBuffer(), withResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse);
        if (r.Status != GattCommunicationStatus.Success) throw new IOException($"BLE write failed ({r.Status})");
    }
    public async Task<byte[]> ReadAsync(Guid ch)
    {
        var r = await (await Ch(ch)).ReadValueAsync(BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success) throw new IOException($"BLE read failed ({r.Status})");
        return r.Value.ToArray();
    }
    public async Task SubscribeAsync(Guid ch, Action<byte[]> onNotify)
    {
        var c = await Ch(ch);
        c.ValueChanged += (_, e) => { try { onNotify(e.CharacteristicValue.ToArray()); } catch (Exception ex) { Program.Log(ex, "ble notify"); } };
        try { await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify); } catch (Exception ex) { Program.Log(ex, "ble cccd"); }
    }
    public ValueTask DisposeAsync() { _svc.Dispose(); _dev.Dispose(); return ValueTask.CompletedTask; }

    /// <summary>Listens briefly for advertisements and returns heard addresses (AA:BB:..) by signal strength.</summary>
    public static async Task<List<string>> HeardAsync(TimeSpan t)
    {
        var seen = new Dictionary<string, short>();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, e) => { var m = string.Join(':', BitConverter.GetBytes(e.BluetoothAddress).Take(6).Reverse().Select(b => b.ToString("X2"))); lock (seen) seen[m] = Math.Max(seen.GetValueOrDefault(m, short.MinValue), e.RawSignalStrengthInDBm); };
        w.Start(); await Task.Delay(t); w.Stop();
        lock (seen) return seen.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
    }

    /// <summary>Factory-reset Telink bulbs advertise the local name "telink_mesh1".</summary>
    public static async Task<List<string>> FindResetBulbsAsync(TimeSpan t)
    {
        var seen = new Dictionary<string, short>();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        w.Received += (_, e) =>
        {
            if (e.Advertisement.LocalName != Ble.FactoryName) return;
            var m = string.Join(':', BitConverter.GetBytes(e.BluetoothAddress).Take(6).Reverse().Select(b => b.ToString("X2")));
            lock (seen) seen[m] = e.RawSignalStrengthInDBm;
        };
        w.Start(); await Task.Delay(t); w.Stop();
        lock (seen) return seen.OrderByDescending(k => k.Value).Select(k => k.Key).ToList();
    }

    /// <summary>Provisions one reset bulb onto the mesh, then logs in with the new key to learn its mesh address.</summary>
    public static async Task<Bulb> AdoptAsync(string mac, Home target, string name)
    {
        await using (var g = await ConnectAsync(mac)) await BleSession.ProvisionAsync(g, target.MeshName, target.MeshPassword);
        await Task.Delay(1500);
        var bulb = new Bulb { Name = name, Mac = mac, Type = DeviceCaps.AllControls, MeshId = 0 };
        try
        {
            await using var g2 = await ConnectAsync(mac);
            await using var s = new BleSession(g2, target);
            var got = new TaskCompletionSource<ushort>();
            s.StateChanged += (_, st) => got.TrySetResult(st.MeshId);
            await s.LoginAsync();
            bulb.MeshId = await got.Task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch { /* address unknown: 0 = the directly connected node */ }
        return bulb;
    }

    /// <summary>Logs into the home's mesh through the nearest bulb that accepts the mesh key.</summary>
    public static async Task<BleSession> ConnectMeshAsync(Home h, Action<string> status)
    {
        var heard = await HeardAsync(TimeSpan.FromSeconds(4));
        var macs = h.Bulbs.Select(b => b.Mac.ToUpperInvariant()).Where(m => m.Length == 17).Distinct().ToList();
        var order = heard.Where(macs.Contains).Concat(macs.Where(m => !heard.Contains(m))).ToList();
        if (order.Count == 0) throw new IOException("no bulb MAC addresses in the key file");
        Exception? last = null;
        foreach (var mac in order.Take(5))
        {
            try
            {
                status($"Bluetooth: trying {h.Bulbs.First(b => b.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase)).Name}…");
                var g = await ConnectAsync(mac).WaitAsync(TimeSpan.FromSeconds(12));
                var s = new BleSession(g, h);
                try { await s.LoginAsync().WaitAsync(TimeSpan.FromSeconds(8)); return s; }
                catch { await s.DisposeAsync(); throw; }
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new IOException("no bulb answered over Bluetooth");
    }
}
