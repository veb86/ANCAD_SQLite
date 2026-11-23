using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using AcadDevices.Core;

namespace AcadDevices.DbEditor
{
    internal sealed class DbEditorForm : Form
    {
        private const string DatabaseFilter = "База данных SQLite (*.db)|*.db";
        private readonly ToolTip toolTip = new ToolTip();
        private readonly Label path = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "База данных не открыта",
            TextAlign = ContentAlignment.MiddleLeft
        };
        private readonly TreeNode tables = new TreeNode("Таблицы");
        private readonly TreeNode views = new TreeNode("Именованные запросы");
        private readonly TreeView navigation = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            HideSelection = false,
            ShowNodeToolTips = true,
            AccessibleName = "Объекты базы данных"
        };
        private readonly Label status = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Состояние"
        };
        private readonly TextBox sql = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsReturn = true,
            Font = new Font(FontFamily.GenericMonospace, 10),
            AccessibleName = "SQL-запрос"
        };
        private readonly CheckBox dark = new CheckBox { Text = "Тёмная тема", Checked = true, AutoSize = true };
        private readonly BindingSource rows = new BindingSource();
        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BorderStyle = BorderStyle.FixedSingle,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            RowHeadersVisible = false,
            AccessibleName = "Записи"
        };
        private readonly ColumnFilterBar filters;
        private readonly Button createFile;
        private readonly Button openFile;
        private readonly Button addRow;
        private readonly Button deleteRow;
        private readonly Button save;
        private readonly Button cancel;
        private readonly Button execute;
        private EditorTheme theme;
        private SqliteDatabase database;
        private SqliteTable table;
        private TreeNode loadedNode;
        private bool changingSelection;
        private bool statusIsError;

        internal DbEditorForm()
        {
            Text = "Редактор базы данных SQLite";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9);
            ClientSize = new Size(1080, 610);
            MinimumSize = new Size(880, 440);
            ShowIcon = false;
            ShowInTaskbar = false;

            createFile = MakeFileButton("Создать новую БД", CreateDatabase);
            openFile = MakeFileButton("Открыть БД", OpenDatabase);
            addRow = MakeButton("Добавить строку", () => Run(AddRow));
            deleteRow = MakeButton("Удалить строку", () => Run(DeleteRows));
            save = MakeButton("Сохранить изменения", () => Run(Save));
            cancel = MakeButton("Отменить", () => Run(Cancel));
            execute = MakeButton("Выполнить", ExecuteSql);
            toolTip.SetToolTip(execute, "Выполнить SQL (Ctrl+Enter)");

            navigation.Nodes.AddRange(new[] { tables, views });
            filters = new ColumnFilterBar(grid, toolTip);
            filters.FiltersChanged += (sender, args) => Run(ApplyFilters);
            BuildLayout();

            navigation.AfterSelect += (sender, args) => ChangeSelection(args.Node);
            sql.KeyDown += (sender, args) =>
            {
                if (args.Control && args.KeyCode == Keys.Enter)
                {
                    args.SuppressKeyPress = true;
                    ExecuteSql();
                }
            };
            sql.TextChanged += (sender, args) => UpdateControls();
            dark.CheckedChanged += (sender, args) => ApplyTheme();
            grid.CellDoubleClick += (sender, args) =>
            {
                if (!grid.ReadOnly && args.RowIndex >= 0 && args.ColumnIndex >= 0)
                    grid.BeginEdit(true);
            };
            grid.CellParsing += ParseCell;
            grid.EditingControlShowing += (sender, args) =>
            {
                args.Control.BackColor = theme.Input;
                args.Control.ForeColor = theme.Text;
            };
            grid.KeyDown += (sender, args) =>
            {
                if (table != null && args.KeyCode == Keys.Delete && !grid.IsCurrentCellInEditMode)
                {
                    args.SuppressKeyPress = true;
                    Run(DeleteRows);
                }
            };
            grid.DataError += (sender, args) => SetStatus(args.Exception.Message, isError: true);

            ApplyTheme();
            UpdateControls();
        }

        private void BuildLayout()
        {
            var fileButtons = MakeBar(createFile, openFile);
            var fileBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            fileBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fileBar.Controls.Add(fileButtons, 0, 0);
            fileBar.Controls.Add(path, 1, 0);

            var dataPane = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(8, 0, 0, 0),
                ColumnCount = 1,
                RowCount = 3
            };
            dataPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dataPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            dataPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dataPane.Controls.Add(MakeBar(addRow, deleteRow, save, cancel), 0, 0);
            dataPane.Controls.Add(filters, 0, 1);
            dataPane.Controls.Add(grid, 0, 2);

            var workspace = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 6),
                ColumnCount = 2,
                RowCount = 1
            };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 218));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workspace.Controls.Add(navigation, 0, 0);
            workspace.Controls.Add(dataPane, 1, 0);

            var queryPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            queryPane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            queryPane.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            queryPane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            queryPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            queryPane.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            queryPane.Controls.Add(new Label { Text = "SQL-запрос", AutoSize = true }, 0, 0);
            queryPane.Controls.Add(sql, 0, 1);
            queryPane.Controls.Add(execute, 1, 1);
            queryPane.Controls.Add(status, 0, 2);
            queryPane.Controls.Add(dark, 1, 2);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(14)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
            layout.Controls.Add(fileBar, 0, 0);
            layout.Controls.Add(workspace, 0, 1);
            layout.Controls.Add(queryPane, 0, 2);
            Controls.Add(layout);
        }

        internal void SetDatabase(SqliteDatabase selected)
        {
            var tableNames = selected.GetTables();
            var viewNames = selected.GetViews();
            database = selected;
            path.Text = selected.FilePath;
            toolTip.SetToolTip(path, selected.FilePath);
            SelectNode(null);
            ShowData(null);
            SetStatus(tableNames.Count + viewNames.Count == 0 ? "База данных пуста" : string.Empty);
            RebuildNavigation(tableNames, viewNames, showObject: true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!CanDiscardEdits())
                e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                createFile?.Image?.Dispose();
                openFile?.Image?.Dispose();
                toolTip.Dispose();
                rows.Dispose();
            }
            base.Dispose(disposing);
        }

        private void CreateDatabase()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = DatabaseFilter,
                DefaultExt = "db",
                AddExtension = true,
                OverwritePrompt = false
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && CanDiscardEdits())
                    Run(() => SetDatabase(SqliteDatabase.CreateNew(dialog.FileName)));
            }
        }

        private void OpenDatabase()
        {
            using (var dialog = new OpenFileDialog { Filter = DatabaseFilter, CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && CanDiscardEdits())
                    Run(() => SetDatabase(new SqliteDatabase(dialog.FileName)));
            }
        }

        private void RebuildNavigation(IReadOnlyList<string> tableNames, IReadOnlyList<string> viewNames, bool showObject)
        {
            var previous = loadedNode;
            var previousRoot = previous?.Parent;
            SelectNode(null);
            navigation.BeginUpdate();
            try
            {
                AddNodes(tables, tableNames);
                AddNodes(views, viewNames);
            }
            finally
            {
                navigation.EndUpdate();
            }

            if (!showObject)
                return;

            var match = previousRoot?.Nodes.Cast<TreeNode>().FirstOrDefault(node => node.Text == previous.Text);
            var next = match ?? tables.FirstNode ?? views.FirstNode;
            if (next == null)
            {
                ShowData(null);
                return;
            }

            try
            {
                ShowObject(next, keepFilters: match != null);
            }
            catch
            {
                ShowData(null);
                throw;
            }
        }

        private static void AddNodes(TreeNode root, IReadOnlyList<string> names)
        {
            root.Nodes.Clear();
            foreach (var name in names)
                root.Nodes.Add(new TreeNode(name) { ToolTipText = name });
            root.Expand();
        }

        private void SelectNode(TreeNode node)
        {
            changingSelection = true;
            try
            {
                navigation.SelectedNode = node;
                loadedNode = node;
            }
            finally
            {
                changingSelection = false;
            }
        }

        private void ChangeSelection(TreeNode node)
        {
            if (changingSelection || node?.Parent == null || node == loadedNode)
                return;

            if (CanDiscardEdits() && Run(() => ShowObject(node)))
                SetStatus(string.Empty);
            else
                SelectNode(loadedNode);
        }

        private void ShowObject(TreeNode node, bool keepFilters = false)
        {
            var snapshot = node.Parent == tables ? database.LoadTable(node.Text) : null;
            ShowData(snapshot?.Data ?? database.LoadView(node.Text), snapshot, keepFilters);
            SelectNode(node);
        }

        private void ShowData(DataTable data, SqliteTable snapshot = null, bool keepFilters = false)
        {
            var previousValues = keepFilters ? filters.Values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, string>();
            filters.RemoveInputs();
            grid.DataSource = null;
            rows.DataSource = null;
            grid.Columns.Clear();
            table = snapshot;
            grid.ReadOnly = table == null;

            if (data != null)
            {
                foreach (DataColumn column in data.Columns)
                {
                    if (column.ColumnMapping == MappingType.Hidden)
                        continue;

                    grid.Columns.Add(new DataGridViewTextBoxColumn
                    {
                        HeaderText = column.Caption,
                        DataPropertyName = column.ColumnName,
                        MinimumWidth = 108,
                        ReadOnly = table == null || column.ReadOnly ||
                            string.Equals(column.Caption, "Id", StringComparison.OrdinalIgnoreCase)
                    });
                }

                rows.DataSource = data.DefaultView;
                grid.DataSource = rows;
            }

            filters.Rebuild(previousValues);
            theme.ApplyControls(filters, execute);
            ApplyFilters();
            UpdateControls();
        }

        private void ApplyFilters()
        {
            if (rows.DataSource is DataView view)
                TableFilter.Apply(view, filters.Values);
        }

        private void UpdateControls()
        {
            addRow.Enabled = deleteRow.Enabled = save.Enabled = cancel.Enabled = table != null;
            sql.Enabled = database != null;
            execute.Enabled = database != null && !string.IsNullOrWhiteSpace(sql.Text);
        }

        private void ExecuteSql()
        {
            if (!execute.Enabled || !Run(() =>
            {
                if (!FinishEdit() || HasTableChanges())
                    throw new InvalidOperationException("Сохраните или отмените правки перед выполнением SQL.");
            }))
                return;

            DataTable result = null;
            var executed = Run(() => result = database.ExecuteSql(sql.Text));
            var executionError = executed ? null : status.Text;
            if (result != null)
                ShowData(result);

            // Earlier statements of a batch may have changed the schema even if a later one failed.
            var refreshed = Run(() => RebuildNavigation(database.GetTables(), database.GetViews(),
                showObject: result == null && (executed || loadedNode != null)));
            if (!executed)
                SetStatus(executionError, isError: true);
            else if (refreshed)
                SetStatus(result == null ? "Запрос выполнен" : $"Строк в результате: {result.Rows.Count}");
        }

        private void AddRow()
        {
            if (!FinishEdit())
                return;

            filters.Clear();
            var row = rows.AddNew();
            var editableColumn = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(column => !column.ReadOnly);
            if (editableColumn != null)
            {
                grid.CurrentCell = grid.Rows[rows.IndexOf(row)].Cells[editableColumn.Index];
                grid.Focus();
                grid.BeginEdit(true);
            }
        }

        private void DeleteRows()
        {
            if (!FinishEdit())
                return;

            var selected = grid.SelectedRows.Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem).OfType<DataRowView>().ToArray();
            foreach (var row in selected)
                row.Delete();
        }

        private void ParseCell(object sender, DataGridViewCellParsingEventArgs args)
        {
            if (table == null || !(args.Value is string text))
                return;

            var column = table.Data.Columns[grid.Columns[args.ColumnIndex].DataPropertyName];
            args.Value = text.Length == 0 ? (object)DBNull.Value : text;
            if (table.ColumnTypes.TryGetValue(column, out var type))
            {
                var culture = CultureInfo.CurrentCulture;
                if (type == SqliteColumnType.Integer && long.TryParse(text, NumberStyles.Integer, culture, out var integer))
                    args.Value = integer;
                if (type == SqliteColumnType.Real && double.TryParse(text, NumberStyles.Float, culture, out var real) &&
                    !double.IsInfinity(real) && !double.IsNaN(real))
                    args.Value = real;
            }
            args.ParsingApplied = true;
        }

        private void Save()
        {
            if (!FinishEdit())
                return;

            var saved = database.SaveTable(table);
            ShowData(saved.Data, saved, keepFilters: true);
            SetStatus("Изменения сохранены");
        }

        private void Cancel()
        {
            grid.CancelEdit();
            rows.CancelEdit();
            ShowObject(loadedNode, keepFilters: true);
            SetStatus("Данные перечитаны из файла");
        }

        private bool FinishEdit()
        {
            if (table == null)
                return true;
            if (!grid.EndEdit())
                return false;
            rows.EndEdit();
            return true;
        }

        private bool HasTableChanges() =>
            table != null && table.Data.Rows.Cast<DataRow>().Any(row => row.RowState != DataRowState.Unchanged);

        private bool CanDiscardEdits()
        {
            var pending = false;
            if (!Run(() => pending = !FinishEdit() || HasTableChanges()))
                return false;

            return !pending || MessageBox.Show(this, "Отменить несохранённые изменения?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private bool Run(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception error)
            {
                SetStatus(error.Message, isError: true);
                return false;
            }
        }

        private void SetStatus(string message, bool isError = false)
        {
            statusIsError = isError;
            status.Text = message?.Replace("\r\n", " · ").Replace('\r', ' ').Replace('\n', ' ');
            status.ForeColor = isError ? theme.Error : theme.Text;
            toolTip.SetToolTip(status, message);
        }

        private void ApplyTheme()
        {
            theme = dark.Checked ? EditorTheme.Dark : EditorTheme.Light;
            theme.ApplyControls(this, execute);
            theme.ApplyGrid(grid);
            status.ForeColor = statusIsError ? theme.Error : theme.Text;
            SetFileIcon(createFile, newFile: true);
            SetFileIcon(openFile, newFile: false);
        }

        private Button MakeFileButton(string title, Action action)
        {
            var button = new Button
            {
                AccessibleName = title,
                Size = new Size(38, 32),
                Margin = new Padding(0, 0, 5, 0)
            };
            button.Click += (sender, args) => action();
            toolTip.SetToolTip(button, title);
            return button;
        }

        private void SetFileIcon(Button button, bool newFile)
        {
            button.Image?.Dispose();
            var icon = new Bitmap(20, 20);
            using (var graphics = Graphics.FromImage(icon))
            using (var pen = new Pen(theme.Text, 1.5f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                if (newFile)
                {
                    graphics.DrawPolygon(pen, new[] { new Point(4, 2), new Point(11, 2), new Point(16, 7),
                        new Point(16, 18), new Point(4, 18) });
                    graphics.DrawLines(pen, new[] { new Point(11, 2), new Point(11, 7), new Point(16, 7) });
                    graphics.DrawLine(pen, 7, 12, 13, 12);
                    graphics.DrawLine(pen, 10, 9, 10, 15);
                }
                else
                {
                    graphics.DrawPolygon(pen, new[] { new Point(2, 16), new Point(2, 4), new Point(8, 4),
                        new Point(10, 6), new Point(17, 6), new Point(17, 8), new Point(19, 8),
                        new Point(16, 16) });
                    graphics.DrawLine(pen, 2, 16, 6, 8);
                    graphics.DrawLine(pen, 6, 8, 17, 8);
                }
            }
            button.Image = icon;
        }

        private static Button MakeButton(string title, Action action)
        {
            var button = new Button { Text = title, AutoSize = true, Padding = new Padding(7, 3, 7, 3) };
            button.Click += (sender, args) => action();
            return button;
        }

        private static FlowLayoutPanel MakeBar(params Control[] controls)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            bar.Controls.AddRange(controls);
            return bar;
        }
    }
}
