using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EasyShare.Core;
using EasyShare.Core.Services;
using EasyShare.App.ViewModels;
using EasyShare.Core.Config;
using EasyShare.Core.Crypto;
using EasyShare.Core.Discovery;
using EasyShare.Core.Models;
using EasyShare.Core.Security;
using EasyShare.Core.Sessions;
using EasyShare.Transport.FileTransfer;
using EasyShare.Transport.Server;
using EasyShare.Shell;
using EasyShare.App.Views;
using H.NotifyIcon;
using Microsoft.Win32;
using CoreProtocolType = EasyShare.Core.Models.ProtocolType;

namespace EasyShare.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private MulticastDiscovery? _discovery;
    private LocalSendServer? _server;
    private TrustStore? _trustStore;
    private SessionManager? _sessionManager;
    private TaskbarIcon? _trayIcon;

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<TransferViewModel> Transfers { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();

    private AppConfig _config = null!;
    private X509Certificate2? _certificate;
    private string _fingerprint = string.Empty;
    private readonly string _configPath;
    private bool _isCleanedUp;
    private UpdateService? _updateService;
    private readonly Dictionary<string, TransferViewModel> _receiveTransfers = new();
    private readonly CancellationTokenSource _cts = new();
    private UpdateInfo? _pendingUpdate;
    private DeviceViewModel? _selectedDevice;

    public MainWindow()
    {
        InitializeComponent();

        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyShare", "config.json");

        DeviceList.ItemsSource = Devices;
        TransferList.ItemsSource = Transfers;
        HistoryList.ItemsSource = History;

        LoadConfig();
        InitializeServices();
        SetupTrayIcon();
        StartServices();
        ApplyTheme(_config.Theme);
        CheckForUpdatesAsync();

        DropZone.DragOver += DropZone_DragOver;
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            else
            {
                _config = new AppConfig();
                SaveConfig();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EasyShare] LoadConfig failed: {ex.Message}"); _config = new AppConfig(); }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(_configPath, System.Text.Json.JsonSerializer.Serialize(_config));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[EasyShare] SaveConfig failed: {ex.Message}"); }
    }

    private void InitializeServices()
    {
        _certificate = TlsCertificate.LoadOrCreate();
        _fingerprint = TlsCertificate.GetFingerprint(_certificate);

        _trustStore = new TrustStore();
        _sessionManager = new SessionManager();
        _discovery = new MulticastDiscovery(_config.MulticastAddress, _config.MulticastPort);
        _server = new LocalSendServer(_certificate);

        _discovery.DeviceFound += OnDeviceFound;
        _discovery.DeviceLost += OnDeviceLost;
        _server.UploadRequested += OnUploadRequested;
        _server.UploadCompleted += OnUploadCompleted;

        RegisterContextMenu();
        StartNamedPipeListener();
    }

    private void RegisterContextMenu()
    {
        try
        {
            if (!ShellIntegration.IsRegistered())
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    ShellIntegration.Register(exePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EasyShare] Context menu registration failed: {ex.Message}");
        }
    }

    private void StartNamedPipeListener()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await NamedPipeServer.StartServerAsync(files =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            var target = _selectedDevice ?? Devices.FirstOrDefault();
                            if (target != null) SendFiles(files, target);
                        });
                    }, _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(1000); }
            }
        });
    }

    private void StartServices()
    {
        var self = BuildAnnouncement();
        _discovery?.Start(self);
        _server?.Start(_config.HttpPort, _config.DeviceAlias, _fingerprint, _config.DefaultSavePath);
        StatusText.Text = "Bereit";
    }

    private DeviceAnnouncement BuildAnnouncement() => new()
    {
        Alias = _config.DeviceAlias,
        Version = Constants.DefaultProtocolVersion,
        DeviceModel = Environment.MachineName,
        DeviceType = DeviceType.Desktop,
        Fingerprint = _fingerprint,
        Port = _config.HttpPort,
        Protocol = CoreProtocolType.Https,
        Download = true,
        Announce = true,
    };

    public void ApplyTheme(Theme theme)
    {
        _config.Theme = theme;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "EasyShare",
            Icon = GenerateTrayIcon(),
        };

        _trayIcon.TrayLeftMouseDown += (_, _) => ToggleWindowVisibility();
        _trayIcon.ContextMenu = CreateTrayMenu();
        _trayIcon.Visibility = Visibility.Visible;

        Resources.Add("TrayIcon", _trayIcon);
    }

    private static Icon GenerateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using var bgBrush = new SolidBrush(Color.FromArgb(42, 42, 42));
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var font = new Font(new FontFamily("Segoe UI"), 11, System.Drawing.FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("ES", font, textBrush, new RectangleF(0, 0, 32, 32), sf);

        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private static ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();

        var showItem = new MenuItem { Header = "Zeigen" };
        showItem.Click += (_, _) =>
        {
            if (Application.Current.MainWindow is MainWindow w)
                w.ToggleWindowVisibility();
        };

        var exitItem = new MenuItem { Header = "Beenden" };
        exitItem.Click += (_, _) =>
        {
            if (Application.Current.MainWindow is MainWindow w)
                w.ShutdownApplication();
        };

        menu.Items.Add(showItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible) { Hide(); }
        else { Show(); WindowState = WindowState.Normal; Activate(); }
    }

    private void ShutdownApplication()
    {
        Cleanup();
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private void OnDeviceFound(object? sender, DeviceInfo device)
    {
        Dispatcher.Invoke(() =>
        {
            if (Devices.Any(d => d.Fingerprint == device.Fingerprint))
                return;

            Devices.Add(new DeviceViewModel
            {
                Alias = device.Alias,
                DeviceModel = device.DeviceModel ?? "Unbekannt",
                IpAddress = device.IpAddress,
                DeviceType = device.DeviceType ?? DeviceType.Desktop,
                Fingerprint = device.Fingerprint,
                Port = device.Port > 0 ? device.Port : 53317,
            });

            StatusText.Text = $"{Devices.Count} Gerät(e) gefunden";
        });
    }

    private void OnDeviceLost(object? sender, string fingerprint)
    {
        Dispatcher.Invoke(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.Fingerprint == fingerprint);
            if (existing is not null)
            {
                Devices.Remove(existing);
                if (_selectedDevice == existing)
                    _selectedDevice = null;
            }

            StatusText.Text = Devices.Count > 0
                ? $"{Devices.Count} Gerät(e) gefunden"
                : "Bereit";
            UpdateSelectedDeviceText();
        });
    }

    private void DeviceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeviceViewModel device)
        {
            _selectedDevice = device;
            UpdateSelectedDeviceText();

            foreach (var item in DeviceList.Items)
            {
                if (item is DeviceViewModel d)
                    d.IsSelected = d.Fingerprint == device.Fingerprint;
            }
        }
    }

    private void UpdateSelectedDeviceText()
    {
        SelectedDeviceText.Text = _selectedDevice is not null
            ? $"Ziel: {_selectedDevice.Alias}"
            : "Kein Zielgerät ausgewählt";
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        else
        { e.Effects = DragDropEffects.None; }
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (_selectedDevice is null)
            { StatusText.Text = "Bitte zuerst ein Zielgerät auswählen"; return; }

            SendFiles(files, _selectedDevice);
        }
    }

    private void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null)
        { StatusText.Text = "Bitte zuerst ein Zielgerät auswählen"; return; }

        var dialog = new OpenFileDialog { Multiselect = true, Title = "Dateien auswählen" };
        if (dialog.ShowDialog() == true)
            SendFiles(dialog.FileNames, _selectedDevice);
    }

    private void SendFiles(string[] filePaths, DeviceViewModel target)
    {
        if (_sessionManager is null) return;

        var deviceInfo = new DeviceInfo
        {
            Alias = target.Alias, DeviceModel = target.DeviceModel,
            DeviceType = target.DeviceType, Fingerprint = target.Fingerprint,
            IpAddress = target.IpAddress, Port = target.Port,
        };

        var session = _sessionManager.CreateSendSession(deviceInfo, filePaths.ToList());

        var transfer = new TransferViewModel
        {
            FileName = filePaths.Length == 1 ? Path.GetFileName(filePaths[0]) : $"{filePaths.Length} Dateien an {target.Alias}",
            Progress = 0, SpeedText = string.Empty, StatusText = "Wird vorbereitet...", Status = TransferStatus.Pending,
        };

        Dispatcher.Invoke(() => Transfers.Add(transfer));
        StatusText.Text = $"Sende an {target.Alias}...";

        var fileSender = new FileSender(
            BuildAnnouncement(),
            target.IpAddress,
            target.Port,
            target.Fingerprint,
            useTls: true,
            Constants.DefaultApiBase);
        fileSender.ProgressChanged += (_, progress) =>
        {
            Dispatcher.Invoke(() =>
            {
                transfer.Progress = progress;
                transfer.SpeedText = $"{FormatBytes((long)fileSender.CurrentBytesPerSecond)}/s";
                transfer.Status = TransferStatus.Active;
                transfer.StatusText = "Übertragung läuft...";
            });
        };

        fileSender.StatusChanged += (_, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                transfer.Status = status;
                transfer.StatusText = status switch
                {
                    TransferStatus.Completed => "Abgeschlossen",
                    TransferStatus.Failed => "Fehlgeschlagen",
                    TransferStatus.Cancelled => "Abgebrochen",
                    _ => transfer.StatusText,
                };
                StatusText.Text = status == TransferStatus.Active
                    ? $"Sende an {target.Alias}..."
                    : $"Übertragung: {transfer.StatusText}";

                if (status == TransferStatus.Completed)
                    History.Insert(0, new HistoryEntry { FileName = transfer.FileName, Direction = "→", Timestamp = DateTime.Now });
            });
        };

        _ = Task.Run(async () =>
        {
            try
            {
                using (fileSender)
                    await fileSender.SendAsync(session);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    transfer.Status = TransferStatus.Failed;
                    transfer.StatusText = $"Fehler: {ex.Message}";
                    StatusText.Text = $"Fehler bei Übertragung an {target.Alias}";
                });
            }
        });
    }

    private void OnUploadRequested(object? sender, UploadRequestEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_config.AutoAcceptTrusted && _trustStore?.IsTrusted(e.Fingerprint) == true)
            {
                AcceptUpload(e, false);
                return;
            }

            var dialog = new ReceiveDialog(e.Sender.Alias ?? "Unbekannt", e.Files, e.Fingerprint);
            dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.Accepted)
            {
                AcceptUpload(e, dialog.TrustDevice);
                if (dialog.TrustDevice)
                    _trustStore?.AddTrusted(e.Fingerprint, e.Sender.Alias ?? "Unbekannt");
            }
            else
            {
                _server?.RejectUpload(e.SessionId);
            }
        });
    }

    private void AcceptUpload(UploadRequestEventArgs e, bool addToHistory)
    {
        var transfer = new TransferViewModel
        {
            FileName = e.Files.Count == 1 ? e.Files[0].FileName : $"{e.Files.Count} Dateien von {e.Sender.Alias}",
            Progress = 0, SpeedText = string.Empty, StatusText = "Empfang wird vorbereitet...", Status = TransferStatus.Active,
        };

        Transfers.Add(transfer);
        StatusText.Text = $"Empfange von {e.Sender.Alias}...";
        _receiveTransfers[e.SessionId] = transfer;

        _server?.AcceptUpload(e.SessionId, _config.DefaultSavePath);
    }

    private void OnUploadCompleted(object? sender, UploadCompletedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_receiveTransfers.Remove(e.SessionId, out var transfer))
            {
                transfer.Status = TransferStatus.Completed;
                transfer.Progress = 1;
                transfer.StatusText = "Abgeschlossen";
                History.Insert(0, new HistoryEntry { FileName = e.FileName, Direction = "←", Timestamp = DateTime.Now });
            }
            StatusText.Text = $"Empfangen: {e.FileName}";

            if (!IsVisible)
            {
                Show();
                Activate();
                _trayIcon?.ToolTipText = $"Datei empfangen: {e.FileName}";
            }
        });
    }

    private void QrButton_Click(object sender, RoutedEventArgs e)
    {
        var localIp = GetLocalIpAddress();
        var window = new QrCodeWindow(localIp, _config.HttpPort);
        window.Owner = this;
        window.ShowDialog();
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 53);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }

        return IPAddress.Loopback.ToString();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_config, _configPath);
        window.Owner = this;
        window.ShowDialog();

        if (_config.DeviceAlias != window.AliasTextBox.Text || _config.HttpPort != (int.TryParse(window.PortBox.Text, out var p) ? p : 53317))
        {
            LoadConfig();
            RestartServices();
        }
    }

    private void RestartServices()
    {
        _discovery?.Stop();
        _server?.Stop();
        StartServices();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isCleanedUp) { e.Cancel = true; Hide(); }
    }

    public void Cleanup()
    {
        _isCleanedUp = true;
        _discovery?.DeviceFound -= OnDeviceFound;
        _discovery?.DeviceLost -= OnDeviceLost;
        _server?.UploadRequested -= OnUploadRequested;
        _server?.UploadCompleted -= OnUploadCompleted;
        _discovery?.Stop();
        _server?.Stop();
        _discovery?.Dispose();
        _certificate?.Dispose();
        _cts.Cancel();
    }

    private void CheckForUpdatesAsync()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        _updateService = new UpdateService(version);
        _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
        _ = Task.Run(() => _updateService.CheckForUpdateAsync());
    }

    private void OnUpdateCheckCompleted(UpdateInfo info)
    {
        Dispatcher.Invoke(() =>
        {
            if (info.UpdateAvailable)
            {
                _pendingUpdate = info;
                UpdateVersionText.Text = $"v{info.LatestVersion} -> Aktualisieren und Neustart";
                UpdateBanner.Visibility = Visibility.Visible;
            }
        });
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Wird heruntergeladen...";
        StatusText.Text = "Lade Update herunter...";

        var progress = new Progress<int>(p => StatusText.Text = $"Lade Update herunter... {p}%");
        _ = Task.Run(async () =>
        {
            try
            {
                await _updateService!.DownloadAndApplyAsync(_pendingUpdate!.DownloadUrl, progress);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateButton.IsEnabled = true;
                    UpdateButton.Content = "Aktualisieren";
                    StatusText.Text = $"Update fehlgeschlagen: {ex.Message}";
                });
            }
        });
    }
    private static string FormatBytes(long bytes)
    {
        var gb = bytes / 1_000_000_000.0;
        var mb = bytes / 1_000_000.0;
        var kb = bytes / 1_000.0;
        if (gb >= 1) return $"{gb:F1} GB";
        if (mb >= 1) return $"{mb:F1} MB";
        if (kb >= 1) return $"{kb:F1} KB";
        return $"{bytes:F0} B";
    }
}

public class HistoryEntry
{
    public string FileName { get; set; } = "";
    public string Direction { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
