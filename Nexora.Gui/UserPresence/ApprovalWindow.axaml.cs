using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nexora.Gui.Security;
using Nexora.Gui.Telemetry;

namespace Nexora.Gui.UserPresence;

public partial class ApprovalWindow : Window
{
    private const int TimeoutSeconds = 30;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft = TimeoutSeconds;
    private bool _resultSet;
    public bool Result { get; private set; }

    public ApprovalWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ApprovalPin.Focus();
        Closed += (_, _) => _timer.Stop();
    }

    public ApprovalWindow(string deviceLabel, string operation, string rpId) : this()
    {
        DeviceText.Text = deviceLabel;
        OperationText.Text = operation switch
        {
            "Register a new key" => "Enregistrement d'une nouvelle clé",
            "Sign in" => "Demande de connexion FIDO2",
            _ => operation
        };
        RpText.Text = rpId;
        CountdownBar.Maximum = TimeoutSeconds;
        CountdownBar.Value = _secondsLeft;
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            CountdownBar.Value = Math.Max(0, _secondsLeft);
            CountdownText.Text = $"Refus automatique dans {_secondsLeft} s";
            if (_secondsLeft <= 0) CloseWithResult(false);
        };
        _timer.Start();
    }

    private void ApprovalPin_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Approve();
    }

    private void Approve_Click(object? sender, RoutedEventArgs e) => Approve();

    private void Approve()
    {
        if (!VaultAccessController.Instance.VerifyApproval(ApprovalPin.Text ?? string.Empty, out var error))
        {
            ApprovalError.Text = error;
            ApprovalError.IsVisible = true;
            SecurityAudit.Instance.Record("PIN", "approval-pin-failed");
            ApprovalPin.Text = string.Empty;
            ApprovalPin.Focus();
            return;
        }
        ApprovalPin.Text = string.Empty;
        CloseWithResult(true);
    }

    private void Deny_Click(object? sender, RoutedEventArgs e) => CloseWithResult(false);

    private void CloseWithResult(bool approved)
    {
        if (_resultSet) return;
        _resultSet = true;
        Result = approved;
        SecurityAudit.Instance.Record("FIDO", approved ? "approved" : "denied", "user-presence");
        _timer.Stop();
        Close(approved);
    }
}
