using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using MySqlConnector;

namespace CloneDBManager
{
    public sealed class TableCloneOption
    {
        public string Name { get; }
        public bool CopyData { get; }

        public TableCloneOption(string name, bool copyData)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            CopyData = copyData;
        }
    }

    public enum DataCopyMethod
    {
        BulkCopy,
        BulkInsert
    }

    public static class CloneService
    {
        public static async Task<IReadOnlyList<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            await using var connection = new MySqlConnection(EnsureLocalInfileEnabled(connectionString));
            await connection.OpenAsync(cancellationToken);

            var tables = new List<string>();
            const string sql = "SELECT TABLE_NAME FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY TABLE_NAME";
            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(GetStringValue(reader, 0));
            }

            return tables;
        }

        public static async Task CloneDatabaseAsync(
            string sourceConnectionString,
            string destinationConnectionString,
            IReadOnlyCollection<TableCloneOption> tables,
            bool copyTriggers,
            bool copyRoutines,
            bool copyViews,
            Action<string>? log,
            DataCopyMethod copyMethod = DataCopyMethod.BulkCopy,
            bool createDestinationDatabaseIfMissing = false,
            CancellationToken cancellationToken = default)
        {
            var sourceBuilder = new MySqlConnectionStringBuilder(EnsureLocalInfileEnabled(sourceConnectionString));
            var destinationBuilder = new MySqlConnectionStringBuilder(EnsureLocalInfileEnabled(destinationConnectionString));

            await using var source = new MySqlConnection(sourceBuilder.ToString());
            await source.OpenAsync(cancellationToken);

            if (createDestinationDatabaseIfMissing)
            {
                var sourceDatabase = string.IsNullOrWhiteSpace(sourceBuilder.Database)
                    ? await GetCurrentDatabaseAsync(source, cancellationToken)
                    : sourceBuilder.Database;

                await EnsureDestinationDatabaseExistsAsync(source, sourceDatabase, destinationBuilder, cancellationToken);
            }

            await using var destination = new MySqlConnection(destinationBuilder.ToString());
            await destination.OpenAsync(cancellationToken);

            var originalForeignKeyState = await GetForeignKeyChecksAsync(destination, cancellationToken);
            await SetForeignKeyChecksAsync(destination, 0, cancellationToken);

            try
            {
                foreach (var table in tables)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!await IsBaseTableAsync(source, table.Name, cancellationToken))
                    {
                        log?.Invoke($"Skipping '{table.Name}' because it is a view; views are cloned separately.");
                        continue;
                    }

                    log?.Invoke($"Cloning structure for table '{table.Name}'...");
                    await CloneTableAsync(source, destination, table.Name, cancellationToken);

                    if (table.CopyData)
                    {
                        log?.Invoke($"Copying data for '{table.Name}'...");
                        await CopyDataAsync(source, destination, table.Name, copyMethod, log, cancellationToken);
                    }
                }

                if (copyViews)
                {
                    log?.Invoke("Cloning views...");
                    await CloneViewsAsync(source, destination, cancellationToken);
                }

                if (copyTriggers)
                {
                    log?.Invoke("Cloning triggers...");
                    await CloneTriggersAsync(source, destination, cancellationToken);
                }

                if (copyRoutines)
                {
                    log?.Invoke("Cloning stored routines (functions/procedures)...");
                    await CloneRoutinesAsync(source, destination, cancellationToken);
                }

                log?.Invoke("Cloning completed successfully.");
            }
            finally
            {
                await SetForeignKeyChecksAsync(destination, originalForeignKeyState, cancellationToken);
            }
        }

        private static async Task CloneTableAsync(MySqlConnection source, MySqlConnection destination, string tableName, CancellationToken cancellationToken)
        {
            await using var createCmd = new MySqlCommand($"SHOW CREATE TABLE `{tableName}`;", source);
            await using var reader = await createCmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            var createStatement = GetStringValue(reader, 1);
            await reader.CloseAsync();

            await using var dropTableCmd = new MySqlCommand($"DROP TABLE IF EXISTS `{tableName}`;", destination);
            await dropTableCmd.ExecuteNonQueryAsync(cancellationToken);

            await using var createDestCmd = new MySqlCommand(createStatement, destination);
            await createDestCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task EnsureDestinationDatabaseExistsAsync(
            MySqlConnection source,
            string sourceDatabase,
            MySqlConnectionStringBuilder destinationBuilder,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(destinationBuilder.Database) || string.IsNullOrWhiteSpace(sourceDatabase))
            {
                return;
            }

            var (characterSet, collation) = await GetDatabaseCharsetAndCollationAsync(source, sourceDatabase, cancellationToken);

            var adminBuilder = new MySqlConnectionStringBuilder(destinationBuilder.ToString())
            {
                Database = string.Empty
            };

            await using var adminConnection = new MySqlConnection(adminBuilder.ToString());
            await adminConnection.OpenAsync(cancellationToken);

            var createSql = $"CREATE DATABASE IF NOT EXISTS {WrapName(destinationBuilder.Database)}";
            if (!string.IsNullOrEmpty(characterSet))
            {
                createSql += $" CHARACTER SET {characterSet}";
            }

            if (!string.IsNullOrEmpty(collation))
            {
                createSql += $" COLLATE {collation}";
            }

            createSql += ";";

            await using var createCmd = new MySqlCommand(createSql, adminConnection);
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task CopyDataAsync(
            MySqlConnection source,
            MySqlConnection destination,
            string tableName,
            DataCopyMethod method,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            switch (method)
            {
                case DataCopyMethod.BulkInsert:
                    await CopyWithBulkInsertAsync(source, destination, tableName, cancellationToken);
                    break;
                case DataCopyMethod.BulkCopy:
                default:
                    var bulkCopied = await TryCopyWithBulkCopyAsync(source, destination, tableName, log, cancellationToken);
                    if (!bulkCopied)
                    {
                        await CopyWithBulkInsertAsync(source, destination, tableName, cancellationToken);
                    }
                    break;
            }
        }

        private static async Task<bool> TryCopyWithBulkCopyAsync(
            MySqlConnection source,
            MySqlConnection destination,
            string tableName,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            await using var selectCmd = new MySqlCommand($"SELECT * FROM `{tableName}`;", source);
            await using var reader = await selectCmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            if (!reader.HasRows)
            {
                return true;
            }

            try
            {
                await CopyDataWithBulkCopyAsync(reader, destination, tableName, log, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is MySqlException || ex is InvalidOperationException)
            {
                log?.Invoke($"Bulk copy failed for '{tableName}', falling back to bulk insert: {ex.Message}");
                return false;
            }
        }

        private static async Task CopyWithBulkInsertAsync(
            MySqlConnection source,
            MySqlConnection destination,
            string tableName,
            CancellationToken cancellationToken)
        {
            await using var selectCmd = new MySqlCommand($"SELECT * FROM `{tableName}`;", source);
            await using var reader = await selectCmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            if (!reader.HasRows)
            {
                return;
            }

            await CopyDataWithBulkInsertAsync(reader, destination, tableName, cancellationToken);
        }

        private static async Task CopyDataWithBulkCopyAsync(
            DbDataReader reader,
            MySqlConnection destination,
            string tableName,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            await PrepareConnectionForBulkCopyAsync(destination, log, cancellationToken);

            var bulkCopy = new MySqlBulkCopy(destination)
            {
                DestinationTableName = WrapName(tableName)
            };

            await bulkCopy.WriteToServerAsync(reader, cancellationToken);
        }

        private static async Task CopyDataWithBulkInsertAsync(DbDataReader reader, MySqlConnection destination, string tableName, CancellationToken cancellationToken)
        {
            const int batchSize = 500;

            var schema = reader.GetColumnSchema();
            if (schema.Count == 0)
            {
                return;
            }

            var columnNames = schema.Select(col => WrapName(col.ColumnName)).ToArray();
            var insertPrefix = $"INSERT INTO {WrapName(tableName)} ({string.Join(", ", columnNames)}) VALUES ";

            var valueRows = new List<string>(batchSize);
            var parameters = new List<MySqlParameter>(batchSize * columnNames.Length);

            try
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var placeholders = new string[columnNames.Length];
                    for (var i = 0; i < columnNames.Length; i++)
                    {
                        var paramName = $"@p{parameters.Count}";
                        placeholders[i] = paramName;

                        var value = await reader.IsDBNullAsync(i, cancellationToken)
                            ? DBNull.Value
                            : reader.GetValue(i);

                        parameters.Add(new MySqlParameter(paramName, value));
                    }

                    valueRows.Add($"({string.Join(", ", placeholders)})");

                    if (valueRows.Count >= batchSize)
                    {
                        await FlushBatchAsync(destination, insertPrefix, valueRows, parameters, cancellationToken);
                    }
                }
            }
            finally
            {
                await FlushBatchAsync(destination, insertPrefix, valueRows, parameters, cancellationToken);
            }
        }

        private static async Task FlushBatchAsync(
            MySqlConnection destination,
            string insertPrefix,
            List<string> valueRows,
            List<MySqlParameter> parameters,
            CancellationToken cancellationToken)
        {
            if (valueRows.Count == 0)
            {
                return;
            }

            var sql = insertPrefix + string.Join(", ", valueRows) + ";";

            using (var insertCmd = new MySqlCommand(sql, destination))
            {
                insertCmd.Parameters.AddRange(parameters.ToArray());
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            valueRows.Clear();
            parameters.Clear();
        }

        private static async Task PrepareConnectionForBulkCopyAsync(MySqlConnection destination, Action<string>? log, CancellationToken cancellationToken)
        {
            var connectionCharacterSet = await GetConnectionCharacterSetAsync(destination, cancellationToken);
            if (string.IsNullOrWhiteSpace(connectionCharacterSet))
            {
                return;
            }

            var supportedCharset = await GetSupportedCharacterSetAsync(destination, connectionCharacterSet, cancellationToken);
            if (string.IsNullOrWhiteSpace(supportedCharset))
            {
                log?.Invoke($"Destination server does not recognize character set '{connectionCharacterSet}'. Skipping SET NAMES before bulk copy.");
                return;
            }

            if (!string.Equals(supportedCharset, connectionCharacterSet, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Character set '{connectionCharacterSet}' not supported by destination; using '{supportedCharset}' for bulk copy.");
            }

            var setNamesSql = $"SET NAMES {supportedCharset};";
            await using var setNamesCmd = new MySqlCommand(setNamesSql, destination);
            await setNamesCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task CloneViewsAsync(MySqlConnection source, MySqlConnection destination, CancellationToken cancellationToken)
        {
            const string listViewsSql = "SHOW FULL TABLES WHERE Table_type = 'VIEW';";
            await using var listCmd = new MySqlCommand(listViewsSql, source);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            var views = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                views.Add(GetStringValue(reader, 0));
            }
            await reader.CloseAsync();

            var definitions = new List<(string Name, string CreateSql)>();
            foreach (var viewName in views)
            {
                await using var createCmd = new MySqlCommand($"SHOW CREATE VIEW `{viewName}`;", source);
                await using var createReader = await createCmd.ExecuteReaderAsync(cancellationToken);
                if (!await createReader.ReadAsync(cancellationToken))
                {
                    continue;
                }

                var createStatement = GetStringValue(createReader, 1);
                await createReader.CloseAsync();

                definitions.Add((viewName, createStatement));
            }

            foreach (var (name, _) in definitions)
            {
                await using var dropViewCmd = new MySqlCommand($"DROP VIEW IF EXISTS `{name}`;", destination);
                await dropViewCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var pending = new List<(string Name, string CreateSql)>(definitions);
            while (pending.Count > 0)
            {
                var createdThisPass = false;
                Exception? lastError = null;

                foreach (var view in pending.ToList())
                {
                    try
                    {
                        await using var createDestCmd = new MySqlCommand(view.CreateSql, destination);
                        await createDestCmd.ExecuteNonQueryAsync(cancellationToken);
                        pending.Remove(view);
                        createdThisPass = true;
                    }
                    catch (MySqlException ex) when (ex.Number == 1146 || ex.Number == 1356)
                    {
                        lastError = ex;
                    }
                }

                if (!createdThisPass)
                {
                    throw lastError ?? new InvalidOperationException("Unable to create views due to unresolved dependencies.");
                }
            }
        }

        private static async Task CloneTriggersAsync(MySqlConnection source, MySqlConnection destination, CancellationToken cancellationToken)
        {
            const string listSql = "SELECT TRIGGER_NAME FROM information_schema.triggers WHERE TRIGGER_SCHEMA = DATABASE();";
            await using var listCmd = new MySqlCommand(listSql, source);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            var triggers = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                triggers.Add(GetStringValue(reader, 0));
            }
            await reader.CloseAsync();

            var sourceSchema = await GetCurrentDatabaseAsync(source, cancellationToken);
            var destinationSchema = await GetCurrentDatabaseAsync(destination, cancellationToken);

            const string triggerDetailsSql = @"SELECT ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, ACTION_STATEMENT
FROM information_schema.triggers
WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME = @triggerName
LIMIT 1;";

            foreach (var trigger in triggers)
            {
                var triggerDetailsQuery = $@"SELECT ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, ACTION_STATEMENT
FROM information_schema.triggers
WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME = '{MySqlHelper.EscapeString(trigger)}'
LIMIT 1;";

                await using var createCmd = new MySqlCommand(triggerDetailsQuery, source);
                await using var createReader = await createCmd.ExecuteReaderAsync(cancellationToken);
                if (!await createReader.ReadAsync(cancellationToken))
                {
                    continue;
                }

                var actionTiming = GetStringValue(createReader, 0);
                var eventManipulation = GetStringValue(createReader, 1);
                var eventTable = GetStringValue(createReader, 2);
                var body = GetStringValue(createReader, 3).Trim().TrimEnd(';');
                await createReader.CloseAsync();

                if (!string.IsNullOrEmpty(sourceSchema) && !string.IsNullOrEmpty(destinationSchema) && !sourceSchema.Equals(destinationSchema, StringComparison.OrdinalIgnoreCase))
                {
                    eventTable = eventTable.Replace(sourceSchema, destinationSchema, StringComparison.OrdinalIgnoreCase);
                }

                var createStatement = $"CREATE TRIGGER {WrapName(trigger)} {actionTiming} {eventManipulation} ON {WrapName(eventTable)} FOR EACH ROW {body};";
                createStatement = NormalizeTriggerCreateStatement(createStatement);

                try
                {
                    await ExecuteTextNonQueryAsync(destination, $"DROP TRIGGER `{trigger}`;", cancellationToken);
                }
                catch (MySqlException ex) when (ex.Number == 1360)
                {
                    // Trigger is missing on the destination; safe to ignore before recreation.
                }

                await ExecuteTextNonQueryAsync(destination, createStatement, cancellationToken);
            }
        }

        private static async Task<string> GetCurrentDatabaseAsync(MySqlConnection connection, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(connection.Database))
            {
                return connection.Database;
            }

            await using var cmd = new MySqlCommand("SELECT DATABASE();", connection);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToString(result) ?? string.Empty;
        }

        private static async Task<(string? CharacterSet, string? Collation)> GetDatabaseCharsetAndCollationAsync(
            MySqlConnection connection,
            string databaseName,
            CancellationToken cancellationToken)
        {
            const string sql = @"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME
FROM information_schema.schemata
WHERE SCHEMA_NAME = @schemaName
LIMIT 1;";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@schemaName", databaseName);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var characterSet = reader.IsDBNull(0) ? null : GetStringValue(reader, 0);
                var collation = reader.IsDBNull(1) ? null : GetStringValue(reader, 1);
                return (characterSet, collation);
            }

            return (null, null);
        }

        private static async Task CloneRoutinesAsync(MySqlConnection source, MySqlConnection destination, CancellationToken cancellationToken)
        {
            const string listSql = "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.routines WHERE ROUTINE_SCHEMA = DATABASE();";
            await using var listCmd = new MySqlCommand(listSql, source);
            await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
            var routines = new List<(string Name, string Type)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                routines.Add((GetStringValue(reader, 0), GetStringValue(reader, 1)));
            }
            await reader.CloseAsync();

            foreach (var routine in routines)
            {
                var showCommand = routine.Type.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase)
                    ? $"SHOW CREATE FUNCTION `{routine.Name}`;"
                    : $"SHOW CREATE PROCEDURE `{routine.Name}`;";

                await using var createCmd = new MySqlCommand(showCommand, source);
                await using var createReader = await createCmd.ExecuteReaderAsync(cancellationToken);
                if (!await createReader.ReadAsync(cancellationToken))
                {
                    continue;
                }

                var createStatement = GetStringValue(createReader, 2);
                await createReader.CloseAsync();

                var dropSql = routine.Type.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase)
                    ? $"DROP FUNCTION IF EXISTS `{routine.Name}`;"
                    : $"DROP PROCEDURE IF EXISTS `{routine.Name}`;";

                await using var dropRoutineCmd = new MySqlCommand(dropSql, destination);
                await dropRoutineCmd.ExecuteNonQueryAsync(cancellationToken);

                await using var createDestCmd = new MySqlCommand(createStatement, destination);
                await createDestCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task<uint> GetForeignKeyChecksAsync(MySqlConnection connection, CancellationToken cancellationToken)
        {
            await using var cmd = new MySqlCommand("SELECT @@FOREIGN_KEY_CHECKS;", connection);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return Convert.ToUInt32(result);
        }

        private static string NormalizeTriggerCreateStatement(string createStatement)
        {
            var cleaned = StripVersionedComments(createStatement);
            cleaned = StripDefinerClause(cleaned);
            cleaned = cleaned.Trim();

            // Some CREATE TRIGGER statements from older servers can lose the leading CREATE
            // keyword after definer/version comment stripping, causing syntax errors that begin
            // at "TRIGGER ...". Ensure the statement is explicitly prefixed with CREATE so the
            // destination server accepts the definition.
            if (!cleaned.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = $"CREATE {cleaned}";
            }

            return cleaned;
        }

        private static string StripVersionedComments(string sql)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < sql.Length; i++)
            {
                if (i + 2 < sql.Length && sql[i] == '/' && sql[i + 1] == '*' && sql[i + 2] == '!')
                {
                    var endComment = sql.IndexOf("*/", i + 3, StringComparison.Ordinal);
                    if (endComment < 0)
                    {
                        break;
                    }

                    i = endComment + 1;
                    continue;
                }

                builder.Append(sql[i]);
            }

            return builder.ToString();
        }

        private static string StripDefinerClause(string sql)
        {
            const string definerKeyword = " DEFINER=";
            var definerIndex = sql.IndexOf(definerKeyword, StringComparison.OrdinalIgnoreCase);
            if (definerIndex < 0)
            {
                return sql;
            }

            var triggerIndex = sql.IndexOf(" TRIGGER", definerIndex, StringComparison.OrdinalIgnoreCase);
            if (triggerIndex < 0)
            {
                return sql;
            }

            return sql.Remove(definerIndex, triggerIndex - definerIndex);
        }

        private static async Task<bool> IsBaseTableAsync(MySqlConnection connection, string tableName, CancellationToken cancellationToken)
        {
            const string sql = "SELECT TABLE_TYPE FROM information_schema.tables WHERE table_schema = DATABASE() AND TABLE_NAME = @tableName LIMIT 1;";
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@tableName", tableName);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return string.Equals(result as string, "BASE TABLE", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task SetForeignKeyChecksAsync(MySqlConnection connection, uint value, CancellationToken cancellationToken)
        {
            await using var cmd = new MySqlCommand($"SET FOREIGN_KEY_CHECKS={value};", connection);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static string GetStringValue(DbDataReader reader, int ordinal)
        {
            var value = reader.GetValue(ordinal);
            if (value is string stringValue)
            {
                return stringValue;
            }

            if (value is byte[] bytes)
            {
                return Encoding.UTF8.GetString(bytes);
            }

            return Convert.ToString(value) ?? string.Empty;
        }

        private static string WrapName(string name) => $"`{name}`";

        private static string EnsureLocalInfileEnabled(string connectionString)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                AllowLoadLocalInfile = true,
                AllowUserVariables = true,
                IgnorePrepare = true
            };

            return builder.ToString();
        }

        private static async Task<string?> GetConnectionCharacterSetAsync(MySqlConnection connection, CancellationToken cancellationToken)
        {
            await using var cmd = new MySqlCommand("SELECT @@character_set_connection;", connection);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result as string;
        }

        private static async Task<string?> GetSupportedCharacterSetAsync(MySqlConnection connection, string requestedCharacterSet, CancellationToken cancellationToken)
        {
            const string sql = "SELECT COUNT(*) FROM information_schema.CHARACTER_SETS WHERE CHARACTER_SET_NAME = @charset LIMIT 1;";

            await using var cmd = new MySqlCommand(sql, connection);

            async Task<bool> CharacterSetExistsAsync(string charset)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@charset", charset);
                var result = await cmd.ExecuteScalarAsync(cancellationToken);
                return Convert.ToInt32(result) > 0;
            }

            if (await CharacterSetExistsAsync(requestedCharacterSet))
            {
                return requestedCharacterSet;
            }

            if (requestedCharacterSet.Equals("utf8mb4", StringComparison.OrdinalIgnoreCase) && await CharacterSetExistsAsync("utf8"))
            {
                return "utf8";
            }

            return null;
        }

        private static async Task ExecuteTextNonQueryAsync(MySqlConnection connection, string sql, CancellationToken cancellationToken)
        {
            await using var cmd = new MySqlCommand(sql, connection)
            {
                CommandType = CommandType.Text
            };

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
