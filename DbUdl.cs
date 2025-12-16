using System;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text;

namespace GiroServerOps
{
    public static class DbUdl
    {
        public static string UdlFileName => "GiroServerOps.udl";
        public static string UdlPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UdlFileName);

        public static bool TryEnsureValidUdl(out string connectionString)
        {
            connectionString = "";

            if (!File.Exists(UdlPath))
                return false;

            if (!TryReadConnectionStringFromUdl(UdlPath, out connectionString))
                return false;

            if (!TestConnection(connectionString))
                return false;

            connectionString = ToSqlClientConnectionString(connectionString);
            return true;

        }

        public static bool TryWriteUdlIfConnectionOk(string connectionString)
        {
            if (!TestConnection(connectionString))
                return false;

            WriteUdl(UdlPath, connectionString);
            return true;
        }

        public static bool TestConnection(string connectionString)
        {
            try
            {
                using (var cn = new OleDbConnection(connectionString))
                {
                    cn.Open();
                    cn.Close();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string BuildSqlAuth(string instance, string database, string user, string pass)
        {
            instance = (instance ?? "").Trim();
            database = (database ?? "").Trim();
            user = (user ?? "").Trim();

            return $"Provider=SQLOLEDB.1;Data Source={instance};Initial Catalog={database};User ID={user};Password={pass};Persist Security Info=True;";
        }

        public static string BuildWindowsAuth(string instance, string database)
        {
            instance = (instance ?? "").Trim();
            database = (database ?? "").Trim();

            return $"Provider=SQLOLEDB.1;Data Source={instance};Initial Catalog={database};Integrated Security=SSPI;Persist Security Info=False;";
        }

        private static void WriteUdl(string path, string connectionString)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[oledb]");
            sb.AppendLine("; Everything after this line is an OLE DB initstring");
            sb.AppendLine(connectionString);

            File.WriteAllText(path, sb.ToString(), Encoding.Unicode);
        }

        private static bool TryReadConnectionStringFromUdl(string path, out string connectionString)
        {
            connectionString = "";

            try
            {
                var lines = File.ReadAllLines(path, Encoding.Unicode)
                                .Select(x => x.Trim())
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .ToList();

                var cs = lines.FirstOrDefault(x => x.StartsWith("Provider=", StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(cs))
                    return false;

                connectionString = cs;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string ToSqlClientConnectionString(string oleDbConnectionString)
        {
            var ole = new System.Data.OleDb.OleDbConnectionStringBuilder(oleDbConnectionString);

            var server = (ole.TryGetValue("Data Source", out var ds) ? ds?.ToString() : "") ?? "";
            var database = (ole.TryGetValue("Initial Catalog", out var ic) ? ic?.ToString() : "") ?? "";

            var scb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                TrustServerCertificate = true,
                Encrypt = false
            };

            if (ole.TryGetValue("Integrated Security", out var isec) && (isec?.ToString() ?? "").Equals("SSPI", StringComparison.OrdinalIgnoreCase))
            {
                scb.IntegratedSecurity = true;
            }
            else
            {
                scb.UserID = (ole.TryGetValue("User ID", out var uid) ? uid?.ToString() : "") ?? "";
                scb.Password = (ole.TryGetValue("Password", out var pwd) ? pwd?.ToString() : "") ?? "";
            }

            return scb.ConnectionString;
        }

    }
}
