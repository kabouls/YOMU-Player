using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace YomuPlayer.Desktop;

public sealed partial class UpdateDialog : ContentDialog
{
    private readonly UpdateInfo _info;
    private bool _busy;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;
        VersionText.Text = $"Version {info.Version}";
        DateText.Text = $"{AppPaths.ProductName}  ·  {info.Channel} channel  ·  {info.Published.LocalDateTime:g}";
        NotesText.Text = info.Notes.Count > 0 ? info.NotesText : "No release notes were provided.";
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_busy) return;
        _busy = true;
        args.Cancel = true;
        var deferral = args.GetDeferral();
        PrimaryButtonText = "Downloading...";
        CloseButtonText = "Cancel";
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        StatusText.Text = "Downloading update...";
        IsPrimaryButtonEnabled = false;

        try
        {
            _cts = new CancellationTokenSource();
            long downloadedBytes = 0;
            var result = await UpdateService.DownloadAndInstallAsync(
                _info, new Progress<UpdateDownloadProgress>(p =>
                {
                    downloadedBytes = p.Bytes;
                    Progress.Value = p.Fraction * 100;
                    var downloadedMb = p.Bytes / 1048576.0;
                    StatusText.Text = p.Total > 0
                        ? $"Downloading update... {p.Fraction:P0}  ·  {downloadedMb:0.0} / {p.Total / 1048576.0:0.0} MB"
                        : $"Downloading update... {downloadedMb:0.0} MB";
                }), _cts.Token);

            if (result == UpdateLaunchResult.ExitApp)
            {
                StatusText.Text = downloadedBytes > 0
                    ? $"Downloaded {downloadedBytes / 1048576.0:0.0} MB. Restarting YOMU Player..."
                    : "Update ready. Restarting YOMU Player...";
                args.Cancel = false;
                App.MainWindow?.CloseForUpdate();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Update cancelled.";
            Reset();
        }
        catch (Exception e)
        {
            StatusText.Text = "Update failed: " + e.Message;
            Reset();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Reset()
    {
        _busy = false;
        PrimaryButtonText = "Download & install";
        CloseButtonText = "Later";
        IsPrimaryButtonEnabled = true;
        Progress.Visibility = Visibility.Collapsed;
    }
}
