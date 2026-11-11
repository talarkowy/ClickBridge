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
    private readonly Label _feedback = CreateLabel(string.Empty);
    private readonly Label _bridgeStatus = CreateLabel(string.Empty);

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
        ClientSize = new Size(448, 306);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            Padding = new Padding(20, 16, 20, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        buttons.Controls.AddRange([_signIn, _signOut]);

        Add(_sessionStatus, 0);
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

    public string Email => _email.Text.Trim();

    public string TakePassword()
    {
        var password = _password.Text;
        _password.Clear();
        return password;
    }

    public void UpdateStatus(string sessionStatus, string bridgeStatus, bool editingEnabled, bool canSignOut)
    {
        _sessionStatus.Text = sessionStatus;
        _bridgeStatus.Text = bridgeStatus;
        _email.Enabled = editingEnabled;
        _password.Enabled = editingEnabled;
        _signIn.Enabled = editingEnabled;
        _signOut.Enabled = editingEnabled && canSignOut;
    }

    public void ShowFeedback(string message, bool error = false)
    {
        _feedback.ForeColor = error && !SystemInformation.HighContrast ? Color.Firebrick : SystemColors.ControlText;
        _feedback.Text = message;
        _feedback.Visible = message.Length > 0;
    }

    private static Label CreateLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top };
}
