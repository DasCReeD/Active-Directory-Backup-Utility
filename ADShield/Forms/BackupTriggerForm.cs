namespace ADShield.Forms;

/// <summary>Modal dialog for triggering a backup on a specific machine.</summary>
public class BackupTriggerForm : Form
{
    public string  SelectedBackupType { get; private set; } = "Incremental";
    public string  VeraCryptPassword  { get; private set; } = string.Empty;

    private readonly string _computerName;
    private RadioButton _rbIncremental = null!;
    private RadioButton _rbFull        = null!;
    private TextBox     _tbPassword    = null!;
    private Button      _btnStart      = null!;
    private Button      _btnCancel     = null!;

    public BackupTriggerForm(string computerName)
    {
        _computerName = computerName;
        BuildUI();
    }

    private void BuildUI()
    {
        Text            = "Initiate Agentless Backup Session";
        Size            = new Size(480, 360);
        MinimumSize     = new Size(480, 360);
        MaximumSize     = new Size(480, 360);
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Theme.Surface;
        ForeColor       = Theme.TextPrimary;
        Font            = Theme.FontBase;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var layout = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            RowCount    = 5,
            ColumnCount = 1,
            Padding     = new Padding(24, 20, 24, 16),
            BackColor   = Theme.Surface,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // title
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // target label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // backup type
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // password
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); // buttons

        // Title
        var lblTitle = new Label
        {
            Text      = "Backup Session",
            Font      = Theme.FontLarge,
            ForeColor = Theme.TextPrimary,
            AutoSize  = true,
            Padding   = new Padding(0, 0, 0, 4),
        };
        layout.Controls.Add(lblTitle, 0, 0);

        // Target computer label
        var lblTarget = new Label
        {
            Text      = $"Target Host:  {_computerName}",
            ForeColor = Theme.TextSecondary,
            Font      = Theme.FontBase,
            AutoSize  = true,
            Padding   = new Padding(0, 0, 0, 16),
        };
        layout.Controls.Add(lblTarget, 0, 1);

        // Backup type group
        var grpType = Theme.MakeGroupBox("Backup Sequence Type");
        grpType.Dock   = DockStyle.Fill;
        grpType.Height = 90;

        _rbIncremental = new RadioButton
        {
            Text      = "Incremental Delta  (VSS block update — faster)",
            Checked   = true,
            ForeColor = Theme.TextPrimary,
            Font      = Theme.FontBase,
            AutoSize  = true,
            Location  = new Point(10, 24),
        };
        _rbFull = new RadioButton
        {
            Text      = "Full System Image  (new complete snapshot)",
            ForeColor = Theme.TextPrimary,
            Font      = Theme.FontBase,
            AutoSize  = true,
            Location  = new Point(10, 52),
        };
        grpType.Controls.Add(_rbIncremental);
        grpType.Controls.Add(_rbFull);
        layout.Controls.Add(grpType, 0, 2);

        // Password group
        var grpPass = Theme.MakeGroupBox("VeraCrypt Vault Passphrase");
        grpPass.Dock   = DockStyle.Fill;
        grpPass.Height = 80;

        _tbPassword = Theme.MakeTextBox(password: true);
        _tbPassword.Dock        = DockStyle.Fill;
        _tbPassword.PlaceholderText = "Enter server decryption key…";
        _tbPassword.Margin      = new Padding(0, 4, 0, 0);
        grpPass.Controls.Add(_tbPassword);
        layout.Controls.Add(grpPass, 0, 3);

        // Buttons row
        var btnPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor     = Theme.Surface,
            Padding       = new Padding(0, 12, 0, 0),
        };

        _btnStart = Theme.MakeButton("▶  Start Remote Session", primary: true);
        _btnStart.Width  = 200;
        _btnStart.Click += BtnStart_Click;

        _btnCancel = Theme.MakeButton("Cancel");
        _btnCancel.Width  = 90;
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        btnPanel.Controls.Add(_btnStart);
        btnPanel.Controls.Add(_btnCancel);
        layout.Controls.Add(btnPanel, 0, 4);

        Controls.Add(layout);
        AcceptButton = _btnStart;
        CancelButton = _btnCancel;
    }

    private void BtnStart_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_tbPassword.Text))
        {
            MessageBox.Show("Please enter the VeraCrypt vault passphrase.",
                "Required Field", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _tbPassword.Focus();
            return;
        }
        SelectedBackupType = _rbFull.Checked ? "Full" : "Incremental";
        VeraCryptPassword  = _tbPassword.Text;
        DialogResult       = DialogResult.OK;
        Close();
    }
}
