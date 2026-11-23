using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace AcadDevices.Core
{
    public sealed class SqliteDatabase
    {
        private const string UserTables =
            "FROM pragma_table_list WHERE schema = 'main' AND type IN ('table', 'virtual') " +
            "AND substr(name, 1, 7) <> 'sqlite_'";

        private readonly string connectionString;

        public SqliteDatabase(string path)
        {
            FilePath = Path.GetFullPath(path);
            connectionString = new SQLiteConnectionStringBuilder
            {
                DataSource = FilePath,
                FailIfMissing = true,
                ForeignKeys = true,
                DefaultTimeout = 5
            }.ConnectionString;
        }

        public string FilePath { get; }

        public static SqliteDatabase CreateNew(string path)
        {
            var database = new SqliteDatabase(path);
            // CreateNew makes the existence check atomic and preserves any existing file.
            using (File.Open(database.FilePath, FileMode.CreateNew, FileAccess.Write))
            {
            }

            try
            {
                // Write a SQLite header and an empty schema without creating a table.
                database.ExecuteNonQuery("PRAGMA main.user_version = 0");
                return database;
            }
            catch
            {
                File.Delete(database.FilePath);
                throw;
            }
        }

        public IReadOnlyList<string> GetTables() =>
            GetObjectNames("SELECT name " + UserTables + " ORDER BY name COLLATE NOCASE");

        public IReadOnlyList<string> GetViews() => GetObjectNames(
            "SELECT name FROM main.sqlite_schema WHERE type = 'view' ORDER BY name COLLATE NOCASE");

        private IReadOnlyList<string> GetObjectNames(string sql)
        {
            var tables = new List<string>();
            using (var connection = OpenConnection())
            using (var command = new SQLiteCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }

            return tables;
        }

        public DataTable LoadView(string name) => ExecuteSql($"SELECT * FROM main.{Quote(name)}");

        /// <summary>Executes SQL and returns its final row-producing result, or null for SQL without results.</summary>
        public DataTable ExecuteSql(string sql)
        {
            using (var connection = OpenConnection())
            using (var command = new SQLiteCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                DataTable result = null;
                do
                {
                    if (reader.FieldCount == 0)
                        continue;

                    result = new DataTable();
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        // SQL may return multiple columns with the same label.
                        var column = result.Columns.Add("Column" + index, typeof(object));
                        column.Caption = reader.GetName(index);
                    }

                    while (reader.Read())
                    {
                        var values = new object[reader.FieldCount];
                        reader.GetValues(values);
                        result.Rows.Add(values);
                    }
                }
                while (reader.NextResult());

                return result;
            }
        }

        public int ExecuteNonQuery(string sql, params SQLiteParameter[] parameters)
        {
            using (var connection = OpenConnection())
            using (var command = new SQLiteCommand(sql, connection))
            {
                command.Parameters.AddRange(parameters);
                return command.ExecuteNonQuery();
            }
        }

        public DataTable ExecuteQuery(string sql, params SQLiteParameter[] parameters)
        {
            using (var connection = OpenConnection())
            using (var command = new SQLiteCommand(sql, connection))
            using (var adapter = new SQLiteDataAdapter(command))
            {
                command.Parameters.AddRange(parameters);
                var result = new DataTable();
                adapter.Fill(result);
                return result;
            }
        }

        public SqliteTable LoadTable(string name)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                var result = ReadTable(name, transaction);
                transaction.Commit();
                return result;
            }
        }

        /// <summary>Commits all pending rows and returns a fresh snapshot, including generated values.</summary>
        public SqliteTable SaveTable(SqliteTable table)
        {
            if (table.DatabasePath != FilePath)
                throw new ArgumentException("Таблица загружена из другой базы данных.", nameof(table));

            var columns = table.Data.Columns.Cast<DataColumn>().Where(c => c != table.RowIdColumn).ToArray();
            // Delete first so an inserted row can reuse a value covered by a UNIQUE constraint.
            var changes = table.Data.Rows.Cast<DataRow>()
                .Where(row => row.RowState != DataRowState.Unchanged)
                .OrderBy(row => row.RowState == DataRowState.Deleted ? 0 : 1);

            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var row in changes)
                    SaveRow(table, row, columns, transaction);

                var result = ReadTable(table.Data.TableName, transaction);
                transaction.Commit();
                return result;
            }
        }

        private SqliteTable ReadTable(string name, SQLiteTransaction transaction)
        {
            var connection = transaction.Connection;
            bool hasRowId;
            using (var command = new SQLiteCommand("SELECT wr " + UserTables + " AND name = @name COLLATE NOCASE",
                connection, transaction))
            {
                command.Parameters.AddWithValue("@name", name);
                var withoutRowId = command.ExecuteScalar();
                if (withoutRowId == null)
                    throw new InvalidOperationException($"Таблица «{name}» не найдена.");
                hasRowId = Convert.ToInt32(withoutRowId) == 0;
            }

            var data = new DataTable(name);
            var columnTypes = new Dictionary<DataColumn, SqliteColumnType>();
            var generatedColumns = new List<DataColumn>();
            var databaseColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var command = new SQLiteCommand(
                "SELECT name, type, hidden FROM pragma_table_xinfo(@name, 'main') ORDER BY cid", connection, transaction))
            {
                command.Parameters.AddWithValue("@name", name);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var columnName = reader.GetString(0);
                        databaseColumnNames.Add(columnName);
                        var hidden = reader.GetInt32(2);
                        if (hidden == 1)
                            continue; // Hidden arguments of virtual tables are outside SELECT *.

                        // SQLite storage classes can vary by row, even within a declared TEXT column.
                        var column = data.Columns.Add(columnName, typeof(object));
                        column.Caption = columnName;
                        var type = ColumnType(reader.GetString(1));
                        if (type.HasValue)
                            columnTypes.Add(column, type.Value);
                        if (hidden == 2 || hidden == 3)
                            generatedColumns.Add(column);
                    }
                }
            }

            var rowIdAlias = hasRowId
                ? new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(alias => !databaseColumnNames.Contains(alias))
                : null;
            // Unary + strips the declared type without coercing the value, so the provider
            // reads the actual storage class (including BLOBs and numbers stored in text columns).
            var projections = data.Columns.Cast<DataColumn>().Select(c => "+" + Quote(c.Caption)).ToList();
            DataColumn rowIdColumn = null;
            if (rowIdAlias != null)
            {
                projections.Add(Quote(rowIdAlias));
                var nameForRowId = "__rowid";
                while (data.Columns.Contains(nameForRowId))
                    nameForRowId += "_";

                rowIdColumn = data.Columns.Add(nameForRowId, typeof(long));
                rowIdColumn.ColumnMapping = MappingType.Hidden;
            }

            using (var command = new SQLiteCommand(
                $"SELECT {string.Join(", ", projections)} FROM main.{Quote(name)}", connection, transaction))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    data.Rows.Add(values);
                }
            }

            foreach (var column in generatedColumns)
                column.ReadOnly = true;
            if (rowIdColumn != null)
                rowIdColumn.ReadOnly = true;

            data.AcceptChanges();
            return new SqliteTable(FilePath, data, new ReadOnlyDictionary<DataColumn, SqliteColumnType>(columnTypes),
                rowIdAlias, rowIdColumn);
        }

        private static void SaveRow(SqliteTable table, DataRow row, DataColumn[] columns, SQLiteTransaction transaction)
        {
            using (var command = new SQLiteCommand(transaction.Connection))
            {
                command.Transaction = transaction;
                var name = "main." + Quote(table.Data.TableName);
                if (row.RowState == DataRowState.Added)
                {
                    // Unspecified values get their SQLite defaults (including the generated Id).
                    var supplied = columns.Where(c => !c.ReadOnly && !row.IsNull(c)).ToArray();
                    command.CommandText = supplied.Length == 0
                        ? $"INSERT INTO {name} DEFAULT VALUES"
                        : $"INSERT INTO {name} ({string.Join(", ", supplied.Select(c => Quote(c.Caption)))}) " +
                          $"VALUES ({string.Join(", ", supplied.Select(c => Parameter(command, row[c])))})";
                    command.ExecuteNonQuery();
                    return;
                }

                if (row.RowState == DataRowState.Deleted)
                {
                    command.CommandText = $"DELETE FROM {name}";
                }
                else
                {
                    var assignments = columns
                        .Where(c => !c.ReadOnly && !Equals(row[c], row[c, DataRowVersion.Original]))
                        .Select(c => $"{Quote(c.Caption)} = {Parameter(command, row[c])}").ToArray();
                    if (assignments.Length == 0)
                        return;
                    command.CommandText = $"UPDATE {name} SET {string.Join(", ", assignments)}";
                }

                // Match the original row contents in addition to its rowid, when available.
                // This prevents stale edits from silently overwriting somebody else's changes.
                var predicates = columns.Select(c => $"+{Quote(c.Caption)} COLLATE BINARY IS " +
                    Parameter(command, row[c, DataRowVersion.Original])).ToList();
                if (table.RowIdColumn != null)
                    predicates.Insert(0, $"{Quote(table.RowIdAlias)} IS " +
                        Parameter(command, row[table.RowIdColumn, DataRowVersion.Original]));

                command.CommandText += " WHERE " + string.Join(" AND ", predicates);
                if (command.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Запись изменилась или не определяется однозначно. Нажмите «Отменить», чтобы перечитать таблицу.");
            }
        }

        private SQLiteConnection OpenConnection()
        {
            var connection = new SQLiteConnection(connectionString);
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private static string Parameter(SQLiteCommand command, object value)
        {
            var name = "@p" + command.Parameters.Count;
            command.Parameters.AddWithValue(name, value);
            return name;
        }

        private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        private static SqliteColumnType? ColumnType(string declaration)
        {
            var type = declaration.ToUpperInvariant();
            if (type.Contains("INT"))
                return SqliteColumnType.Integer;
            if (type.Contains("TEXT") || type.Contains("CHAR") || type.Contains("CLOB"))
                return SqliteColumnType.Text;
            if (type.Contains("BLOB"))
                return null;
            if (type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB"))
                return SqliteColumnType.Real;

            return null;
        }
    }
}
