using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
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
using EasyShare.App.Localization;
using H.NotifyIcon;
using Microsoft.Win32;
using CoreProtocolType = EasyShare.Core.Models.ProtocolType;

namespace EasyShare.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public static readonly System.Windows.Input.RoutedCommand OpenFilesCommand = new();
    public static readonly System.Windows.Input.RoutedCommand SettingsCommand = new();
    public static readonly System.Windows.Input.RoutedCommand QuitCommand = new();
    public static readonly System.Windows.Input.RoutedCommand DeselectCommand = new();
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
    private readonly Dictionary<TransferViewModel, CancellationTokenSource> _sendCancels = new();
    private readonly CancellationTokenSource _cts = new();
    private UpdateInfo? _pendingUpdate;
    private DeviceViewModel? _selectedDevice;
    private readonly List<string> _pendingFiles = new();
    private readonly AmsiScanner _amsiScanner = new();
    private readonly System.Windows.Threading.DispatcherTimer _deviceStatusTimer;
    private System.Windows.Threading.DispatcherTimer? _updateCheckTimer;

    public MainWindow()
    {
        InitializeComponent();

        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyShare", "config.json");

        DeviceList.ItemsSource = Devices;
        TransferList.ItemsSource = Transfers;
        HistoryList.ItemsSource = History;

        Devices.CollectionChanged += (_, _) => DevicesEmptyText.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Transfers.CollectionChanged += (_, _) => TransfersEmptyText.Visibility = Transfers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        History.CollectionChanged += (_, _) => HistoryEmptyText.Visibility = History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LoadConfig();
        Loc.Instance.Language = _config.Language;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RestoreWindowBounds();
        InitializeServices();
        SetupTrayIcon();
        StartServices();
        ApplyTheme(_config.Theme);

        _deviceStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _deviceStatusTimer.Tick += (_, _) => RefreshDeviceStatus();
        _deviceStatusTimer.Start();

        _updateCheckTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(4),
        };
        _updateCheckTimer.Tick += (_, _) => CheckForUpdatesAsync();
        _updateCheckTimer.Start();

        CheckForUpdatesAsync();
        ConsumeShareArgs();
    }

    private void RefreshDeviceStatus()
    {
        var now = DateTime.UtcNow;
        foreach (var device in Devices)
        {
            var age = now - device.LastSeen;
            device.IsOnline = age < TimeSpan.FromSeconds(30);
            device.LastSeenText = FormatLastSeen(age);
        }
    }

    private static string FormatLastSeen(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(30)) return Loc.Tr("Main.JustNow");
        if (age < TimeSpan.FromMinutes(1)) return Loc.Tr("Main.SeenSeconds", Math.Max(1, (int)age.TotalSeconds));
        if (age < TimeSpan.FromHours(1)) return Loc.Tr("Main.SeenMinutes", (int)age.TotalMinutes);
        return Loc.Tr("Main.SeenHours", (int)age.TotalHours);
    }

    private void OnLanguageChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateSelectedDeviceText();
            StatusText.Text = Loc.Tr("Main.StatusReady");
        });
    }

    private void ConsumeShareArgs()
    {
        if (App.ShareArgs.Length == 0) return;
        var paths = App.ShareArgs.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        App.SetShareArgs(Array.Empty<string>());
        if (paths.Length == 0) return;

        if (_selectedDevice is not null)
        {
            SendFiles(paths, _selectedDevice);
        }
        else
        {
            _pendingFiles.AddRange(paths);
            StatusText.Text = Loc.Tr("Main.PendingFiles", _pendingFiles.Count);
            Show();
            Activate();
        }
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

    private void RestoreWindowBounds()
    {
        var x = _config.WindowX;
        var y = _config.WindowY;
        var w = _config.WindowWidth;
        var h = _config.WindowHeight;
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(w) || double.IsNaN(h)) return;
        if (w < 400 || h < 300) return;

        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vWidth = SystemParameters.VirtualScreenWidth;
        var vHeight = SystemParameters.VirtualScreenHeight;
        var visible = !(x > vLeft + vWidth - 100 || y > vTop + vHeight - 40 || x + w < vLeft + 100 || y + h < vTop + 40);
        if (!visible) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = x;
        Top = y;
        Width = w;
        Height = h;
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
        _discovery.DeviceSeen += OnDeviceSeen;
        _discovery.DeviceLost += OnDeviceLost;
        _server.UploadRequested += OnUploadRequested;
        _server.UploadProgress += OnUploadProgress;
        _server.UploadCancelled += OnUploadCancelled;
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
                    ShellIntegration.Register(exePath, Loc.Tr("Shell.ShareMenu"));
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
                            if (_selectedDevice is not null)
                            {
                                SendFiles(files, _selectedDevice);
                            }
                            else
                            {
                                _pendingFiles.AddRange(files);
                                StatusText.Text = Loc.Tr("Main.PendingFiles", _pendingFiles.Count);
                                Show();
                                Activate();
                            }
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
        StatusText.Text = Loc.Tr("Main.StatusReady");
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
        var isDark = theme switch
        {
            Theme.Light => false,
            Theme.Dark => true,
            _ => Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() == Wpf.Ui.Appearance.SystemTheme.Dark,
        };

        try
        {
            SwapAppThemeDictionary(isDark);
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                isDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                updateAccent: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EasyShare] ApplyTheme failed: {ex.Message}");
        }
    }

    private static void SwapAppThemeDictionary(bool darksTheme)
    {
        var uri = new Uri($"pack://application:,,,/EasyShare.App;component/Themes/{(darksTheme ? "Dark" : "Light")}.xaml");
        var merged = Application.Current.Resources.MergedDictionaries;

        var existing = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/", StringComparison.Ordinal) == true);
        if (existing is not null)
            merged.Remove(existing);
        merged.Insert(0, new ResourceDictionary { Source = uri });
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

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();

        var showItem = new MenuItem { Header = Loc.Tr("Tray.Show") };
        showItem.Click += (_, _) =>
        {
            if (Application.Current.MainWindow is MainWindow w)
                w.ToggleWindowVisibility();
        };

        var exitItem = new MenuItem { Header = Loc.Tr("Tray.Exit") };
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
        try
        {
            Dispatcher.Invoke(() =>
            {
                var existing = Devices.FirstOrDefault(d => d.Fingerprint == device.Fingerprint);
                if (existing is not null)
                {
                    existing.Alias = device.Alias;
                    existing.DeviceModel = device.DeviceModel ?? Loc.Tr("Main.DeviceUnknown");
                    existing.IpAddress = device.IpAddress;
                    existing.DeviceType = device.DeviceType ?? DeviceType.Desktop;
                    existing.Port = device.Port > 0 ? device.Port : 53317;
                    existing.LastSeen = DateTime.UtcNow;
                    return;
                }

                Devices.Add(new DeviceViewModel
                {
                    Alias = device.Alias,
                    DeviceModel = device.DeviceModel ?? Loc.Tr("Main.DeviceUnknown"),
                    IpAddress = device.IpAddress,
                    DeviceType = device.DeviceType ?? DeviceType.Desktop,
                    Fingerprint = device.Fingerprint,
                    Port = device.Port > 0 ? device.Port : 53317,
                    LastSeen = DateTime.UtcNow,
                });

                StatusText.Text = Loc.Tr("Main.StatusDevicesFound", Devices.Count);
            });
        }
        catch { }
    }

    private void OnDeviceSeen(object? sender, DeviceInfo device)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                var existing = Devices.FirstOrDefault(d => d.Fingerprint == device.Fingerprint);
                if (existing is not null)
                    existing.LastSeen = device.LastSeen;
            });
        }
        catch { }
    }

    private void OnDeviceLost(object? sender, string fingerprint)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                var existing = Devices.FirstOrDefault(d => d.Fingerprint == fingerprint);
                if (existing is not null)
                {
                    Devices.Remove(existing);
                    if (_selectedDevice == existing)
                        ClearSelection();
                }

                StatusText.Text = Devices.Count > 0
                    ? Loc.Tr("Main.StatusDevicesFound", Devices.Count)
                    : Loc.Tr("Main.StatusReady");
                UpdateSelectedDeviceText();
            });
        }
        catch { }
    }

    private void DeviceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is DeviceViewModel device)
        {
            if (_selectedDevice == device)
            {
                ClearSelection();
                return;
            }

            _selectedDevice = device;
            UpdateSelectedDeviceText();

            foreach (var item in DeviceList.Items)
            {
                if (item is DeviceViewModel d)
                    d.IsSelected = d.Fingerprint == device.Fingerprint;
            }

            FlushPendingFiles();
        }
    }

    private void DeselectButton_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void ClearSelection()
    {
        _selectedDevice = null;
        foreach (var item in DeviceList.Items)
        {
            if (item is DeviceViewModel d)
                d.IsSelected = false;
        }
        UpdateSelectedDeviceText();
    }

    private void FlushPendingFiles()
    {
        if (_selectedDevice is null || _pendingFiles.Count == 0) return;

        var files = _pendingFiles.ToArray();
        _pendingFiles.Clear();
        StatusText.Text = Loc.Tr("Main.SendingPending", files.Length);
        SendFiles(files, _selectedDevice);
    }

    private void UpdateSelectedDeviceText()
    {
        SelectedDeviceText.Text = _selectedDevice is not null
            ? Loc.Tr("Main.TargetDevice", _selectedDevice.Alias)
            : Loc.Tr("Main.NoDeviceSelected");
        DeselectButton.Visibility = _selectedDevice is not null ? Visibility.Visible : Visibility.Collapsed;
        SelectFilesButton.IsEnabled = _selectedDevice is not null;
        SelectFolderButton.IsEnabled = _selectedDevice is not null;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        else
        { e.Effects = DragDropEffects.None; }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        DropZoneHighlight.Visibility = Visibility.Visible;
        ((System.Windows.Media.Animation.Storyboard)FindResource("DropZonePulse")).Begin(this);
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneHighlight.Visibility = Visibility.Collapsed;
        ((System.Windows.Media.Animation.Storyboard)FindResource("DropZonePulse")).Stop(this);
        DropZoneHighlightScale.ScaleX = 1;
        DropZoneHighlightScale.ScaleY = 1;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZoneHighlight.Visibility = Visibility.Collapsed;
        ((System.Windows.Media.Animation.Storyboard)FindResource("DropZonePulse")).Stop(this);
        DropZoneHighlightScale.ScaleX = 1;
        DropZoneHighlightScale.ScaleY = 1;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (_selectedDevice is null)
            { StatusText.Text = Loc.Tr("Main.SelectDeviceFirst"); return; }

            SendFiles(files, _selectedDevice);
        }
    }

    private void SelectFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null)
        { StatusText.Text = Loc.Tr("Main.SelectDeviceFirst"); return; }

        var dialog = new OpenFileDialog { Multiselect = true, Title = Loc.Tr("Main.SelectFilesDialog") };
        if (dialog.ShowDialog() == true)
            SendFiles(dialog.FileNames, _selectedDevice);
    }

    private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDevice is null)
        { StatusText.Text = Loc.Tr("Main.SelectDeviceFirst"); return; }

        var dialog = new OpenFolderDialog { Title = Loc.Tr("Main.SelectFolderDialog") };
        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
            SendFiles([dialog.FolderName], _selectedDevice);
    }

    private void OpenFilesCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => SelectFilesButton_Click(sender, e);

    private void SettingsCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => SettingsButton_Click(sender, e);

    private void QuitCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        _isCleanedUp = true;
        Application.Current.Shutdown();
    }

    private void DeselectCommand_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => DeselectButton_Click(sender, e);

    private void SendFiles(string[] filePaths, DeviceViewModel target)
    {
        if (_sessionManager is null) return;

        var deviceInfo = new DeviceInfo
        {
            Alias = target.Alias,
            DeviceModel = target.DeviceModel,
            DeviceType = target.DeviceType,
            Fingerprint = target.Fingerprint,
            IpAddress = target.IpAddress,
            Port = target.Port,
        };

        var session = _sessionManager.CreateSendSession(deviceInfo, filePaths.ToList());

        if (session.Files.Count == 0)
        {
            StatusText.Text = Loc.Tr("Main.FolderEmpty");
            return;
        }

        var hasFolders = filePaths.Any(Directory.Exists);

        var transfer = new TransferViewModel
        {
            FileName = filePaths.Length == 1 ? Path.GetFileName(filePaths[0]) : Loc.Tr("Transfer.FilesTo", filePaths.Length, target.Alias),
            Progress = 0,
            SpeedText = string.Empty,
            StatusText = Loc.Tr("Transfer.Preparing"),
            Status = TransferStatus.Pending,
            CanCancel = true,
        };

        if (hasFolders || (CompressCheckBox.IsChecked == true && filePaths.Length > 1))
        {
            transfer.FileName = hasFolders && filePaths.Length == 1
                ? Loc.Tr("Transfer.FolderToCompressed", transfer.FileName, target.Alias)
                : Loc.Tr("Transfer.FilesToCompressed", filePaths.Length, target.Alias);
        }

        Dispatcher.Invoke(() => Transfers.Add(transfer));
        StatusText.Text = Loc.Tr("Main.StatusSendingTo", target.Alias);

        var fileSender = new FileSender(
            BuildAnnouncement(),
            target.IpAddress,
            target.Port,
            target.Fingerprint,
            useTls: true,
            Constants.DefaultApiBase);
        fileSender.ProgressChanged += (_, progress) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                transfer.Progress = progress;
                transfer.BytesText = $"{FormatBytes(fileSender.BytesSent)} / {FormatBytes(fileSender.TotalBytes)}";
                transfer.SpeedText = $"{FormatBytes((long)fileSender.CurrentBytesPerSecond)}/s";
                transfer.EtaText = FormatEta(fileSender.TotalBytes - fileSender.BytesSent, fileSender.CurrentBytesPerSecond);
                transfer.Status = TransferStatus.Active;
                transfer.StatusText = Loc.Tr("Transfer.Running");
            });
        };

        var sendHistoryPath = BuildHistoryPath(session.Files);

        fileSender.StatusChanged += (_, status) =>
        {
            Dispatcher.Invoke(() =>
            {
                transfer.Status = status;
                transfer.StatusText = status switch
                {
                    TransferStatus.Completed => Loc.Tr("Transfer.Completed"),
                    TransferStatus.Failed => Loc.Tr("Transfer.Failed"),
                    TransferStatus.Cancelled => Loc.Tr("Transfer.Cancelled"),
                    _ => transfer.StatusText,
                };
                transfer.CanCancel = status
                    is TransferStatus.Pending or TransferStatus.Active;
                if (status
                    is TransferStatus.Completed or TransferStatus.Failed or TransferStatus.Cancelled)
                    RemoveSendCancel(transfer);
                StatusText.Text = status == TransferStatus.Active
                    ? Loc.Tr("Main.StatusSendingTo", target.Alias)
                    : Loc.Tr("Main.StatusTransfer", transfer.StatusText);

                if (status == TransferStatus.Completed)
                    History.Insert(0, new HistoryEntry { FileName = transfer.FileName, Direction = "→", Timestamp = DateTime.Now, Path = sendHistoryPath });
            });
        };

        var cts = new CancellationTokenSource();
        transfer.CancelAction = () => cts.Cancel();
        _sendCancels[transfer] = cts;

        var scanEnabled = ScanCheckBox.IsChecked == true;
        var compressEnabled = CompressCheckBox.IsChecked == true;

        _ = Task.Run(async () =>
        {
            try
            {
                if (scanEnabled)
                {
                    foreach (var file in session.Files)
                    {
                        if (file.LocalFilePath is null || !File.Exists(file.LocalFilePath)) continue;

                        Dispatcher.Invoke(() => { transfer.StatusText = Loc.Tr("Transfer.Scanning"); });

                        var scanResult = await Task.Run(() => _amsiScanner.ScanFile(file.LocalFilePath));
                        if (scanResult == AmsiScanResult.Detected)
                            throw new MalwareDetectedException(file.FileName);
                        if (scanResult == AmsiScanResult.Error)
                            throw new InvalidOperationException(Loc.Tr("Transfer.ScanFailed", file.FileName));
                    }
                }

                using (fileSender)
                    await fileSender.SendAsync(session, cts.Token, compress: compressEnabled);
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    if (transfer.CanCancel)
                    {
                        transfer.Status = TransferStatus.Cancelled;
                        transfer.StatusText = Loc.Tr("Transfer.Cancelled");
                        transfer.CanCancel = false;
                        RemoveSendCancel(transfer);
                    }
                });
            }
            catch (MalwareDetectedException ex)
            {
                Dispatcher.Invoke(() =>
                {
                    transfer.Status = TransferStatus.Failed;
                    transfer.StatusText = Loc.Tr("Transfer.MalwareFound", ex.FileName);
                    transfer.CanCancel = false;
                    RemoveSendCancel(transfer);
                    StatusText.Text = Loc.Tr("Main.StatusSendError", target.Alias);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    transfer.Status = TransferStatus.Failed;
                    transfer.StatusText = Loc.Tr("Transfer.Error", ex.Message);
                    transfer.CanCancel = false;
                    RemoveSendCancel(transfer);
                    StatusText.Text = Loc.Tr("Main.StatusSendError", target.Alias);
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

            var dialog = new ReceiveDialog(e.Sender.Alias ?? Loc.Tr("Main.DeviceUnknown"), e.Files, e.Fingerprint);
            dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.Accepted)
            {
                AcceptUpload(e, dialog.TrustDevice);
                if (dialog.TrustDevice)
                    _trustStore?.AddTrusted(e.Fingerprint, e.Sender.Alias ?? Loc.Tr("Main.DeviceUnknown"));
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
            FileName = e.Compressed
                ? Loc.Tr("Transfer.FilesFromCompressed", e.OriginalFileCount, e.Sender.Alias)
                : e.Files.Count == 1 ? e.Files[0].FileName : Loc.Tr("Transfer.FilesFrom", e.Files.Count, e.Sender.Alias),
            Progress = 0,
            SpeedText = string.Empty,
            StatusText = Loc.Tr("Transfer.Running"),
            Status = TransferStatus.Active,
            CanCancel = true,
        };

        Transfers.Add(transfer);
        StatusText.Text = Loc.Tr("Main.StatusReceivingFrom", e.Sender.Alias);
        _receiveTransfers[e.SessionId] = transfer;

        transfer.CancelAction = () =>
        {
            _server?.CancelUpload(e.SessionId);
            transfer.Status = TransferStatus.Cancelled;
            transfer.StatusText = Loc.Tr("Transfer.Cancelled");
            transfer.CanCancel = false;
            _receiveTransfers.Remove(e.SessionId);
        };

        _server?.AcceptUpload(e.SessionId, _config.DefaultSavePath);
    }

    private void OnUploadCompleted(object? sender, UploadCompletedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _receiveTransfers.Remove(e.SessionId, out var transfer);
            if (transfer is not null)
            {
                transfer.Progress = 1;
                transfer.CanCancel = false;
                transfer.StatusText = Loc.Tr("Transfer.Scanning");
            }
            _ = ScanAndFinalizeAsync(e, transfer);

            if (!IsVisible)
            {
                Show();
                Activate();
                _trayIcon?.ToolTipText = Loc.Tr("Tray.FileReceived", e.FileName);
            }
        });
    }

    private async Task ScanAndFinalizeAsync(UploadCompletedEventArgs e, TransferViewModel? transfer)
    {
        var scanResult = await Task.Run(() => _amsiScanner.ScanFile(e.SavePath));

        Dispatcher.Invoke(() =>
        {
            if (scanResult == AmsiScanResult.Detected)
            {
                QuarantineFile(e.SavePath);
                if (transfer is not null)
                {
                    transfer.Status = TransferStatus.Failed;
                    transfer.StatusText = Loc.Tr("Transfer.MalwareFound", e.FileName);
                }
                StatusText.Text = Loc.Tr("Transfer.MalwareFound", e.FileName);
                return;
            }

            if (scanResult == AmsiScanResult.Error)
                StatusText.Text = Loc.Tr("Transfer.ScanFailed", e.FileName);

            if (transfer is not null)
            {
                transfer.Status = TransferStatus.Completed;
                transfer.StatusText = Loc.Tr("Transfer.Completed");
            }
            StatusText.Text = Loc.Tr("Main.StatusReceived", e.FileName);
            _trayIcon?.ShowNotification(Loc.Tr("Tray.ReceivedTitle"), Loc.Tr("Tray.Received", e.FileName), H.NotifyIcon.Core.NotificationIcon.Info);

            if (e.Compressed && File.Exists(e.SavePath) && Path.GetExtension(e.SavePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var extractDir = Path.Combine(Path.GetDirectoryName(e.SavePath)!, Path.GetFileNameWithoutExtension(e.SavePath));
                    ExtractZipSafely(e.SavePath, extractDir);
                    File.Delete(e.SavePath);
                    History.Insert(0, new HistoryEntry { FileName = Loc.Tr("Transfer.ExtractedFiles", e.OriginalFileCount, Path.GetFileName(extractDir)), Direction = "←", Timestamp = DateTime.Now, Path = extractDir });
                }
                catch (Exception ex)
                {
                    if (transfer is not null)
                        transfer.StatusText = Loc.Tr("Transfer.ExtractFailed", ex.Message);
                }
            }
            else
            {
                History.Insert(0, new HistoryEntry { FileName = e.FileName, Direction = "←", Timestamp = DateTime.Now, Path = e.SavePath });
            }
        });
    }

    private static void ExtractZipSafely(string zipPath, string extractDir)
    {
        var extractRoot = Path.GetFullPath(extractDir);
        Directory.CreateDirectory(extractRoot);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
            if (!target.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsicherer Archivpfad: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static void QuarantineFile(string filePath)
    {
        try
        {
            var quarantineDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyShare", "Quarantine");
            Directory.CreateDirectory(quarantineDir);
            var dest = Path.Combine(quarantineDir, Path.GetFileName(filePath));
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(filePath, dest);
        }
        catch { }
    }

    private void OnUploadProgress(object? sender, UploadProgressEventArgs e)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_receiveTransfers.TryGetValue(e.SessionId, out var transfer))
                {
                    transfer.Progress = e.TotalBytes > 0
                        ? Math.Min(1.0, (double)e.BytesReceived / e.TotalBytes)
                        : 1.0;
                    transfer.BytesText = $"{FormatBytes(e.BytesReceived)} / {FormatBytes(e.TotalBytes)}";
                    transfer.SpeedText = $"{FormatBytes((long)e.BytesPerSecond)}/s";
                    transfer.EtaText = FormatEta(e.TotalBytes - e.BytesReceived, e.BytesPerSecond);
                    transfer.StatusText = Loc.Tr("Transfer.Running");
                }
            });
        }
        catch { }
    }

    private void OnUploadCancelled(object? sender, UploadCancelledEventArgs e)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_receiveTransfers.Remove(e.SessionId, out var transfer))
                {
                    transfer.Status = TransferStatus.Cancelled;
                    transfer.StatusText = Loc.Tr("Transfer.Cancelled");
                    transfer.CanCancel = false;
                }
            });
        }
        catch { }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TransferViewModel vm) return;
        vm.CancelAction?.Invoke();
    }

    private void RemoveSendCancel(TransferViewModel transfer)
    {
        if (_sendCancels.Remove(transfer, out var cts))
            cts.Dispose();
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
        var oldAlias = _config.DeviceAlias;
        var oldPort = _config.HttpPort;

        var window = new SettingsWindow(_config, _configPath);
        window.Owner = this;
        window.ShowDialog();

        if (_config.DeviceAlias != oldAlias || _config.HttpPort != oldPort)
        {
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
        try
        {
            _config.WindowX = Left;
            _config.WindowY = Top;
            _config.WindowWidth = Width;
            _config.WindowHeight = Height;
            SaveConfig();
        }
        catch { }
        _trayIcon?.Dispose();
        _trayIcon = null;
        _discovery?.DeviceFound -= OnDeviceFound;
        _discovery?.DeviceSeen -= OnDeviceSeen;
        _discovery?.DeviceLost -= OnDeviceLost;
        _server?.UploadRequested -= OnUploadRequested;
        _server?.UploadProgress -= OnUploadProgress;
        _server?.UploadCancelled -= OnUploadCancelled;
        _server?.UploadCompleted -= OnUploadCompleted;
        _discovery?.Stop();
        _server?.Stop();
        _discovery?.Dispose();
        _certificate?.Dispose();
        _amsiScanner.Dispose();
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
                UpdateVersionText.Text = Loc.Tr("Main.UpdateRestart", info.LatestVersion);
                UpdateBanner.Visibility = Visibility.Visible;
            }
        });
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = Loc.Tr("Main.UpdateDownloading");
        StatusText.Text = Loc.Tr("Main.UpdateDownloading");

        var progress = new Progress<int>(p => StatusText.Text = Loc.Tr("Main.UpdateDownloadProgress", p));
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
                    UpdateButton.Content = Loc.Tr("Main.UpdateButton");
                    StatusText.Text = Loc.Tr("Main.UpdateFailed", ex.Message);
                });
            }
        });
    }
    private static string BuildHistoryPath(List<FileEntry> files)
    {
        var localFiles = files.Where(f => f.LocalFilePath is not null).Select(f => f.LocalFilePath!).ToList();
        if (localFiles.Count == 1) return localFiles[0];
        if (localFiles.Count > 1) return Path.GetDirectoryName(localFiles[0]) ?? "";
        return "";
    }

    private void HistoryItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HistoryEntry entry && !string.IsNullOrEmpty(entry.Path))
        {
            try
            {
                if (Directory.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.Path}\"") { UseShellExecute = true });
                }
                else if (File.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }
    }

    private void HistoryItem_Reveal(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HistoryEntry entry && !string.IsNullOrEmpty(entry.Path))
        {
            try
            {
                if (Directory.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.Path}\"") { UseShellExecute = true });
                }
                else if (File.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Path}\"") { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }
        e.Handled = true;
    }

    private void HistoryItem_MenuOpen(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is HistoryEntry entry && !string.IsNullOrEmpty(entry.Path))
        {
            try
            {
                if (Directory.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.Path}\"") { UseShellExecute = true });
                }
                else if (File.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
                }
            }
            catch
            {
            }
        }
        e.Handled = true;
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

    private static string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0 || bytesPerSecond <= 0) return string.Empty;
        var seconds = (long)(remainingBytes / bytesPerSecond);
        if (seconds <= 0) return string.Empty;
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60}m";
        if (seconds >= 60) return $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds}s";
    }
}

public class HistoryEntry
{
    public string FileName { get; set; } = "";
    public string Direction { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Path { get; set; } = "";
}

internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
