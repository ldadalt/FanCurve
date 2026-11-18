namespace FanCurvePanel;

static class Theme
{
    public static readonly Color Page = Color.FromArgb(246, 247, 249);
    public static readonly Color Paper = Color.White;
    public static readonly Color Ink = Color.FromArgb(32, 36, 44);
    public static readonly Color Muted = Color.FromArgb(102, 112, 128);
    public static readonly Color Line = Color.FromArgb(226, 230, 236);
    public static readonly Color Select = Color.FromArgb(226, 232, 240);

    public static void Button(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Paper;
        button.ForeColor = Ink;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    public static void Grid(DataGridView grid)
    {
        grid.BackgroundColor = Paper;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Line;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Page;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Page;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        grid.DefaultCellStyle.BackColor = Paper;
        grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Select;
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.RowHeadersVisible = false;
    }
}
