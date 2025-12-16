using System.Management;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;


namespace GiroServerOps
{
    public class DashboardViewModel
    {
        public System.Collections.ObjectModel.ObservableCollection<KpiCard> Cards { get; } =
            new System.Collections.ObjectModel.ObservableCollection<KpiCard>();

        private readonly DispatcherTimer _timer;

        private readonly Dictionary<string, KpiCard> _map = new Dictionary<string, KpiCard>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _prev = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private readonly int _cpuCores = Environment.ProcessorCount;

        private PerformanceCounter? _cpuHost;
        private PerformanceCounter? _cpuSql;
        private string _sqlProcInstance = "";

        private readonly string? _sqlConnectionString;


        private bool _updating;

        public DashboardViewModel(string sqlConnectionString)
        {
            _sqlConnectionString = sqlConnectionString ?? "";
            AddCard("CPU", "inicializando…", "—", KpiStatus.Ok);
            AddCard("RAM", "inicializando…", "—", KpiStatus.Ok);
            AddCard("Disco SQL", "inicializando…", "—", KpiStatus.Ok);
            AddCard("LOG/DB", "inicializando…", "—", KpiStatus.Ok);

            TryInitCounters();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, __) => await UpdateAsync();
            _timer.Start();
        }

        private void AddCard(string title, string value, string delta, KpiStatus status)
        {
            var c = new KpiCard { Title = title, Value = value, Delta = delta, Status = status };
            Cards.Add(c);
            _map[title] = c;
        }

        private void TryInitCounters()
        {
            try
            {
                _cpuHost = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuHost.NextValue();
            }
            catch { _cpuHost = null; }

            try
            {
                _sqlProcInstance = FindSqlServrProcessInstanceName();
                if (!string.IsNullOrWhiteSpace(_sqlProcInstance))
                {
                    _cpuSql = new PerformanceCounter("Process", "% Processor Time", _sqlProcInstance, true);
                    _cpuSql.NextValue();
                }
                else _cpuSql = null;
            }
            catch { _cpuSql = null; }

            
        }

        private bool TryReadPhysicalRam(out double usedPct, out double availMb)
        {
            usedPct = double.NaN;
            availMb = double.NaN;

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                using (var results = searcher.Get())
                {
                    var mo = results.Cast<ManagementObject>().FirstOrDefault();
                    if (mo == null) return false;

                    var totalKbObj = mo["TotalVisibleMemorySize"];
                    var freeKbObj = mo["FreePhysicalMemory"];

                    if (totalKbObj == null || freeKbObj == null) return false;

                    var totalKb = Convert.ToDouble(totalKbObj);
                    var freeKb = Convert.ToDouble(freeKbObj);

                    if (totalKb <= 0) return false;

                    availMb = freeKb / 1024.0;

                    var used = (totalKb - freeKb) / totalKb;
                    usedPct = Clamp(used * 100.0, 0, 100);

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FindSqlServrProcessInstanceName()
        {
            try
            {
                var cat = new PerformanceCounterCategory("Process");
                var names = cat.GetInstanceNames();

                var candidates = names
                    .Where(n => n.Equals("sqlservr", StringComparison.OrdinalIgnoreCase)
                             || n.StartsWith("sqlservr#", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (candidates.Count == 0) return "";

                string best = candidates[0];
                int bestId = -1;

                foreach (var inst in candidates)
                {
                    try
                    {
                        using (var pc = new PerformanceCounter("Process", "ID Process", inst, true))
                        {
                            var pid = (int)pc.NextValue();
                            if (pid > 0 && pid > bestId)
                            {
                                bestId = pid;
                                best = inst;
                            }
                        }
                    }
                    catch { }
                }

                return best;
            }
            catch
            {
                return "";
            }
        }

        private async Task UpdateAsync()
        {
            if (_updating) return;
            _updating = true;

            try
            {
                var cpuHost = ReadCounter(_cpuHost);
                var cpuFree = double.IsNaN(cpuHost) ? double.NaN : Math.Max(0, 100.0 - Clamp(cpuHost, 0, 100));

                var cpuSqlRaw = ReadCounter(_cpuSql);
                var cpuSql = double.IsNaN(cpuSqlRaw) ? double.NaN : Clamp(cpuSqlRaw / _cpuCores, 0, 100);

                var okRam = TryReadPhysicalRam(out var ramUsedPct, out var ramAvailMb);
                var disk = await TryReadSqlDiskAsync();

                var log = await TryReadSqlLogVsDbAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    UpdateCpu(cpuSql, cpuHost, cpuFree);
                    UpdateRam(okRam, ramUsedPct, ramAvailMb);
                    UpdateSqlDisk(disk.ok, disk.usedGb, disk.freeGb);
                    UpdateLogVsDb(log.ok, log.logGb, log.totalGb);
                });

            }
            finally
            {
                _updating = false;
            }
        }

        private async Task<(bool ok, double logGb, double totalGb)> TryReadSqlLogVsDbAsync()
        {
            double logGb = double.NaN;
            double totalGb = double.NaN;

            if (string.IsNullOrWhiteSpace(_sqlConnectionString))
                return (false, logGb, totalGb);

            try
            {
                using (var cn = new Microsoft.Data.SqlClient.SqlConnection(_sqlConnectionString))
                {
                    await cn.OpenAsync();

                    using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                SELECT
                    SUM(CASE WHEN mf.type = 1 THEN CAST(mf.size AS bigint) ELSE 0 END) * 8.0 / 1024 / 1024 AS LogGb,
                    SUM(CAST(mf.size AS bigint)) * 8.0 / 1024 / 1024 AS TotalGb
                FROM sys.master_files mf
                INNER JOIN sys.databases d ON d.database_id = mf.database_id
                WHERE d.state = 0 AND d.database_id > 4;", cn))
                    {
                        using (var rd = await cmd.ExecuteReaderAsync())
                        {
                            if (await rd.ReadAsync())
                            {
                                if (rd.FieldCount >= 1 && rd[0] != DBNull.Value) logGb = Convert.ToDouble(rd[0]);
                                if (rd.FieldCount >= 2 && rd[1] != DBNull.Value) totalGb = Convert.ToDouble(rd[1]);
                            }
                        }
                    }

                    var ok = !(double.IsNaN(logGb) || double.IsNaN(totalGb) || totalGb <= 0);
                    return (ok, logGb, totalGb);
                }
            }
            catch
            {
                return (false, logGb, totalGb);
            }
        }

        private void UpdateLogVsDb(bool ok, double logGb, double totalGb)
        {
            if (!_map.TryGetValue("LOG/DB", out var card)) return;

            if (!ok)
            {
                card.Value = "—";
                card.Delta = "—";
                card.Status = KpiStatus.Warning;
                return;
            }

            var log = Math.Max(0, logGb);
            var total = Math.Max(0.000001, totalGb);
            var pct = Clamp((log / total) * 100.0, 0, 999);

            card.Value = $"{pct:0}%";
            card.Delta = $"Log {log:0.0} GB de {total:0.0} GB";

            if (pct >= 80) card.Status = KpiStatus.Critical;
            else if (pct >= 60) card.Status = KpiStatus.Warning;
            else card.Status = KpiStatus.Ok;

            //UpdateDelta("LOG/DB", pct);
        }

        private async Task<(bool ok, double usedGb, double freeGb)> TryReadSqlDiskAsync()
        {
            double usedGb = double.NaN;
            double freeGb = double.NaN;

            if (string.IsNullOrWhiteSpace(_sqlConnectionString))
                return (false, usedGb, freeGb);

            try
            {
                using (var cn = new Microsoft.Data.SqlClient.SqlConnection(_sqlConnectionString))
                {
                    await cn.OpenAsync();

                    using (var cmdUsed = new Microsoft.Data.SqlClient.SqlCommand(@"
                        SELECT SUM(CAST(size AS bigint)) * 8.0 / 1024 / 1024 AS UsedGb
                        FROM sys.master_files;", cn))
                    {
                        var obj = await cmdUsed.ExecuteScalarAsync();
                        if (obj != null && obj != DBNull.Value)
                            usedGb = Convert.ToDouble(obj);
                    }

                    using (var cmdFree = new Microsoft.Data.SqlClient.SqlCommand("EXEC master..xp_fixeddrives;", cn))
                    using (var rd = await cmdFree.ExecuteReaderAsync())
                    {
                        double sumMb = 0;
                        while (await rd.ReadAsync())
                        {
                            if (rd.FieldCount >= 2 && rd[1] != DBNull.Value)
                                sumMb += Convert.ToDouble(rd[1]);
                        }
                        freeGb = sumMb / 1024.0;
                    }

                    var ok = !(double.IsNaN(usedGb) || double.IsNaN(freeGb));
                    return (ok, usedGb, freeGb);
                }
            }
            catch
            {
                return (false, usedGb, freeGb);
            }
        }


        private void UpdateSqlDisk(bool ok, double usedGb, double freeGb)
        {
            if (!_map.TryGetValue("Disco SQL", out var card)) return;

            if (!ok)
            {
                card.Value = "—";
                card.Delta = "—";
                card.Status = KpiStatus.Warning;
                return;
            }

            var used = Math.Max(0, usedGb);
            var free = Math.Max(0, freeGb);

            var total = used + free;
            var usedPct = total <= 0 ? 0 : (used / total) * 100.0;

            var status = usedPct >= 90 ? KpiStatus.Critical : (usedPct >= 80 ? KpiStatus.Warning : KpiStatus.Ok);

            SetMetric("Disco SQL", usedPct, $"{used:0.0} GB usados • {free:0.0} GB libres", "%", status);
        }


        private static double ReadCounter(PerformanceCounter? pc)
        {
            try { return pc == null ? double.NaN : pc.NextValue(); }
            catch { return double.NaN; }
        }

        private void UpdateCpu(double cpuSql, double cpuHost, double cpuFree)
        {
            if (!_map.TryGetValue("CPU", out var card)) return;

            var hasHost = !double.IsNaN(cpuHost) && !double.IsNaN(cpuFree);
            var hasSql = !double.IsNaN(cpuSql);

            if (!hasHost && !hasSql)
            {
                card.Value = "—";
                card.Delta = "—";
                card.Status = KpiStatus.Warning;
                return;
            }

            if (!hasSql && hasHost)
            {
                card.Value = $"{Clamp(cpuHost, 0, 100):0}% host • {Clamp(cpuFree, 0, 100):0}% libre";
                card.Delta = string.IsNullOrWhiteSpace(_sqlProcInstance) ? "SQL: no detectado" : "SQL: no disponible";
                card.Status = KpiStatus.Warning;
                return;
            }

            var sql = Clamp(cpuSql, 0, 100);
            var freeText = hasHost ? $"{Clamp(cpuFree, 0, 100):0}% libre" : "libre: —";

            var status = sql >= 85 ? KpiStatus.Critical : (sql >= 70 ? KpiStatus.Warning : KpiStatus.Ok);

            SetMetric("CPU", sql, $"{sql:0}% SQL • {freeText}", "%", status);
        }

        private void UpdateRam(bool okRam, double usedPct, double availMb)
        {
            if (!_map.TryGetValue("RAM", out var card)) return;

            if (!okRam || double.IsNaN(usedPct) || double.IsNaN(availMb))
            {
                card.Value = "—";
                card.Delta = "—";
                card.Status = KpiStatus.Warning;
                return;
            }

            var used = Clamp(usedPct, 0, 100);

            var status = used >= 90 ? KpiStatus.Critical : (used >= 75 ? KpiStatus.Warning : KpiStatus.Ok);

            SetMetric("RAM", used, $"{used:0}% usado • {availMb/1024:0} GB libre", "%", status);
        }


        private void SetMetric(string title, double raw, string display, string unitKey, KpiStatus status)
        {
            if (!_map.TryGetValue(title, out var card)) return;

            var prevKey = $"{title}:{unitKey}";
            _prev.TryGetValue(prevKey, out var prev);
            _prev[prevKey] = raw;

            card.Value = display;
            card.Status = status;

            if (prev == 0 && raw == 0)
            {
                card.Delta = "0.00% vs anterior";
                return;
            }

            var diff = raw - prev;
            var sign = diff >= 0 ? "+" : "-";
            card.Delta = $"{sign}{Math.Abs(diff):0.00}% vs anterior";
        }

        private static double Clamp(double v, double min, double max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
