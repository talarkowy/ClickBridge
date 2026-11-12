using ClickBridge.Core.Mapping;

namespace ClickBridge.Tray;

internal sealed class AccountWindow : Form
{
    private readonly TextBox _email = new() { Dock = DockStyle.Top, AccessibleName = "E-mail Zwift" };
    private readonly TextBox _password = new()
    {
        Dock = DockStyle.Top,
        AccessibleName = "Hasło Zwift",
        UseSystemPasswordChar = true,
        ShortcutsEnabled = true,
    };
    private readonly Button _signIn = new() { Text = "Zaloguj się", AutoSize = true };
    private readonly Button _signOut = new() { Text = "Wyloguj się", AutoSize = true };
    private readonly Label _sessionStatus = CreateLabel(string.Empty);
    private readonly Label _feedback = new() { AutoSize = true, Dock = DockStyle.Top, Visible = false };
    private readonly Label _bridgeStatus = CreateLabel(string.Empty);
    private readonly ComboBox _mode = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Tryb pracy" };
    private readonly Button _applyMode = new() { Text = "Zastosuj tryb", AutoSize = true };
    private readonly Label _modeHint = CreateLabel(string.Empty);
    private readonly TableLayoutPanel _layout;

    /// <summary>Display names indexed by <see cref="BridgeMode"/>; shared by the window and the tray menu.</summary>
    public static readonly string[] ModeNames =
        ["Oba kontrolery — konto Zwift", "Tylko prawy kontroler", "Oba bez konta — reset lewego (eksperymentalny)"];

    public AccountWindow(Icon icon)
    {
        Text = "ClickBridge";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(500, 450); // the height is fitted to the content in FitHeight

        var layout = _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        buttons.Controls.AddRange([_signIn, _signOut]);

        _mode.Items.AddRange(ModeNames);
        _mode.SelectedIndex = 0;
        Add(CreateLabel("Tryb pracy"), 0);
        Add(_mode, 4);
        Add(_applyMode, 6);
        Add(_modeHint, 4);
        Add(_sessionStatus, 12);
        Add(CreateLabel("E-&mail Zwift"), 12);
        Add(_email, 2);
        Add(CreateLabel("&Hasło Zwift"), 8);
        Add(_password, 2);
        Add(buttons, 8);
        Add(_feedback, 4);
        Add(_bridgeStatus, 12);
        Controls.Add(layout);
        AcceptButton = _signIn;
        ActiveControl = _email;
        _signIn.Click += (_, _) => SignInRequested?.Invoke(this, EventArgs.Empty);
        _signOut.Click += (_, _) => SignOutRequested?.Invoke(this, EventArgs.Empty);
        _applyMode.Click += (_, _) => ModeChangeRequested?.Invoke(this, EventArgs.Empty);

        void Add(Control control, int spaceAbove)
        {
            control.Margin = new Padding(0, spaceAbove, 0, 0);
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
        }
    }

    public event EventHandler? SignInRequested;
    public event EventHandler? SignOutRequested;
    public event EventHandler? ModeChangeRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public BridgeMode SelectedMode
    {
        get => (BridgeMode)_mode.SelectedIndex;
        set => _mode.SelectedIndex = (int)value;
    }

    public string Email => _email.Text.Trim();

    public string TakePassword()
    {
        var password = _password.Text;
        _password.Clear();
        return password;
    }

    public void UpdateStatus(string sessionStatus, string bridgeStatus, bool editingEnabled, bool canSignOut, BridgeMode activeMode)
    {
        _sessionStatus.Text = sessionStatus;
        _bridgeStatus.Text = bridgeStatus;
        _mode.Enabled = editingEnabled;
        _applyMode.Enabled = editingEnabled;
        _modeHint.Text = activeMode switch
        {
            BridgeMode.OfflineReset => "Aktywny reset lewego bez konta Zwift.\nPodczas restartu oba kontrolery mogą nie reagować.",
            BridgeMode.RightOnly => "Aktywny tylko prawy kontroler. + zwiększa bieg, B zmniejsza.",
            _ => "Aktywny tryb konta Zwift z automatyczną autoryzacją.",
        };
        _email.Enabled = editingEnabled && activeMode == BridgeMode.Auto;
        _password.Enabled = editingEnabled && activeMode == BridgeMode.Auto;
        _signIn.Enabled = editingEnabled && activeMode == BridgeMode.Auto;
        _signOut.Enabled = editingEnabled && canSignOut;
        FitHeight();
    }

    public void ShowFeedback(string message, bool error = false)
    {
        _feedback.ForeColor = error && !SystemInformation.HighContrast ? Color.Firebrick : SystemColors.ControlText;
        _feedback.Text = message;
        _feedback.Visible = message.Length > 0;
        FitHeight();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitHeight(); // after DPI scaling of the initial size
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitHeight();
    }

    /// <summary>Sizes the window to its content, so it grows only when a hint or message needs more lines.</summary>
    private void FitHeight()
    {
        var height = _layout.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        if (height != ClientSize.Height)
        {
            ClientSize = new Size(ClientSize.Width, height);
        }
    }

    private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top };
}
