using System.Drawing;
using System.Windows.Forms;

namespace AcadDevices.DbEditor
{
    internal sealed class EditorTheme
    {
        internal static readonly EditorTheme Dark = new EditorTheme(
            Color.FromArgb(27, 30, 35), Color.FromArgb(42, 48, 57), Color.FromArgb(33, 38, 45),
            Color.FromArgb(228, 234, 242), Color.FromArgb(70, 81, 95), Color.FromArgb(255, 161, 157));
        internal static readonly EditorTheme Light = new EditorTheme(
            Color.FromArgb(240, 243, 247), Color.FromArgb(227, 233, 241), Color.White,
            Color.FromArgb(31, 41, 55), Color.FromArgb(163, 174, 189), Color.FromArgb(167, 31, 38));
        private static readonly Color Accent = Color.FromArgb(40, 92, 164);
        private readonly Color window;
        private readonly Color surface;
        private readonly Color border;

        private EditorTheme(Color window, Color surface, Color input, Color text, Color border, Color error)
        {
            this.window = window;
            this.surface = surface;
            this.border = border;
            Input = input;
            Text = text;
            Error = error;
        }

        internal Color Input { get; }
        internal Color Text { get; }
        internal Color Error { get; }

        internal void ApplyControls(Control control, Button primary)
        {
            control.ForeColor = Text;
            control.BackColor = window;
            if (control is TextBox || control is TreeView)
                control.BackColor = Input;
            if (control is TextBox input)
                input.BorderStyle = BorderStyle.FixedSingle;
            if (control is Button button)
            {
                var isPrimary = button == primary;
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = isPrimary ? Accent : surface;
                button.ForeColor = isPrimary ? Color.White : Text;
                button.FlatAppearance.BorderColor = border;
                button.FlatAppearance.MouseOverBackColor = isPrimary ? Color.FromArgb(47, 107, 188) : window;
            }

            if (!(control is DataGridView))
                foreach (Control child in control.Controls)
                    ApplyControls(child, primary);
        }

        internal void ApplyGrid(DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Input;
            grid.GridColor = border;
            grid.ColumnHeadersDefaultCellStyle.BackColor = surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = surface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
            grid.DefaultCellStyle.BackColor = Input;
            grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Accent;
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AlternatingRowsDefaultCellStyle.BackColor = window;
        }
    }
}
