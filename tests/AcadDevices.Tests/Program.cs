using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AcadDevices.Core;
using AcadDevices.DbEditor;

namespace AcadDevices.Tests
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var temp = Path.Combine(Path.GetTempPath(), "ANCAD SQLite " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var database = SqliteDatabase.CreateNew(Path.Combine(temp, "Devices.db"));
                Application.EnableVisualStyles();
                Test("Table-free database initialization and existing files", () => CheckDatabase(database, temp));
                Test("SQL execution, named queries and result sets", () => CheckQueries(database));
                Test("Literal column filters and CRUD", () => CheckCrud(database));
                Test("Existing tables and generated columns", () => CheckExistingTables(database));
                Test("Transaction rollback and stale edits", () => CheckRollback(database));
                Test("WinForms browser, row editing, SQL, filtering and theme", () => CheckEditor(temp));
                if (args.Length == 1)
                    CaptureScreenshots(args[0]);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                Directory.Delete(temp, recursive: true);
            }
        }

        private static void CheckDatabase(SqliteDatabase database, string temp)
        {
            var reopened = new SqliteDatabase(database.FilePath);
            var header = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(database.FilePath), 0, 16);
            Check(header == "SQLite format 3\0" && reopened.GetTables().Count == 0 && reopened.GetViews().Count == 0 &&
                (long)reopened.ExecuteQuery("SELECT count(*) FROM sqlite_schema").Rows[0][0] == 0,
                "A new database is a valid SQLite file and has no schema objects.");
            Throws<IOException>(() => SqliteDatabase.CreateNew(database.FilePath));
            var missing = Path.Combine(temp, "Missing.db");
            Throws<SQLiteException>(() => new SqliteDatabase(missing).GetTables());
            Check(!File.Exists(missing), "Opening a missing database must not create it.");
            var empty = Path.Combine(temp, "Empty.db");
            SQLiteConnection.CreateFile(empty);
            Check(new SqliteDatabase(empty).GetTables().Count == 0, "An empty SQLite database can be opened.");
        }

        private static void CheckQueries(SqliteDatabase database)
        {
            Check(database.ExecuteSql("CREATE TABLE QueryData (Id INTEGER PRIMARY KEY, Name TEXT)") == null,
                "DDL succeeds without returning rows.");
            Check(database.ExecuteSql("WITH names AS (SELECT 'Pump') INSERT INTO QueryData (Name) SELECT * FROM names") == null,
                "A CTE can also execute a write.");
            var result = database.ExecuteSql("-- a result query\nWITH data AS (SELECT * FROM QueryData) " +
                "SELECT Name AS repeated, Id AS repeated, NULL AS empty FROM data");
            Check(result.Columns.Count == 3 && result.Columns[0].Caption == "repeated" &&
                result.Columns[1].Caption == "repeated" && (string)result.Rows[0][0] == "Pump" &&
                (long)result.Rows[0][1] == 1 && result.Rows[0].IsNull(2),
                "A CTE with a comment returns values, nulls and duplicate column labels.");
            var empty = database.ExecuteSql("SELECT Name FROM QueryData WHERE 0");
            Check(empty.Columns[0].Caption == "Name" && empty.Rows.Count == 0, "A zero-row result keeps its columns.");

            var returned = database.ExecuteSql("UPDATE QueryData SET Name = 'Valve' RETURNING Name");
            Check((string)returned.Rows[0][0] == "Valve", "A write with RETURNING also yields rows.");
            Check((long)database.ExecuteSql("SELECT 1; INSERT INTO QueryData (Name) VALUES ('Meter'); SELECT 2; " +
                "UPDATE QueryData SET Name = 'Filter' WHERE Id = 2").Rows[0][0] == 2,
                "A batch runs every statement and displays its last result set.");
            const string viewName = "Named \"query\"";
            database.ExecuteSql("CREATE VIEW \"Named \"\"query\"\"\" AS SELECT Name FROM QueryData ORDER BY Id");
            Check(database.GetViews().SequenceEqual(new[] { viewName }) && database.GetTables().SequenceEqual(new[] { "QueryData" }) &&
                database.LoadView(viewName).Rows.Cast<DataRow>().Select(row => row[0]).SequenceEqual(new[] { "Valve", "Filter" }),
                "Named views are listed separately and safely opened, including names with quotes.");
            Throws<SQLiteException>(() => database.ExecuteSql("SELECT FROM QueryData"));
        }

        private static void CheckCrud(SqliteDatabase database)
        {
            const string name = "Bench \"devices\"";
            const string note = "Notes ]\\";
            database.ExecuteSql("CREATE TABLE \"Bench \"\"devices\"\"\" " +
                "(Id INTEGER PRIMARY KEY AUTOINCREMENT, \"Notes ]\\\" TEXT, Code TEXT, Quantity INTEGER, Price REAL)");
            Check(database.GetTables().Contains(name), "A table with a quoted name is listed.");

            var table = database.LoadTable(name);
            table.Data.Rows.Add(DBNull.Value, "Meter 100%[*] 'A'", "CAB-1", "2", "2.5");
            table.Data.Rows.Add(DBNull.Value, "Back-up", "METER", "4", "10");
            table.Data.Rows.Add(DBNull.Value, DBNull.Value, "CAB-3", "9", "2");
            table = database.SaveTable(table);
            Check(table.Data.Rows.Count == 3 && (long)table.Data.Rows[0]["Id"] == 1 &&
                (long)table.Data.Rows[0]["Quantity"] == 2 && (double)table.Data.Rows[0]["Price"] == 2.5,
                "SQLite generated an Id and stored numeric values.");

            var view = table.Data.DefaultView;
            TableFilter.Apply(view, new Dictionary<string, string> { [note] = "100%[*] 'A'" });
            Check(table.Data.DefaultView.Count == 1, "Pattern and identifier metacharacters are matched literally.");
            TableFilter.Apply(view, new Dictionary<string, string> { ["Code"] = "meter", ["Quantity"] = "4" });
            Check(view.Count == 1 && (long)view[0]["Id"] == 2, "Case-insensitive text and numeric filters combine with AND.");
            TableFilter.Apply(view, new Dictionary<string, string> { ["Code"] = "meter", ["Quantity"] = "9" });
            Check(view.Count == 0, "Filters apply to their own columns.");
            TableFilter.Apply(view, new Dictionary<string, string> { ["Code"] = string.Empty });
            Check(table.Data.DefaultView.Count == 3, "Clearing search restores all rows.");

            table.Data.Rows[0].Delete();
            table.Data.Rows[1][note] = DBNull.Value;
            table.Data.Rows[2]["Price"] = 6.25;
            table.Data.Rows.Add(DBNull.Value, "New", "CAB-4", 1, 7.25);
            database.SaveTable(table);

            var reopened = new SqliteDatabase(database.FilePath).LoadTable(name);
            var rows = reopened.Data.Rows.Cast<DataRow>().ToDictionary(row => (long)row["Id"]);
            Check(rows.Keys.OrderBy(id => id).SequenceEqual(new long[] { 2, 3, 4 }) &&
                rows[2].IsNull(note) && (double)rows[3]["Price"] == 6.25 && (string)rows[4][note] == "New",
                "One commit persisted the insert, updates and deletion.");
        }

        private static void CheckExistingTables(SqliteDatabase database)
        {
            database.ExecuteNonQuery("CREATE TABLE Legacy (name VARCHAR, value INTEGER DEFAULT 42, rowid TEXT, " +
                "summary TEXT GENERATED ALWAYS AS (name || value) STORED)");
            database.ExecuteNonQuery("INSERT INTO Legacy (name, rowid) VALUES ('same', 'shadow'), ('same', 'shadow')");
            var legacy = database.LoadTable("Legacy");
            Check(legacy.Data.Columns["summary"].ReadOnly, "Generated columns are read-only.");
            legacy.Data.Rows[0]["name"] = "edited";
            legacy.Data.Rows.Add("defaulted");
            legacy = database.SaveTable(legacy);
            Check(legacy.Data.Rows.Cast<DataRow>().Count(row => (string)row["name"] == "same") == 1 &&
                legacy.Data.Rows.Cast<DataRow>().Any(row => (string)row["summary"] == "defaulted42"),
                "Hidden rowid distinguishes duplicates and omitted fields get SQL defaults.");

            database.ExecuteNonQuery("CREATE TABLE Composite (a TEXT, b INTEGER, title TEXT, PRIMARY KEY(a, b)) WITHOUT ROWID");
            database.ExecuteNonQuery("INSERT INTO Composite VALUES ('AA', 1, 'one'), ('AA', 2, 'two')");
            var composite = database.LoadTable("Composite");
            composite.Data.Rows[0]["title"] = "updated";
            composite.Data.Rows[1].Delete();
            composite = database.SaveTable(composite);
            Check(composite.Data.Rows.Count == 1 && (string)composite.Data.Rows[0]["title"] == "updated",
                "Existing composite keys work without a SQLite rowid.");
        }

        private static void CheckRollback(SqliteDatabase database)
        {
            database.ExecuteNonQuery("CREATE TABLE Atomic (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL UNIQUE)");
            database.ExecuteNonQuery("INSERT INTO Atomic VALUES (1, 'keep'), (2, 'unchanged')");
            var table = database.LoadTable("Atomic");
            table.Data.Rows[0].Delete();
            table.Data.Rows.Add(DBNull.Value, "valid");
            table.Data.Rows.Add(DBNull.Value, "unchanged");
            Throws<SQLiteException>(() => database.SaveTable(table));
            var persisted = database.LoadTable("Atomic");
            Check(persisted.Data.Rows.Count == 2 && (string)persisted.Data.Rows[0]["Name"] == "keep" &&
                table.Data.Rows[0].RowState == DataRowState.Deleted, "The failed save rolled back and left edits pending.");

            persisted.Data.Rows[1]["Name"] = "stale";
            database.ExecuteNonQuery("UPDATE Atomic SET Name = 'changed elsewhere' WHERE Id = 2");
            Throws<DBConcurrencyException>(() => database.SaveTable(persisted));
            var actual = database.ExecuteQuery("SELECT Name FROM Atomic WHERE Id = @id", new SQLiteParameter("@id", 2L));
            Check((string)actual.Rows[0][0] == "changed elsewhere", "A stale edit does not overwrite persisted data.");
        }

        private static void CheckEditor(string directory)
        {
            var database = SqliteDatabase.CreateNew(Path.Combine(directory, "Editor.db"));
            using (var editor = new DbEditorForm())
            {
                editor.SetDatabase(database);
                editor.Show();
                Application.DoEvents();
                var tree = Controls<TreeView>(editor).Single();
                var grid = Controls<DataGridView>(editor).Single();
                var sql = Controls<TextBox>(editor).Single(text => text.AccessibleName == "SQL-запрос");
                var fileButtons = Controls<Button>(editor).Where(button => button.Image != null).ToArray();
                var status = Controls<Label>(editor).Single(label => label.AccessibleName == "Состояние");
                var execute = Button(editor, "Выполнить");
                var theme = Controls<CheckBox>(editor).Single();
                var darkBackground = editor.BackColor;
                Check(tree.Nodes.Count == 2 && tree.Nodes.Cast<TreeNode>().All(root => root.Nodes.Count == 0) &&
                    grid.Columns.Count == 0 && sql.Enabled && !execute.Enabled && fileButtons.Length == 2 &&
                    fileButtons.All(button => button.Text.Length == 0 && button.AccessibleName.Length > 0) &&
                    Controls<Label>(editor).Any(label => label.Text == database.FilePath),
                    "New files show the empty navigation tree and enable SQL. File buttons are accessible icons.");
                theme.Checked = false;
                Check(editor.BackColor.GetBrightness() > darkBackground.GetBrightness() &&
                    tree.BackColor == grid.BackgroundColor && sql.BackColor == grid.BackgroundColor, "The theme switches all surfaces.");
                theme.Checked = true;
                Check(editor.BackColor == darkBackground, "The dark theme is restored.");

                sql.Text = "CREATE TABLE UiTable (Id INTEGER PRIMARY KEY, Название TEXT, Количество INTEGER DEFAULT 4); " +
                    "CREATE VIEW \"В наличии\" AS SELECT Название, Количество FROM UiTable";
                execute.PerformClick();
                Check(tree.Nodes[0].Nodes[0].Text == "UiTable" && tree.Nodes[1].Nodes[0].Text == "В наличии" &&
                    grid.Columns.Count == 3 && grid.Columns[0].ReadOnly && !grid.ReadOnly, "SQL DDL refreshes the object tree and editable grid.");

                Button(editor, "Добавить строку").PerformClick();
                Check(grid.IsCurrentCellInEditMode, "The added row starts editing.");
                ((TextBox)grid.EditingControl).Text = "Клапан А-02";
                ((IDataGridViewEditingControl)grid.EditingControl).EditingControlValueChanged = true;
                grid.NotifyCurrentCellDirty(true);
                Button(editor, "Сохранить изменения").PerformClick();
                var persisted = database.LoadTable("UiTable").Data.Rows;
                Check(persisted.Count == 1 && (string)persisted[0]["Название"] == "Клапан А-02",
                    "Save commits the pending cell value and row.");
                Check(grid.Columns.Count == 3 && grid.Rows.Count == 1 && status.Text == "Изменения сохранены",
                    $"Save rebuilds the editable grid. Editor status was «{status.Text}».");

                var nameFilter = Controls<TextBox>(editor).Single(text => text.AccessibleName == "Фильтр · Название");
                var quantityFilter = Controls<TextBox>(editor).Single(text => text.AccessibleName == "Фильтр · Количество");
                var columnBounds = grid.GetColumnDisplayRectangle(1, cutOverflow: true);
                Check(nameFilter.PointToScreen(Point.Empty).X == grid.PointToScreen(new Point(columnBounds.X + 2, 0)).X &&
                    nameFilter.Bottom <= nameFilter.Parent.Height &&
                    nameFilter.PointToScreen(Point.Empty).Y < grid.PointToScreen(Point.Empty).Y,
                    "Filter inputs are aligned above their grid columns.");
                var binding = (BindingSource)grid.DataSource;
                nameFilter.Text = "а-02";
                quantityFilter.Text = "5";
                Check(binding.Count == 0, "Both filters constrain the table data.");
                quantityFilter.Text = "4";
                Check(binding.Count == 1, "A substring and a number can match the same row.");

                grid.Rows[0].Selected = true;
                Button(editor, "Удалить строку").PerformClick();
                sql.Text = "DELETE FROM UiTable";
                execute.PerformClick();
                Check(binding.Count == 0 && database.LoadTable("UiTable").Data.Rows.Count == 1 &&
                    status.Text.Contains("Сохраните"), "Executing SQL leaves pending grid edits intact.");
                Button(editor, "Отменить").PerformClick();
                Check(binding.Count == 1 && database.LoadTable("UiTable").Data.Rows.Count == 1,
                    "Cancel restores the deleted row and keeps its filters.");
                grid.Rows[0].Selected = true;
                Button(editor, "Удалить строку").PerformClick();
                Button(editor, "Сохранить изменения").PerformClick();
                Check(database.LoadTable("UiTable").Data.Rows.Count == 0, "Delete and save remove the selected record.");

                sql.Text = "INSERT INTO UiTable (Название) VALUES ('Снаружи')";
                execute.PerformClick();
                foreach (var filter in Controls<TextBox>(editor).Where(text => text != sql))
                    filter.Clear();
                Check(binding.Count == 1 && status.Text == "Запрос выполнен", "A write refreshes the current table.");

                tree.SelectedNode = tree.Nodes[1].Nodes[0];
                Check(grid.ReadOnly && grid.Columns.Count == 2 && binding.Count == 1 &&
                    !Button(editor, "Добавить строку").Enabled && !Button(editor, "Сохранить изменения").Enabled,
                    "Selecting a named query displays a filterable, read-only result.");
                sql.Text = "SELECT 12 AS Номер, 'Насос' AS Устройство UNION ALL SELECT 45, 'Клапан'";
                execute.PerformClick();
                Check(grid.ReadOnly && grid.Columns[0].HeaderText == "Номер" && binding.Count == 2,
                    "Ad hoc SQL replaces the grid with the result.");
                Controls<TextBox>(editor).Single(text => text.AccessibleName == "Фильтр · Номер").Text = "5";
                Check(binding.Count == 1 && (long)((DataRowView)binding[0])[0] == 45, "Query results can be filtered by column.");

                sql.Text = "CREATE VIEW \"Пустой\" AS SELECT * FROM UiTable WHERE 0; SELECT FROM UiTable";
                execute.PerformClick();
                Check(status.Text.IndexOf("syntax", StringComparison.OrdinalIgnoreCase) >= 0 && binding.Count == 1 &&
                    tree.Nodes[1].Nodes.Cast<TreeNode>().Any(node => node.Text == "Пустой"),
                    "SQL errors are visible, with earlier schema changes reflected in the tree.");
                tree.SelectedNode = tree.Nodes[0].Nodes[0];
                Check(!grid.ReadOnly && binding.Count == 1, "Returning to a table restores row editing.");

                var wideColumns = Enumerable.Range(1, 12).Select(index => $"{index} AS C{index}");
                sql.Text = "SELECT " + string.Join(", ", wideColumns);
                execute.PerformClick();
                grid.FirstDisplayedScrollingColumnIndex = grid.Columns.Count - 1;
                Application.DoEvents();
                var lastFilter = Controls<TextBox>(editor).Single(text => text.AccessibleName == "Фильтр · C12");
                var lastColumn = grid.GetColumnDisplayRectangle(11, cutOverflow: true);
                Check(lastColumn.Width > 12 && lastFilter.Visible && lastFilter.Left == lastColumn.X + 2,
                    $"Filters follow horizontal scrolling. Offset {grid.HorizontalScrollingOffset}, " +
                    $"column {lastColumn}, filter {lastFilter.Bounds}, visible {lastFilter.Visible}.");
            }
        }

        private static void CaptureScreenshots(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "Equipment.db");
            var database = SqliteDatabase.CreateNew(path);
            try
            {
                database.ExecuteSql("CREATE TABLE Оборудование (Id INTEGER PRIMARY KEY, Название TEXT, Модель TEXT, " +
                    "Производитель TEXT, Количество INTEGER); CREATE TABLE Поставщики (Id INTEGER PRIMARY KEY); " +
                    "INSERT INTO Оборудование (Название, Модель, Производитель, Количество) VALUES " +
                    "('Насос Н-101', 'P-101', 'Гидромаш', 3), ('Клапан К-204', 'DN 50', 'Вентиль', 0), " +
                    "('Датчик Т-306', 'PT100', 'Термосенс', 12); " +
                    "CREATE VIEW \"В наличии\" AS SELECT Название, Модель, Количество FROM Оборудование WHERE Количество > 0; " +
                    "CREATE VIEW \"К заказу\" AS SELECT Название, Модель FROM Оборудование WHERE Количество = 0");
                using (var editor = new DbEditorForm())
                {
                    var desktop = Screen.PrimaryScreen.WorkingArea;
                    editor.StartPosition = FormStartPosition.Manual;
                    editor.Location = new Point(desktop.Left + 8, desktop.Top + 8);
                    editor.Size = new Size(Math.Min(editor.Width, desktop.Width - 16),
                        Math.Min(editor.Height, desktop.Height - 16));
                    editor.SetDatabase(database);
                    var sql = Controls<TextBox>(editor).Single(text => text.AccessibleName == "SQL-запрос");
                    sql.Text = "SELECT Название, Модель, Количество\r\nFROM Оборудование\r\nWHERE Количество > 0;";
                    Capture(editor, Path.Combine(directory, "editor.png"));
                    Button(editor, "Выполнить").PerformClick();
                    Capture(editor, Path.Combine(directory, "query.png"));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static void Capture(Form form, string path)
        {
            form.Show();
            form.Refresh();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(form.PointToScreen(Point.Empty), Point.Empty, bitmap.Size);
                bitmap.Save(path);
            }
        }

        private static IEnumerable<T> Controls<T>(Control parent) where T : Control =>
            parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.OfType<T>().Concat(Controls<T>(child)));

        private static Button Button(Control parent, string text) => Controls<Button>(parent).Single(button => button.Text == text);

        private static void Test(string name, Action test)
        {
            test();
            Console.WriteLine("PASS " + name);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
