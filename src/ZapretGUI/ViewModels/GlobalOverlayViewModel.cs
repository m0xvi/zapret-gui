using System;
using ZapretGui.Core;

namespace ZapretGui.ViewModels;

/// <summary>
/// Глобальный затемняющий оверлей для длительных операций. Один экземпляр в MainViewModel.
/// </summary>
public sealed class GlobalOverlayViewModel : ObservableObject
{
    private bool _isVisible;
    private string _title = "Выполнение операции";
    private string _status = "";
    private string _details = "";
    private double _progress; // 0..100
    private bool _isIndeterminate;
    private bool _canCancel;
    private bool _hasError;
    private string _errorText = "";

    private Action? _onCancel;
    private Action? _onRetry;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Set(ref _isVisible, value))
            {
                Raise(nameof(OverlayVisible));
                Raise(nameof(ProgressVisible));
                Raise(nameof(ErrorVisible));
            }
        }
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string Details
    {
        get => _details;
        set => Set(ref _details, value);
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Set(ref _progress, value))
                Raise(nameof(ProgressPercentText));
        }
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (Set(ref _isIndeterminate, value))
                Raise(nameof(ProgressVisible));
        }
    }

    public bool CanCancel
    {
        get => _canCancel;
        set => Set(ref _canCancel, value);
    }

    public bool HasError
    {
        get => _hasError;
        set
        {
            if (Set(ref _hasError, value))
            {
                Raise(nameof(ProgressVisible));
                Raise(nameof(ErrorVisible));
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        set => Set(ref _errorText, value);
    }

    public bool OverlayVisible => IsVisible;
    public bool ProgressVisible => IsVisible && !HasError && !IsIndeterminate;
    public bool ErrorVisible => IsVisible && HasError;
    public string ProgressPercentText => $"{(int)Math.Clamp(Progress, 0, 100)}%";

    public ICommand CancelCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand RetryCommand { get; }

    public GlobalOverlayViewModel()
    {
        CancelCommand = new RelayCommand(Cancel);
        DismissCommand = new RelayCommand(Dismiss);
        RetryCommand = new RelayCommand(Retry);
    }

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

    private void Cancel()
    {
        try { _onCancel?.Invoke(); } catch {}
    }

    private void Dismiss() => Hide();

    private void Retry()
    {
        var cb = _onRetry;
        Hide();
        try { cb?.Invoke(); } catch {}
    }
}
