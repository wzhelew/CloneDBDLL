using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using MySqlConnector;

public class DatabaseConnectionInfo
{
    public DatabaseConnectionInfo()
    {
        Host = string.Empty;
        Port = 3306;
        UserName = string.Empty;
        Password = string.Empty;
        Database = string.Empty;
    }

    public string Host { get; set; }
    public int Port { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    public string Database { get; set; }

    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = (uint)(Port > 0 ? Port : 3306),
            UserID = UserName,
            Password = Password,
            Database = Database,
            AllowUserVariables = true
        };

        return builder.ConnectionString;
    }
}

public class TableCloneOption
{
    public TableCloneOption(string name, bool copyData, bool isBaseTable)
    {
        Name = name;
        CopyData = copyData;
        IsBaseTable = isBaseTable;
    }

    public string Name { get; set; }
    public bool CopyData { get; set; }
    public bool IsBaseTable { get; set; }
}

public class CloneOptions
{
    public CloneOptions()
    {
        CopyTriggers = true;
        CopyRoutines = true;
        CopyViews = true;
    }

    public bool CopyTriggers { get; set; }
    public bool CopyRoutines { get; set; }
    public bool CopyViews { get; set; }
}

public static class CloneService
{
    public static void CloneDatabase(
        DatabaseConnectionInfo source,
        DatabaseConnectionInfo destination,
        CloneOptions options = null,
        Action<string> log = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        options = options ?? new CloneOptions();

        var sourceConnectionString = source.BuildConnectionString();
        var destinationConnectionString = destination.BuildConnectionString();

        using (var sourceConnection = new MySqlConnection(sourceConnectionString))
        using (var destinationConnection = new MySqlConnection(destinationConnectionString))
        {
            sourceConnection.Open();
            destinationConnection.Open();

            var tables = GetTables(sourceConnection);
            var originalForeignKeyState = GetForeignKeyChecks(destinationConnection);
            SetForeignKeyChecks(destinationConnection, 0);

            try
            {
                foreach (var table in tables)
                {
                    if (!table.IsBaseTable)
                    {
                        // Views are handled separately.
                        continue;
                    }

                    if (log != null)
                    {
                        log(string.Format("Cloning structure for table '{0}'...", table.Name));
                    }
                    CloneTable(sourceConnection, destinationConnection, table.Name);

                    if (table.CopyData)
                    {
                        if (log != null)
                        {
                            log(string.Format("Copying data for '{0}'...", table.Name));
                        }
                        CopyData(sourceConnection, destinationConnection, table.Name);
                    }
                }

                if (options != null && options.CopyViews)
                {
                    if (log != null)
                    {
                        log("Cloning views...");
                    }
                    CloneViews(sourceConnection, destinationConnection);
                }

                if (options != null && options.CopyTriggers)
                {
                    if (log != null)
                    {
                        log("Cloning triggers...");
                    }
                    CloneTriggers(sourceConnection, destinationConnection);
                }

                if (options != null && options.CopyRoutines)
                {
                    if (log != null)
                    {
                        log("Cloning stored routines (functions/procedures)...");
                    }
                    CloneRoutines(sourceConnection, destinationConnection);
                }

                if (log != null)
                {
                    log("Cloning completed successfully.");
                }
            }
            finally
            {
                SetForeignKeyChecks(destinationConnection, originalForeignKeyState);
            }
        }
    }

    private static List<TableCloneOption> GetTables(MySqlConnection connection)
    {
        var tables = new List<TableCloneOption>();
        const string sql = "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY TABLE_NAME";

        using (var command = new MySqlCommand(sql, connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var tableType = reader.GetString(1);
                var isBaseTable = string.Equals(tableType, "BASE TABLE", StringComparison.OrdinalIgnoreCase);
                tables.Add(new TableCloneOption(name, isBaseTable, isBaseTable));
            }
        }

        return tables;
    }

    private static void CloneTable(MySqlConnection source, MySqlConnection destination, string tableName)
    {
        using (var createCmd = new MySqlCommand(string.Format("SHOW CREATE TABLE `{0}`;", tableName), source))
        using (var reader = createCmd.ExecuteReader())
        {
            if (!reader.Read())
            {
                return;
            }

            var createStatement = reader.GetString(1);
            reader.Close();

            using (var dropCmd = new MySqlCommand(string.Format("DROP TABLE IF EXISTS `{0}`;", tableName), destination))
            {
                dropCmd.ExecuteNonQuery();
            }

            using (var createDestCmd = new MySqlCommand(createStatement, destination))
            {
                createDestCmd.ExecuteNonQuery();
            }
        }
    }

    private static void CopyData(MySqlConnection source, MySqlConnection destination, string tableName)
    {
        using (var selectCmd = new MySqlCommand(string.Format("SELECT * FROM `{0}`;", tableName), source))
        using (var reader = selectCmd.ExecuteReader())
        {
            if (!reader.HasRows)
            {
                return;
            }

            var columnNames = Enumerable.Range(0, reader.FieldCount)
                .Select(reader.GetName)
                .ToArray();

            var parameterNames = columnNames.Select((_, i) => string.Format("@p{0}", i)).ToArray();
            var insertSql = string.Format(
                "INSERT INTO `{0}` ({1}) VALUES ({2});",
                tableName,
                string.Join(", ", columnNames.Select(WrapName)),
                string.Join(", ", parameterNames));

            while (reader.Read())
            {
                using (var insertCmd = new MySqlCommand(insertSql, destination))
                {
                    for (var i = 0; i < columnNames.Length; i++)
                    {
                        insertCmd.Parameters.AddWithValue(parameterNames[i], reader.GetValue(i));
                    }

                    insertCmd.ExecuteNonQuery();
                }
            }
        }
    }

    private static void CloneViews(MySqlConnection source, MySqlConnection destination)
    {
        const string listViewsSql = "SHOW FULL TABLES WHERE Table_type = 'VIEW';";
        var definitions = new List<Tuple<string, string>>();

        using (var listCmd = new MySqlCommand(listViewsSql, source))
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                definitions.Add(new Tuple<string, string>(reader.GetString(0), string.Empty));
            }
        }

        for (var i = 0; i < definitions.Count; i++)
        {
            var viewName = definitions[i].Item1;
            using (var createCmd = new MySqlCommand(string.Format("SHOW CREATE VIEW `{0}`;", viewName), source))
            using (var createReader = createCmd.ExecuteReader())
            {
                if (createReader.Read())
                {
                    definitions[i] = new Tuple<string, string>(viewName, createReader.GetString(1));
                }
            }
        }

        foreach (var definition in definitions)
        {
            using (var dropCmd = new MySqlCommand(string.Format("DROP VIEW IF EXISTS `{0}`;", definition.Item1), destination))
            {
                dropCmd.ExecuteNonQuery();
            }
        }

        var pending = new List<Tuple<string, string>>(definitions);
        while (pending.Count > 0)
        {
            var createdThisPass = false;
            Exception lastError = null;

            foreach (var view in new List<Tuple<string, string>>(pending))
            {
                try
                {
                    var sanitized = StripDefiner(view.Item2);
                    using (var createDestCmd = new MySqlCommand(sanitized, destination))
                    {
                        createDestCmd.ExecuteNonQuery();
                    }

                    pending.Remove(view);
                    createdThisPass = true;
                }
                catch (MySqlException ex)
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

    private static void CloneTriggers(MySqlConnection source, MySqlConnection destination)
    {
        const string listSql = "SELECT TRIGGER_NAME FROM information_schema.triggers WHERE TRIGGER_SCHEMA = DATABASE();";
        var triggers = new List<string>();

        using (var listCmd = new MySqlCommand(listSql, source))
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                triggers.Add(reader.GetString(0));
            }
        }

        var sourceSchema = GetCurrentDatabase(source);
        var destinationSchema = GetCurrentDatabase(destination);

        foreach (var trigger in triggers)
        {
            using (var createCmd = new MySqlCommand(string.Format("SHOW CREATE TRIGGER `{0}`;", trigger), source))
            using (var createReader = createCmd.ExecuteReader())
            {
                if (!createReader.Read())
                {
                    continue;
                }

                var createStatement = createReader.GetString(2);

                if (!string.IsNullOrEmpty(sourceSchema) && !string.IsNullOrEmpty(destinationSchema) && !sourceSchema.Equals(destinationSchema, StringComparison.OrdinalIgnoreCase))
                {
                    createStatement = createStatement.Replace(string.Format("`{0}`.", sourceSchema), string.Format("`{0}`.", destinationSchema));
                }

                using (var dropCmd = new MySqlCommand(string.Format("DROP TRIGGER IF EXISTS `{0}`;", trigger), destination))
                {
                    dropCmd.ExecuteNonQuery();
                }

                var sanitized = StripDefiner(createStatement);
                using (var createDestCmd = new MySqlCommand(sanitized, destination))
                {
                    createDestCmd.ExecuteNonQuery();
                }
            }
        }
    }

    private static void CloneRoutines(MySqlConnection source, MySqlConnection destination)
    {
        const string listSql = "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.routines WHERE ROUTINE_SCHEMA = DATABASE();";
        var routines = new List<Tuple<string, string>>();

        using (var listCmd = new MySqlCommand(listSql, source))
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                routines.Add(new Tuple<string, string>(reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var routine in routines)
        {
            var showCommand = string.Equals(routine.Item2, "FUNCTION", StringComparison.OrdinalIgnoreCase)
                ? string.Format("SHOW CREATE FUNCTION `{0}`;", routine.Item1)
                : string.Format("SHOW CREATE PROCEDURE `{0}`;", routine.Item1);

            using (var createCmd = new MySqlCommand(showCommand, source))
            using (var createReader = createCmd.ExecuteReader())
            {
                if (!createReader.Read())
                {
                    continue;
                }

                var createStatement = createReader.GetString(2);
                var dropSql = string.Equals(routine.Item2, "FUNCTION", StringComparison.OrdinalIgnoreCase)
                    ? string.Format("DROP FUNCTION IF EXISTS `{0}`;", routine.Item1)
                    : string.Format("DROP PROCEDURE IF EXISTS `{0}`;", routine.Item1);

                using (var dropCmd = new MySqlCommand(dropSql, destination))
                {
                    dropCmd.ExecuteNonQuery();
                }

                var sanitized = StripDefiner(createStatement);
                using (var createDestCmd = new MySqlCommand(sanitized, destination))
                {
                    createDestCmd.ExecuteNonQuery();
                }
            }
        }
    }

    private static string GetCurrentDatabase(MySqlConnection connection)
    {
        if (!string.IsNullOrEmpty(connection.Database))
        {
            return connection.Database;
        }

        using (var cmd = new MySqlCommand("SELECT DATABASE();", connection))
        {
            var result = cmd.ExecuteScalar();
            return Convert.ToString(result) ?? string.Empty;
        }
    }

    private static uint GetForeignKeyChecks(MySqlConnection connection)
    {
        using (var cmd = new MySqlCommand("SELECT @@FOREIGN_KEY_CHECKS;", connection))
        {
            var result = cmd.ExecuteScalar();
            return Convert.ToUInt32(result);
        }
    }

    private static void SetForeignKeyChecks(MySqlConnection connection, uint value)
    {
        using (var cmd = new MySqlCommand(string.Format("SET FOREIGN_KEY_CHECKS={0};", value), connection))
        {
            cmd.ExecuteNonQuery();
        }
    }

    private static string WrapName(string name)
    {
        return string.Format("`{0}`", name);
    }

    private static string StripDefiner(string createStatement)
    {
        if (string.IsNullOrEmpty(createStatement))
        {
            return createStatement;
        }

        var withoutVersionedDefiner = Regex.Replace(
            createStatement,
            @"\/\*![0-9]{5}\s+DEFINER\s*=\s*[^*]*\*\/",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        return Regex.Replace(
            withoutVersionedDefiner,
            "\\s*DEFINER\\s*=\\s*[^\\s]+",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }
}
