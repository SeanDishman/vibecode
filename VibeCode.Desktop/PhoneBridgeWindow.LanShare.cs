using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using VibeCode.Services;

namespace VibeCode;

public partial class PhoneBridgeWindow
{
    private readonly LanShareService _lanShare = LanShareService.Instance;
    private readonly Dictionary<string, BitmapSource> _downloadQrs = new(StringComparer.Ordinal);
    private bool _refreshingLanShare;

    private void InitializeLanShare() => _lanShare.PropertyChanged += OnLanShareChanged;
    private void DisposeLanShare() => _lanShare.PropertyChanged -= OnLanShareChanged;

    private void OnLanShareChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LanShareService.Enabled)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => { if (!_closed) RefreshApk(); }));
            return;
        }
        if (!_closed) RefreshApk();
    }

    private void RefreshLanShare()
    {
        _refreshingLanShare = true;
        try
        {
            var addresses = _lanShare.Addresses;
            var selected = LanShareAddressBox.SelectedItem as string;
            if (LanShareAddressBox.ItemsSource is not string[] current || !current.SequenceEqual(addresses))
            {
                LanShareAddressBox.ItemsSource = addresses;
                LanShareAddressBox.SelectedItem = addresses.Contains(selected) ? selected : addresses.FirstOrDefault();
            }
            LanShareAddressPanel.Visibility = _lanShare.Running ? Visibility.Visible : Visibility.Collapsed;
            LanShareStatus.Foreground = (Brush)FindResource(_lanShare.Error.Length > 0 ? "Red" : "Muted");
            LanShareStatus.Text = _lanShare.Error.Length > 0 ? _lanShare.Error
                : !_lanShare.Running ? "Off. Turn on to scan a QR code and download the app on your phone."
                : addresses.Length == 0 ? "No local network address. Reconnect this PC and choose Check again."
                : !_bridge.Enrolments.Any(app => app.Pending && File.Exists(app.ApkPath))
                    ? "On. Generate an app below to show its download QR code."
                    : "On. Each app below has its own download QR code. Sharing stops when Phone access or VibeCode turns off.";
            LanShareNetworkHint.Text = $"Keep both devices on the same Wi-Fi. If the page does not open, check the selected address and allow VibeCode through Windows Firewall on TCP port {_lanShare.Port}.";
            if (!_lanShare.Running) _downloadQrs.Clear();
        }
        finally { _refreshingLanShare = false; }
    }

    private ApkRow WithLanShare(ApkRow row)
    {
        if (!row.CanCancel || !row.HasFile || LanShareAddressBox.SelectedItem is not string address) return row;
        var url = _lanShare.DownloadUrl(row.Id, address);
        if (url.Length == 0) return row;
        try
        {
            if (!_downloadQrs.TryGetValue(url, out var bitmap))
            {
                using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
                using var qr = new PngByteQRCode(data);
                using var bytes = new MemoryStream(qr.GetGraphic(6));
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = bytes;
                image.EndInit();
                image.Freeze();
                _downloadQrs[url] = bitmap = image;
            }
            return row with { DownloadUrl = url, DownloadQr = bitmap };
        }
        catch (Exception ex)
        {
            LanShareStatus.Text = "The QR code could not be created. Stop LAN Share and try again, or copy the APK file to your phone.";
            LanShareStatus.Foreground = (Brush)FindResource("Red");
            CrashLog.Note("LanShare", "QR generation failed - " + ex.GetType().Name);
            return row;
        }
    }

    private void OnLanShareAddressChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingLanShare && !_closed) RefreshApk();
    }

    private void OnCopyLanShare(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ApkRow { DownloadUrl.Length: > 0 } row }) return;
        try { Clipboard.SetText(row.DownloadUrl); LanShareStatus.Text = "Download link copied. Keep it private until this phone connects."; }
        catch { LanShareStatus.Text = "The clipboard is busy. Try Copy download link again."; }
    }

    private void ShowApkQr(string path)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed) return;
            var row = ApkList.Items.OfType<ApkRow>().FirstOrDefault(item => item.Path == path);
            if (row?.DownloadQr is null) { Reveal(path); return; }
            ApkList.UpdateLayout();
            (ApkList.ItemContainerGenerator.ContainerFromItem(row) as FrameworkElement)?.BringIntoView();
        }));
    }
}
