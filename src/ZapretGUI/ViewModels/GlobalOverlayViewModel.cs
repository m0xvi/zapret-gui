using Wpf.Ui.Controls;
using ZapretGUI.Core;

namespace ZapretGUI.ViewModels;

/// <summary>
/// Глобальный затемняющий оверлей для длительных операций. Один экземпляр в MainViewModel.
/// </summary>
public partial class GlobalOverlayViewModel : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _title = "Выполнение операции";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private double _progress; // 0..100
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = "";

    // Сохранённые колбэки — не сериализуются
    private Action? _onCancel;
    private Action? _onRetry;

    public bool OverlayVisible => IsVisible;
    public bool ProgressVisible => IsVisible && !HasError && !IsIndeterminate;
    public bool ErrorVisible => IsVisible && HasError;
    public string ProgressPercentText => $"{(int)Math.Clamp(Progress, 0, 100)}%";

    partial void OnIsVisibleChanged(bool value)
    {
        Raise(nameof(OverlayVisible));
        Raise(nameof(ProgressVisible));
        Raise(nameof(ErrorVisible));
    }
    partial void OnHasErrorChanged(bool value)
    {
        Raise(nameof(ProgressVisible));
        Raise(nameof(ErrorVisible));
    }
    partial void OnIsIndeterminateChanged(bool value) => Raise(nameof(ProgressVisible));
    partial void OnProgressChanged(double value) => Raise(nameof(ProgressPercentText));

    public void Show(string title, string status, string details = "", double? progress = null, bool indeterminate = false, bool canCancel = false, Action? onCancel = null, Action? onRetry = null)
    {
        Title = title;
        Status = status;
        Details = details;
        IsIndeterminate = indeterminate;
        Progress = progress ?? 0;
        CanCancel = canCancel;
        HasError = false;
        ErrorText = "";
        _onCancel = onCancel;
        _onRetry = onRetry;
        IsVisible = true;
    }

    public void Update(string status, string? details = null, double? progress = null, bool? indeterminate = null)
    {
        Status = status;
        if (details != null) Details = details;
        if (progress != null) Progress = progress.Value;
        if (indeterminate != null) IsIndeterminate = indeterminate.Value;
    }

    public void ShowError(string title, string error, string details = "")
    {
        Title = title;
        ErrorText = error;
        Details = details;
        HasError = true;
        IsVisible = true;
    }

    public void Hide()
    {
        IsVisible = false;
        HasError = false;
        CanCancel = false;
        _onCancel = null;
        _onRetry = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        try { _onCancel?.Invoke(); } catch {}
        // Не прячем сразу — пусть операция сама вызовет Hide после отмены
    }

    [RelayCommand]
    private void Dismiss() => Hide();

    [RelayCommand]
    private void Retry()
    {
        var cb = _onRetry;
        Hide();
        try { cb?.Invoke(); } catch {}
    }
}
