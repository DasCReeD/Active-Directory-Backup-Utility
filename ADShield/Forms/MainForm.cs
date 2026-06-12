using ADShield.Core;
using ADShield.Models;

namespace ADShield.Forms;

public partial class MainForm : Form
{
    private AppSettings         _settings;
    private List<ComputerEntry> _computers;
    private SchedulerService    _scheduler;

    private Panel _pnlContent = null!;
    private Label _lblPageTitle = null!, _lblBreadcrumb = null!;
    private Panel _pgDashboard = null!, _pgComputers = null!, _pgLogs = null!, _pgSettings = null!;

    private Label        _kpiRate = null!, _kpiDisc = null!, _kpiOnline = null!, _kpiVault = null!;
    private DataGridView _gridDash = null!;
    private RichTextBox  _rtbLog   = null!;

    private DataGridView _gridComp = null!;
    private TextBox      _tbSearch = null!;

    private DataGridView _gridLogs = null!;
    private ComboBox     _cbLevel  = null!;
    private readonly List<(string ts, string level, string comp, string msg)> _logRows = [];

    private TextBox  _tbVcExe = null!, _tbVcCon = null!, _tbMount = null!;
    private TextBox  _tbBackupRoot = null!, _tbVhdxSize = null!;
    private TextBox  _tbOU = null!, _tbGroup = null!;
    private CheckBox _chkSched = null!;
    private TextBox  _tbNightly = null!, _tbWeekly = null!;

    private Button? _activeNav;

    public MainForm()
    {
        _settings  = AppConfig.ReadSettings();
        _computers = AppConfig.ReadHistory();
        _scheduler = new SchedulerService(_settings);
        _scheduler.BackupTriggered += OnScheduledBackup;

        SuspendLayout();
        Text = "AD Shield — Agentless Network Backup"; Size = new Size(1300, 820);
        MinimumSize = new Size(1050, 680); BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary; Font = Theme.FontBase;
        StartPosition = FormStartPosition.CenterScreen;

        // Root: 2-column TableLayoutPanel — column 0 = sidebar (210px fixed), column 1 = content (*)
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            BackColor = Theme.Background, Padding = Padding.Empty, Margin = Padding.Empty,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        root.Controls.Add(BuildSidebar(),      0, 0);
        root.Controls.Add(BuildContentArea(),  1, 0);

        BuildDashboardPage(); BuildComputersPage(); BuildLogsPage(); BuildSettingsPage();
        ShowPage(_pgDashboard, "Operations Console", "AD Backup / Dashboard");
        ResumeLayout();

        Load += (_, _) =>
        {
            RefreshGrids();
            if (_settings.ScheduleActive) _scheduler.Start();
            Log("INFO", "SYSTEM", "AD Shield initialized. Ready for backup sequences.");
        };
        FormClosing += (_, _) => _scheduler.Dispose();
    }

    private Panel BuildSidebar()
    {
        var sb = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SidebarBg };

        var brand = new Panel { Height = 68, Dock = DockStyle.Top, BackColor = Theme.SidebarBg };
        brand.Controls.Add(new Label { Text = "⬡  AD Shield", ForeColor = Theme.Accent, Font = new Font("Segoe UI", 12f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 14) });
        brand.Controls.Add(new Label { Text = "ENTERPRISE", ForeColor = Theme.TextSecondary, Font = Theme.FontBadge, AutoSize = true, Location = new Point(20, 40) });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.SidebarBg };
        footer.Controls.Add(new Panel { Size = new Size(8, 8), Location = new Point(16, 18), BackColor = Theme.Success });
        footer.Controls.Add(new Label { Text = "Server Online", ForeColor = Theme.TextSecondary, Font = Theme.FontSmall, Location = new Point(30, 15), AutoSize = true });

        var nav = new Panel { Dock = DockStyle.Fill, BackColor = Theme.SidebarBg };
        Button N(string text, int top, Action onClick)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat,
                BackColor = Theme.SidebarBg, ForeColor = Theme.TextSecondary,
                Font = Theme.FontNavItem, TextAlign = ContentAlignment.MiddleLeft,
                Left = 8, Top = top, Width = 194, Height = 36, Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.SurfaceRaised;
            b.Click += (_, _) => { SetNav(b); onClick(); };
            return b;
        }
        var b1 = N("  ▦  Dashboard",      12,  () => ShowPage(_pgDashboard, "Operations Console", "AD Backup / Dashboard"));
        var b2 = N("  ⊞  Domain Clients", 52,  () => ShowPage(_pgComputers, "Host Inventory",     "AD Backup / Clients"));
        var b3 = N("  ≡  Operation Logs", 92,  () => ShowPage(_pgLogs,      "Log Registry",       "AD Backup / Logs"));
        var b4 = N("  ⚙  System Config",  132, () => ShowPage(_pgSettings,  "Configuration",      "AD Backup / Config"));
        nav.Controls.AddRange([b1, b2, b3, b4]);

        // WinForms docking rule: add Fill FIRST (index 0), then Top/Bottom AFTER.
        // Controls are docked in REVERSE index order, so higher index = docked first.
        sb.Controls.Add(nav);                   // index 0 — Fill, docked LAST (takes remainder)
        sb.Controls.Add(footer);                // index 1 — Bottom, docked third
        sb.Controls.Add(Theme.MakeSeparator()); // index 2 — Top
        sb.Controls.Add(brand);                 // index 3 — Top, docked FIRST (takes 68px from top)

        SetNav(b1); _activeNav = b1;
        return sb;
    }

    private void SetNav(Button b)
    {
        if (_activeNav != null) { _activeNav.BackColor = Theme.SidebarBg; _activeNav.ForeColor = Theme.TextSecondary; }
        b.BackColor = Theme.SurfaceRaised; b.ForeColor = Theme.TextPrimary; _activeNav = b;
    }


    private Panel BuildContentArea()
    {
        var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };

        // Header — TableLayoutPanel keeps title/breadcrumb left and button right reliably
        var hdr = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Color.FromArgb(0x1A, 0x21, 0x2B) };

        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            BackColor = Color.Transparent, Padding = new Padding(20, 10, 16, 8),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // breadcrumb
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // title

        _lblBreadcrumb = new Label
        {
            Text = "", ForeColor = Theme.TextSecondary, Font = Theme.FontSmall,
            AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        _lblPageTitle = new Label
        {
            Text = "", ForeColor = Theme.TextPrimary, Font = Theme.FontLarge,
            AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };

        var btnSync = Theme.MakeButton("↻  Sync Active Directory");
        btnSync.Width  = 186;
        btnSync.Click += async (_, _) => await SyncAd();

        var btnTest = Theme.MakeButton("⚙  Run Tests ▾");
        btnTest.Width  = 140;
        var testMenu = new ContextMenuStrip { BackColor = Theme.Surface, ForeColor = Theme.TextPrimary, Font = Theme.FontBase };
        testMenu.Items.Add("VHDX Self-Healing Test", null, async (_, _) => await RunDiagnosticsTest());
        testMenu.Items.Add("Backup Logic Test Suite", null, async (_, _) => await RunBackupLogicTest());
        btnTest.Click += (_, _) => testMenu.Show(btnTest, new Point(0, btnTest.Height));

        var buttonsFlow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Padding = Padding.Empty, Margin = Padding.Empty
        };
        buttonsFlow.Controls.AddRange(new Control[] { btnTest, btnSync });

        tbl.Controls.Add(_lblBreadcrumb, 0, 0);
        tbl.Controls.Add(buttonsFlow,    1, 0);
        tbl.SetRowSpan(buttonsFlow, 2);
        tbl.Controls.Add(_lblPageTitle,  0, 1);

        var borderBottom = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
        hdr.Controls.Add(borderBottom);  // Bottom border — add before Fill tbl
        hdr.Controls.Add(tbl);            // Fill — takes remainder

        // Fill FIRST (lower index), Top AFTER (higher index = docked first)
        _pnlContent = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(12) };
        outer.Controls.Add(_pnlContent);  // index 0 — Fill, docked LAST
        outer.Controls.Add(hdr);          // index 1 — Top, docked FIRST (carves 68px)
        return outer;
    }

    private void ShowPage(Panel page, string title, string crumb)
    {
        _lblPageTitle.Text = title; _lblBreadcrumb.Text = crumb;
        foreach (Control c in _pnlContent.Controls) c.Visible = false;
        page.Visible = true;
    }

    private void BuildDashboardPage()
    {
        _pgDashboard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
        var kpiRow = new FlowLayoutPanel { Height = 108, Dock = DockStyle.Top, BackColor = Theme.Background, WrapContents = false };
        _kpiRate   = KpiCard(kpiRow, "Success Rate",      "0%",     Theme.Success);
        _kpiDisc   = KpiCard(kpiRow, "Total Discovered",  "0",      Theme.Accent);
        _kpiOnline = KpiCard(kpiRow, "Active Remotes",    "0",      Theme.Warning);
        _kpiVault  = KpiCard(kpiRow, "VeraCrypt Vault",   "Locked", Theme.Danger);
        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Background, SplitterWidth = 8, SplitterDistance = 680 };
        var (tblPanel, tblHdr) = MakeCard2("Domain Computers", "Agentless VSS targets");
        _gridDash = Theme.MakeGrid();
        _gridDash.Dock = DockStyle.Fill;
        _gridDash.Columns.Add(Col("Computer", 18)); _gridDash.Columns.Add(Col("OS", 24));
        _gridDash.Columns.Add(Col("Status", 14));   _gridDash.Columns.Add(Col("Last Backup", 16));
        _gridDash.Columns.Add(Col("Time", 14));
        _gridDash.Columns.Add(new DataGridViewButtonColumn { HeaderText = "Action", FillWeight = 10, Text = "Backup", UseColumnTextForButtonValue = true });
        _gridDash.CellClick += GridDash_CellClick;
        tblPanel.Controls.Add(_gridDash);         // Fill first
        tblPanel.Controls.Add(Theme.MakeSeparator());
        tblPanel.Controls.Add(tblHdr);            // Top last
        split.Panel1.Controls.Add(tblPanel);

        var (termPanel, termHdr) = MakeCard2("Operations Terminal", "");
        _rtbLog = new RichTextBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0x08,0x0C,0x10),
            ForeColor = Theme.TextPrimary, Font = Theme.FontMono, ReadOnly = true, BorderStyle = BorderStyle.None };
        var btnClr = Theme.MakeButton("Clear"); btnClr.Height = 26; btnClr.Dock = DockStyle.Top;
        btnClr.Click += (_, _) => _rtbLog.Clear();
        termPanel.Controls.Add(_rtbLog);          // Fill first
        termPanel.Controls.Add(btnClr);           // Top
        termPanel.Controls.Add(Theme.MakeSeparator());
        termPanel.Controls.Add(termHdr);          // Top last
        split.Panel2.Controls.Add(termPanel);

        // Fill FIRST, Top AFTER
        _pgDashboard.Controls.Add(split);    // Fill — docked last
        _pgDashboard.Controls.Add(kpiRow);   // Top — docked first (carves 108px)
        _pnlContent.Controls.Add(_pgDashboard);
    }

    private void BuildComputersPage()
    {
        _pgComputers = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
        var (card, cardHdr) = MakeCard2("Detailed Domain Host Inventory", "");
        var searchRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Surface, Padding = new Padding(12,8,12,0) };
        _tbSearch = Theme.MakeTextBox(); _tbSearch.Width = 280; _tbSearch.PlaceholderText = "Filter by name or OS…";
        _tbSearch.TextChanged += (_, _) => RefreshComputersGrid();
        searchRow.Controls.Add(_tbSearch);
        _gridComp = Theme.MakeGrid();
        _gridComp.Dock = DockStyle.Fill;
        _gridComp.Columns.Add(Col("Computer Name", 16));    _gridComp.Columns.Add(Col("DNS Hostname", 22));
        _gridComp.Columns.Add(Col("Organizational Unit", 28)); _gridComp.Columns.Add(Col("Operating System", 18));
        _gridComp.Columns.Add(Col("Ping ms", 8));            _gridComp.Columns.Add(Col("State", 10));
        card.Controls.Add(_gridComp);          // Fill first
        card.Controls.Add(searchRow);          // Top
        card.Controls.Add(Theme.MakeSeparator());
        card.Controls.Add(cardHdr);            // Top last
        _pgComputers.Controls.Add(card); _pnlContent.Controls.Add(_pgComputers);
    }

    private void BuildLogsPage()
    {
        _pgLogs = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
        var (card, cardHdr) = MakeCard2("Chronological Backup Log Registry", "");
        var fRow = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Surface, Padding = new Padding(12,8,12,0) };
        _cbLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary, Width = 160, Font = Theme.FontBase };
        _cbLevel.Items.AddRange(["All Levels","INFO","SUCCESS","ERROR","WARN"]); _cbLevel.SelectedIndex = 0;
        _cbLevel.SelectedIndexChanged += (_, _) => RefreshLogsGrid();
        var btnEx = Theme.MakeButton("Export CSV"); btnEx.Width = 100; btnEx.Left = 170; btnEx.Click += ExportLogs;
        fRow.Controls.AddRange([_cbLevel, btnEx]);
        _gridLogs = Theme.MakeGrid();
        _gridLogs.Dock = DockStyle.Fill;
        _gridLogs.Columns.Add(Col("Time", 14));  _gridLogs.Columns.Add(Col("Level", 10));
        _gridLogs.Columns.Add(Col("Computer",16)); _gridLogs.Columns.Add(Col("Message", 60));
        card.Controls.Add(_gridLogs);          // Fill first
        card.Controls.Add(fRow);               // Top
        card.Controls.Add(Theme.MakeSeparator());
        card.Controls.Add(cardHdr);            // Top last
        _pgLogs.Controls.Add(card); _pnlContent.Controls.Add(_pgLogs);
    }

    private void BuildSettingsPage()
    {
        _pgSettings = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false, AutoScroll = true };

        var outerFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true, AutoScroll = true,
            BackColor = Theme.Background, Padding = new Padding(8),
        };

        // ── Card 1: VeraCrypt ─────────────────────────────────────────────────
        var (gVc, fVc) = MakeSettingsCard("VeraCrypt Encryption Store");
        _tbVcExe = AddField(fVc, "VeraCrypt Executable Path", _settings.VeraCryptExePath, @"e.g. C:\Program Files\VeraCrypt\VeraCrypt.exe");
        _tbVcCon = AddField(fVc, "Encrypted Container File (.hc)", _settings.VeraCryptContainer, @"e.g. E:\BackupVault.hc");

        // Volume size + Create button
        var tbVolSize = AddField(fVc, "New Container Size (e.g. 500G, 2T)", "", "e.g. 500G for 500 GB  or  2T for 2 TB");
        var btnCreate = Theme.MakeButton("🔒  Create Encrypted Volume");
        btnCreate.Width = 460; btnCreate.Margin = new Padding(0, 6, 0, 0);
        btnCreate.Click += (_, _) => CreateVeraCryptVolume(tbVolSize.Text.Trim());
        fVc.Controls.Add(btnCreate);

        fVc.Controls.Add(new Panel { Height = 1, Width = 460, BackColor = Theme.Border, Margin = new Padding(0, 10, 0, 6) });

        _tbMount = AddField(fVc, "Mount Drive Letter (single char A–Z)", _settings.MountLetter, "e.g. V");
        _tbMount.MaxLength = 1;
        var bVc = Theme.MakeButton("Save Vault Config", primary: true);
        bVc.Width = 460; bVc.Margin = new Padding(0, 10, 0, 0);
        bVc.Click += SaveVeraCryptSettings;
        fVc.Controls.Add(bVc);

        // ── Card 2: Backup Storage ────────────────────────────────────────────
        var (gSt, fSt) = MakeSettingsCard("Backup Storage Location");
        _tbBackupRoot = AddField(fSt, "Backup Root Folder (inside mounted volume)", _settings.BackupStorageRoot, "e.g. backups");
        _tbVhdxSize   = AddField(fSt, "VHDX Size per Machine (GB)", _settings.VhdxSizeGb.ToString(), "e.g. 1024 for 1 TB");
        fSt.Controls.Add(new Label
        {
            Text = $"Path:  {_settings.MountLetter}:\\{_settings.BackupStorageRoot}\\<Computer>\\disk.vhdx",
            ForeColor = Theme.Accent, Font = Theme.FontSmall,
            AutoSize = false, Width = 460, Height = 20,
            Margin = new Padding(0, 4, 0, 0),
        });
        var bSt = Theme.MakeButton("Save Storage Config", primary: true);
        bSt.Width = 460; bSt.Margin = new Padding(0, 10, 0, 0);
        bSt.Click += SaveStorageSettings;
        fSt.Controls.Add(bSt);

        // ── Card 3: AD & Schedule ─────────────────────────────────────────────
        var (gAd, fAd) = MakeSettingsCard("AD Targeting & Automation");
        _tbOU    = AddField(fAd, "Search OU (leave blank = entire domain)", _settings.SearchOU, "e.g. OU=Workstations,DC=corp,DC=local");
        _tbGroup = AddField(fAd, "AD Security Group Filter (blank = ALL computers)", _settings.AdGroup, "e.g. Backup-Targets — or leave blank");
        _chkSched = new CheckBox
        {
            Text = "Enable Automated Backup Schedule",
            ForeColor = Theme.TextPrimary, Font = Theme.FontBase,
            AutoSize = true, Checked = _settings.ScheduleActive,
            Margin = new Padding(0, 8, 0, 4),
        };
        fAd.Controls.Add(_chkSched);
        _tbNightly = AddField(fAd, "Nightly Incremental — Cron", _settings.NightlyCron, "e.g. 0 1 * * *");
        _tbWeekly  = AddField(fAd, "Weekly Full — Cron", _settings.WeeklyCron, "e.g. 0 0 * * 0");
        var bAd = Theme.MakeButton("Save AD Settings", primary: true);
        bAd.Width = 460; bAd.Margin = new Padding(0, 10, 0, 0);
        bAd.Click += SaveAdSettings;
        fAd.Controls.Add(bAd);

        outerFlow.Controls.AddRange([gVc, gSt, gAd]);
        _pgSettings.Controls.Add(outerFlow);
        _pnlContent.Controls.Add(_pgSettings);
    }

    // ── Widget helpers ────────────────────────────────────────────────────────

    private static Label KpiCard(FlowLayoutPanel p, string title, string val, Color color)
    {
        var card = new Panel { Width = 225, Height = 98, Margin = new Padding(0, 0, 12, 0), BackColor = Theme.Surface };
        card.Controls.Add(new Panel { Width = 4, Height = 58, Location = new Point(0, 20), BackColor = color });
        card.Controls.Add(new Label { Text = title, ForeColor = Theme.TextSecondary, Font = Theme.FontSmall, AutoSize = true, Location = new Point(16, 14) });
        var v = new Label { Text = val, ForeColor = Theme.TextPrimary, Font = Theme.FontKpi, AutoSize = true, Location = new Point(16, 34) };
        card.Controls.Add(v);
        p.Controls.Add(card); return v;
    }

    /// <summary>
    /// Creates a card panel with a titled header. Returns (card, header).
    /// CALLER must add content (Fill) to card FIRST, then add header (Top) LAST.
    /// </summary>
    private (Panel card, Panel header) MakeCard2(string title, string sub)
    {
        var c = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        var h = new Panel { Height = 46, Dock = DockStyle.Top, BackColor = Theme.Surface };
        h.Controls.Add(new Label { Text = title, ForeColor = Theme.TextPrimary, Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 8) });
        if (!string.IsNullOrEmpty(sub)) h.Controls.Add(new Label { Text = sub, ForeColor = Theme.TextSecondary, Font = Theme.FontSmall, AutoSize = true, Location = new Point(16, 28) });
        return (c, h);
    }

    /// <summary>
    /// Creates a settings card using FlowLayoutPanel (TopDown) for correct control ordering.
    /// Returns (outerPanel, contentFlow) — add fields to contentFlow, add outerPanel to page.
    /// </summary>
    private static (Panel card, FlowLayoutPanel content) MakeSettingsCard(string title)
    {
        const int W = 500;
        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = W, BackColor = Theme.Surface, Padding = new Padding(20, 10, 20, 14),
        };

        var card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Width = W, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface, Margin = new Padding(0, 0, 16, 16),
            Padding = new Padding(0),
        };
        card.Controls.Add(new Label
        {
            Text = title, ForeColor = Theme.Accent,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = false, Width = W - 40, Height = 28,
            Margin = new Padding(20, 14, 20, 0),
        });
        card.Controls.Add(new Panel { Height = 1, Width = W - 40, BackColor = Theme.Border, Margin = new Padding(20, 2, 20, 0) });
        card.Controls.Add(content);
        return (card, content);
    }

    /// <summary>Adds a label + textbox pair into a FlowLayoutPanel. Order is preserved.</summary>
    private static TextBox AddField(FlowLayoutPanel parent, string label, string value, string placeholder = "")
    {
        parent.Controls.Add(new Label
        {
            Text = label, ForeColor = Theme.TextSecondary, Font = Theme.FontSmall,
            AutoSize = false, Width = 460, Height = 18, Margin = new Padding(0, 6, 0, 2),
        });
        var tb = Theme.MakeTextBox();
        tb.Text = value; tb.Width = 460;
        if (!string.IsNullOrEmpty(placeholder)) tb.PlaceholderText = placeholder;
        parent.Controls.Add(tb);
        return tb;
    }

    private static DataGridViewTextBoxColumn Col(string hdr, float fill) =>
        new() { HeaderText = hdr, FillWeight = fill };
}
