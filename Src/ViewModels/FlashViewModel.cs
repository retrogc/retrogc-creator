using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroGcCreator.Services;

namespace RetroGcCreator.ViewModels;

/// <summary>"Write to USB": pick a USB disk, confirm, write the image.</summary>
public sealed partial class FlashViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;

    public FlashViewModel(string image)
    {
        Image = image;
        ImageText = $"{Path.GetFileName(image)}  ·  {UsbDrive.FormatSize(new FileInfo(image).Length)} compressed";
    }

    public string Image { get; }
    public string ImageText { get; }

    public event EventHandler? CloseRequested;

    public ObservableCollection<UsbDrive> Drives { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrive))]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    public partial UsbDrive? SelectedDrive { get; set; }

    public bool HasDrive => SelectedDrive is not null;

    partial void OnSelectedDriveChanged(UsbDrive? value) => Confirmed = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand))]
    public partial bool Confirmed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(RefreshCommand), nameof(CloseCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsWriting { get; set; }

    public bool IsIdle => !IsWriting;

    [ObservableProperty] public partial double Percent { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StatusIsError { get; set; }
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool NoDrives { get; set; }

    private bool CanRefresh() => !IsWriting;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        var previous = SelectedDrive?.Device;
        Drives.Clear();
        try
        {
            foreach (var drive in await UsbDrives.ListAsync())
                Drives.Add(drive);
            Status = string.Empty;
            StatusIsError = false;
        }
        catch (Exception exception)
        {
            Log.Error("USB drives", exception);
            Status = $"Could not list the USB drives: {exception.Message}";
            StatusIsError = true;
        }

        NoDrives = Drives.Count == 0;
        SelectedDrive = previous is null ? null : FindDrive(previous);
    }

    private UsbDrive? FindDrive(string device)
    {
        foreach (var drive in Drives)
            if (drive.Device == device)
                return drive;
        return null;
    }

    private bool CanWrite() => !IsWriting && SelectedDrive is not null && Confirmed;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var drive = SelectedDrive!;
        IsWriting = true;
        IsDone = false;
        StatusIsError = false;
        Percent = 0;
        Status = OperatingSystem.IsWindows()
            ? "Waiting for administrator rights…"
            : "Waiting for your password…";

        _cts = new CancellationTokenSource();
        try
        {
            await Flasher.FlashAsync(Image, drive, new Progress<double>(f =>
            {
                Percent = f * 100;
                Status = $"Writing to {drive.Model}… {f * 100:0}%";
            }), _cts.Token);

            Percent = 100;
            IsDone = true;
            Status = $"Done! The card was written to {drive.Model}. You can remove the drive.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. The drive was only partly written: write it again before using it.";
            StatusIsError = true;
        }
        catch (Exception exception)
        {
            Log.Error("Flash", exception);
            Status = exception.Message;
            StatusIsError = true;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsWriting = false;
            Confirmed = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
