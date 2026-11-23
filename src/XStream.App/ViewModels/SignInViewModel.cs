using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Xbox;
using XStream.Player;

namespace XStream.App.ViewModels;

public enum SignInState { Idle, Requesting, Waiting, Finishing, Failed }

/// <summary>Device-code sign-in: show a short code, the player approves it on their phone.</summary>
public sealed partial class SignInViewModel(XboxService xbox) : ObservableObject
{
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _countdown;
    private DateTime _expires;

    public event Action? SignedIn;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy), nameof(IsWaiting), nameof(IsFailed))]
    public partial SignInState State { get; set; }

    [ObservableProperty] public partial string UserCode { get; set; } = "";
    [ObservableProperty] public partial string VerificationUri { get; set; } = "";
    [ObservableProperty] public partial string ExpiresText { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial bool Copied { get; set; }

    public bool IsIdle => State == SignInState.Idle;
    public bool IsBusy => State is SignInState.Requesting or SignInState.Finishing;
    public bool IsWaiting => State == SignInState.Waiting;
    public bool IsFailed => State == SignInState.Failed;

    [RelayCommand]
    private async Task StartAsync()
    {
        Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Error = "";
        State = SignInState.Requesting;
        try
        {
            var prompt = await xbox.StartSignInAsync(ct);
            UserCode = prompt.UserCode;
            VerificationUri = prompt.VerificationUri;
            _expires = DateTime.UtcNow.AddSeconds(prompt.ExpiresInSec);
            StartCountdown();
            State = SignInState.Waiting;
            await xbox.WaitForApprovalAsync(prompt, ct);
            StopCountdown();
            State = SignInState.Finishing;
            await xbox.FinishSignInAsync(ct);
            State = SignInState.Idle;
            SignedIn?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { StopCountdown(); State = SignInState.Idle; }
        catch (TimeoutException) { Fail("The code expired before it was approved. Get a new code and try again."); }
        catch (Exception ex) { Fail(PlayerSession.Friendly(ex)); }
    }

    [RelayCommand]
    private void OpenLink()
    {
        if (VerificationUri.Length > 0) Platform.Open(VerificationUri);
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        await Platform.CopyAsync(UserCode);
        Copied = true;
        await Task.Delay(1500);
        Copied = false;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        StopCountdown();
        if (State != SignInState.Failed) State = SignInState.Idle;
    }

    private void Fail(string message)
    {
        StopCountdown();
        Error = message;
        State = SignInState.Failed;
    }

    private void StartCountdown()
    {
        _countdown?.Stop();
        _countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateExpires());
        _countdown.Start();
        UpdateExpires();
    }

    private void StopCountdown() { _countdown?.Stop(); _countdown = null; }

    private void UpdateExpires()
    {
        var left = _expires - DateTime.UtcNow;
        ExpiresText = left > TimeSpan.Zero ? $"Code expires in {left:m\\:ss}" : "Code expired";
    }
}
