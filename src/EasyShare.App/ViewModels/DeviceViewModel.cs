using System.ComponentModel;
using System.Runtime.CompilerServices;
using EasyShare.Core.Models;

namespace EasyShare.App.ViewModels;

public class DeviceViewModel : INotifyPropertyChanged
{
    private string _alias = string.Empty;
    private string _deviceModel = string.Empty;
    private string _ipAddress = string.Empty;
    private DeviceType _deviceType;
    private string _fingerprint = string.Empty;
    private int _port = 53317;
    private bool _isSelected;
    private DateTime _lastSeen = DateTime.MinValue;
    private bool _isOnline;
    private string _lastSeenText = string.Empty;
    private bool _isManual;
    private List<string> _allIpAddresses = [];
    private string _bluetoothAddress = "";
    private string _wifiDirectDeviceId = "";

    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string DeviceModel
    {
        get => _deviceModel;
        set => SetProperty(ref _deviceModel, value);
    }

    public string IpAddress
    {
        get => _ipAddress;
        set => SetProperty(ref _ipAddress, value);
    }

    public DeviceType DeviceType
    {
        get => _deviceType;
        set => SetProperty(ref _deviceType, value);
    }

    public string Fingerprint
    {
        get => _fingerprint;
        set => SetProperty(ref _fingerprint, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public DateTime LastSeen
    {
        get => _lastSeen;
        set => SetProperty(ref _lastSeen, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => SetProperty(ref _isOnline, value);
    }

    public string LastSeenText
    {
        get => _lastSeenText;
        set => SetProperty(ref _lastSeenText, value);
    }

    public bool IsManual
    {
        get => _isManual;
        set => SetProperty(ref _isManual, value);
    }

    public List<string> AllIpAddresses
    {
        get => _allIpAddresses;
        set => SetProperty(ref _allIpAddresses, value);
    }

    public string BluetoothAddress
    {
        get => _bluetoothAddress;
        set => SetProperty(ref _bluetoothAddress, value);
    }

    public bool HasBluetooth => !string.IsNullOrEmpty(_bluetoothAddress);
    public bool HasWifi => !string.IsNullOrEmpty(_ipAddress);

    public string WifiDirectDeviceId
    {
        get => _wifiDirectDeviceId;
        set => SetProperty(ref _wifiDirectDeviceId, value);
    }

    public bool HasWifiDirect => !string.IsNullOrEmpty(_wifiDirectDeviceId);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
