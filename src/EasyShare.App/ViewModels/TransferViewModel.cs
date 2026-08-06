using System.ComponentModel;
using System.Runtime.CompilerServices;
using EasyShare.Core.Models;

namespace EasyShare.App.ViewModels;

public class TransferViewModel : INotifyPropertyChanged
{
    private string _fileName = string.Empty;
    private double _progress;
    private string _speedText = string.Empty;
    private string _statusText = string.Empty;
    private TransferStatus _status;
    private bool _canCancel;

    public Action? CancelAction { get; set; }

    public bool CanCancel
    {
        get => _canCancel;
        set => SetProperty(ref _canCancel, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public TransferStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

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
