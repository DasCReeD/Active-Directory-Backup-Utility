namespace ADShield.Forms;

/// <summary>Dark theme palette and shared fonts for AD Shield WinForms UI.</summary>
public static class Theme
{
    // ── Colour Palette ────────────────────────────────────────────────────────
    public static readonly Color Background     = Color.FromArgb(0x0D, 0x11, 0x17); // #0D1117
    public static readonly Color Surface        = Color.FromArgb(0x16, 0x1B, 0x22); // #161B22
    public static readonly Color SurfaceRaised  = Color.FromArgb(0x1C, 0x23, 0x2C); // #1C232C
    public static readonly Color Border         = Color.FromArgb(0x30, 0x36, 0x3D); // #30363D
    public static readonly Color Accent         = Color.FromArgb(0x58, 0xA6, 0xFF); // #58A6FF
    public static readonly Color AccentDim      = Color.FromArgb(0x1F, 0x6F, 0xEB); // #1F6FEB
    public static readonly Color Success        = Color.FromArgb(0x3F, 0xB9, 0x50); // #3FB950
    public static readonly Color Warning        = Color.FromArgb(0xD2, 0x99, 0x22); // #D29922
    public static readonly Color Danger         = Color.FromArgb(0xF8, 0x51, 0x49); // #F85149
    public static readonly Color TextPrimary    = Color.FromArgb(0xE6, 0xED, 0xF3); // #E6EDF3
    public static readonly Color TextSecondary  = Color.FromArgb(0x8B, 0x94, 0x9E); // #8B949E
    public static readonly Color TextMuted      = Color.FromArgb(0x48, 0x4F, 0x58); // #484F58
    public static readonly Color SidebarBg      = Color.FromArgb(0x10, 0x16, 0x1D); // slightly darker
    public static readonly Color NavHover       = Color.FromArgb(0x1C, 0x23, 0x2C);
    public static readonly Color NavActive      = Color.FromArgb(0x1F, 0x6F, 0xEB, 0x33); // translucent

    // Status colours for DataGridView cells
    public static Color StatusColor(string status) => status switch
    {
        "Success"          => Success,
        "In Progress"      => Warning,
        "Failed"           => Danger,
        "Never Backed Up"  => TextMuted,
        _                  => TextSecondary
    };

    public static Color OnlineColor(bool online) => online ? Success : Danger;

    // ── Fonts ─────────────────────────────────────────────────────────────────
    public static readonly Font FontBase       = new("Segoe UI",     9f,  FontStyle.Regular);
    public static readonly Font FontSmall      = new("Segoe UI",     8f,  FontStyle.Regular);
    public static readonly Font FontMedium     = new("Segoe UI",    10f,  FontStyle.Regular);
    public static readonly Font FontLarge      = new("Segoe UI",    13f,  FontStyle.Bold);
    public static readonly Font FontTitle      = new("Segoe UI",    18f,  FontStyle.Bold);
    public static readonly Font FontMono       = new("Consolas",     9f,  FontStyle.Regular);
    public static readonly Font FontBadge      = new("Segoe UI",     7f,  FontStyle.Bold);
    public static readonly Font FontNavItem    = new("Segoe UI",     9.5f, FontStyle.Regular);
    public static readonly Font FontKpi        = new("Segoe UI",    22f,  FontStyle.Bold);

    // ── Control Theming Helpers ───────────────────────────────────────────────

    public static Button MakeButton(string text, bool primary = false)
    {
        var btn = new Button
        {
            Text        = text,
            FlatStyle   = FlatStyle.Flat,
            BackColor   = primary ? AccentDim : SurfaceRaised,
            ForeColor   = TextPrimary,
            Font        = FontBase,
            Height      = 34,
            Cursor      = Cursors.Hand,
            AutoSize    = false,
        };
        btn.FlatAppearance.BorderColor    = primary ? AccentDim : Border;
        btn.FlatAppearance.BorderSize     = 1;
        btn.FlatAppearance.MouseOverBackColor  = primary
            ? Color.FromArgb(0x38, 0x8B, 0xFF)
            : Color.FromArgb(0x24, 0x2D, 0x39);
        return btn;
    }

    public static TextBox MakeTextBox(bool password = false)
    {
        var tb = new TextBox
        {
            BackColor     = SurfaceRaised,
            ForeColor     = TextPrimary,
            Font          = FontBase,
            BorderStyle   = BorderStyle.FixedSingle,
        };
        if (password) tb.PasswordChar = '●';
        return tb;
    }

    public static Label MakeLabel(string text, bool secondary = false)
    {
        return new Label
        {
            Text      = text,
            ForeColor = secondary ? TextSecondary : TextPrimary,
            Font      = FontBase,
            AutoSize  = true,
        };
    }

    public static Panel MakeSeparator() =>
        new Panel { Height = 1, BackColor = Border, Dock = DockStyle.Top };

    public static DataGridView MakeGrid()
    {
        var grid = new DataGridView
        {
            BackgroundColor         = Surface,
            GridColor               = Border,
            ForeColor               = TextPrimary,
            Font                    = FontBase,
            BorderStyle             = BorderStyle.None,
            CellBorderStyle         = DataGridViewCellBorderStyle.SingleHorizontal,
            RowHeadersVisible       = false,
            AllowUserToAddRows      = false,
            AllowUserToDeleteRows   = false,
            AllowUserToResizeRows   = false,
            SelectionMode           = DataGridViewSelectionMode.FullRowSelect,
            ReadOnly                = true,
            AutoSizeColumnsMode     = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight     = 36,
            RowTemplate             = { Height = 32 },
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor   = SurfaceRaised,
            ForeColor   = TextSecondary,
            Font        = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            SelectionBackColor = SurfaceRaised,
            SelectionForeColor = TextSecondary,
            Padding     = new Padding(8, 0, 0, 0),
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor   = Surface,
            ForeColor   = TextPrimary,
            SelectionBackColor = Color.FromArgb(0x1F, 0x6F, 0xEB, 0x40),
            SelectionForeColor = TextPrimary,
            Padding     = new Padding(6, 0, 0, 0),
        };
        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor   = Color.FromArgb(0x13, 0x19, 0x20),
            ForeColor   = TextPrimary,
            SelectionBackColor = Color.FromArgb(0x1F, 0x6F, 0xEB, 0x40),
            SelectionForeColor = TextPrimary,
            Padding     = new Padding(6, 0, 0, 0),
        };
        return grid;
    }

    public static GroupBox MakeGroupBox(string title)
    {
        return new GroupBox
        {
            Text      = title,
            ForeColor = Accent,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            BackColor = Surface,
            Padding   = new Padding(10, 16, 10, 10),
        };
    }
}
