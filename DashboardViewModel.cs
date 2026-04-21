using System.Management;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NetcoServerConsole
{
    public class DashboardViewModel : IDisposable
    {
        public ObservableCollection<KpiCard> Cards { get; } = new ObservableCollection<KpiCard>();

        private readonly DispatcherTimer _timer;

        private readonly Dictionary<string, KpiCard> _map = new Dictionary<string, KpiCard>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _prev = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private readonly int _cpuCores = Environment.ProcessorCount;

        private PerformanceCounter? _cpuHost;
        private PerformanceCounter? _cpuSql;
        private string _sqlProcInstance = "";

        private readonly string? _sqlConnectionString;

        private readonly Dictionary<string, PerformanceCounter> _diskReadCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PerformanceCounter> _diskWriteCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PerformanceCounter> _diskBusyCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SqlDriveIoSnapshot> _prevSqlDriveIo = new Dictionary<string, SqlDriveIoSnapshot>(StringComparer.OrdinalIgnoreCase);

        private bool _updating;
        private bool _disposed;
        private Task? _initializeTask;

        public DashboardViewModel(string sqlConnectionString)
        {
            _sqlConnectionString = sqlConnectionString ?? "";
            AddCard("CPU", "inicializando…", "—", KpiStatus.Ok);
            AddCard("RAM", "inicializando…", "—", KpiStatus.Ok);
            AddCard("Disco SQL", "inicializando…", "—", KpiStatus.Ok);
            AddCard("LOG/DB", "inicializando…", "—", KpiStatus.Ok);
            AddCard("Conexiones", "inicializando…", "—", KpiStatus.Ok);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += Timer_Tick;
        }

        public Task InitializeAsync()
        {
            if (_initializeTask == null)
                _initializeTask = InitializeCoreAsync();

            return _initializeTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= Timer_Tick;

            DisposeCounter(ref _cpuHost);
            DisposeCounter(ref _cpuSql);
            DisposeCounters(_diskReadCounters);
            DisposeCounters(_diskWriteCounters);
            DisposeCounters(_diskBusyCounters);

            _prevSqlDriveIo.Clear();
            _map.Clear();
            _prev.Clear();
            Cards.Clear();
        }

        private async Task InitializeCoreAsync()
        {
            if (_disposed)
                return;

            await Task.Run(() =>
            {
                TryInitCounters();
                TryInitDiskIoCounters();
            }).ConfigureAwait(false);

            if (_disposed)
                return;

            await UpdateAsync().ConfigureAwait(false);

            if (_disposed)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                    _timer.Start();
            });
        }

        private async void Timer_Tick(object? sender, EventArgs e)
        {
            await UpdateAsync();
        }

        private void AddCard(string title, string value, string delta, KpiStatus status)
        {
            if (_map.ContainsKey(title))
                return;

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

        private void TryInitDiskIoCounters()
        {
            try
            {
                var cat = new PerformanceCounterCategory("LogicalDisk");
                var instances = cat.GetInstanceNames()
                    .Where(IsValidLogicalDiskInstance)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var instance in instances)
                {
                    EnsureDiskCounters(instance);
                }
            }
            catch
            {
            }
        }

        private void EnsureDiskCounters(string instance)
        {
            if (!_diskReadCounters.ContainsKey(instance))
            {
                try
                {
                    var pc = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instance, true);
                    pc.NextValue();
                    _diskReadCounters[instance] = pc;
                }
                catch
                {
                }
            }

            if (!_diskWriteCounters.ContainsKey(instance))
            {
                try
                {
                    var pc = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instance, true);
                    pc.NextValue();
                    _diskWriteCounters[instance] = pc;
                }
                catch
                {
                }
            }

            if (!_diskBusyCounters.ContainsKey(instance))
            {
                try
                {
                    var pc = new PerformanceCounter("LogicalDisk", "% Disk Time", instance, true);
                    pc.NextValue();
                    _diskBusyCounters[instance] = pc;
                }
                catch
                {
                }
            }
        }

        private static bool IsValidLogicalDiskInstance(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.IndexOf("HarddiskVolume", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private void EnsureDiskCards(IEnumerable<DiskIoInfo> disks)
        {
            foreach (var disk in disks.OrderBy(x => x.Drive, StringComparer.OrdinalIgnoreCase))
            {
                var title = BuildDiskCardTitle(disk.Drive, disk.IsSqlDisk);
                if (_map.ContainsKey(title))
                    continue;

                AddCard(title, "inicializando…", "—", KpiStatus.Ok);
            }
        }

        private static string BuildDiskCardTitle(string drive, bool isSqlDisk)
        {
            return isSqlDisk ? $"I/O Disco {drive} • SQL" : $"I/O Disco {drive}";
        }

        private void UpdateConnections(bool ok, int count)
        {
            if (!_map.TryGetValue("Conexiones", out var card)) return;

            if (!ok)
            {
                card.Value = "—";
                card.Delta = "—";
                card.Status = KpiStatus.Warning;
                return;
            }

            SetMetric("Conexiones", (double)count, $"{count} activas", "count", KpiStatus.Ok);
        }

        private async Task<(bool ok, int count)> TryReadSqlConnectionsAsync()
        {
            int count = 0;
            if (string.IsNullOrWhiteSpace(_sqlConnectionString))
                return (false, count);

            try
            {
                using (var cn = new Microsoft.Data.SqlClient.SqlConnection(_sqlConnectionString))
                {
                    await cn.OpenAsync();
                    using (var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_connections;", cn))
                    {
                        var result = await cmd.ExecuteScalarAsync();
                        if (result != null && result != DBNull.Value)
                        {
                            count = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                            return (true, count);
                        }
                    }
                }
                return (false, count);
            }
            catch
            {
                return (false, count);
            }
        }

        private bool TryReadPhysicalRam(out double usedPct, out double availMb)
        {
            usedPct = double.NaN;
            availMb = double.NaN;

            try
            {
                var status = new MemoryStatusEx();
                if (!GlobalMemoryStatusEx(status) || status.TotalPhysical == 0)
                    return false;

                availMb = status.AvailablePhysical / 1024.0 / 1024.0;

                var used = (status.TotalPhysical - status.AvailablePhysical) / (double)status.TotalPhysical;
                usedPct = Clamp(used * 100.0, 0, 100);

                return true;
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
            if (_disposed || _updating) return;
            _updating = true;

            try
            {
                var cpuHost = ReadCounter(_cpuHost);
                var cpuFree = double.IsNaN(cpuHost) ? double.NaN : Math.Max(0, 100.0 - Clamp(cpuHost, 0, 100));

                var cpuSqlRaw = ReadCounter(_cpuSql);
                var cpuSql = double.IsNaN(cpuSqlRaw) ? double.NaN : Clamp(cpuSqlRaw / _cpuCores, 0, 100);

                var okRam = TryReadPhysicalRam(out var ramUsedPct, out var ramAvailMb);
                var diskSpace = await TryReadSqlDiskAsync();
                var log = await TryReadSqlLogVsDbAsync();
                var conn = await TryReadSqlConnectionsAsync();
                var diskIo = await TryReadDiskIoAsync();

                if (_disposed)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed)
                        return;

                    EnsureDiskCards(diskIo);

                    UpdateCpu(cpuSql, cpuHost, cpuFree);
                    UpdateRam(okRam, ramUsedPct, ramAvailMb);
                    UpdateSqlDisk(diskSpace.ok, diskSpace.usedGb, diskSpace.freeGb);
                    UpdateLogVsDb(log.ok, log.logGb, log.totalGb);
                    UpdateConnections(conn.ok, conn.count);
                    UpdateDiskIo(diskIo);
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
                                if (rd.FieldCount >= 1 && rd[0] != DBNull.Value) logGb = Convert.ToDouble(rd[0], CultureInfo.InvariantCulture);
                                if (rd.FieldCount >= 2 && rd[1] != DBNull.Value) totalGb = Convert.ToDouble(rd[1], CultureInfo.InvariantCulture);
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
                            usedGb = Convert.ToDouble(obj, CultureInfo.InvariantCulture);
                    }

                    using (var cmdFree = new Microsoft.Data.SqlClient.SqlCommand("EXEC master..xp_fixeddrives;", cn))
                    using (var rd = await cmdFree.ExecuteReaderAsync())
                    {
                        double sumMb = 0;
                        while (await rd.ReadAsync())
                        {
                            if (rd.FieldCount >= 2 && rd[1] != DBNull.Value)
                                sumMb += Convert.ToDouble(rd[1], CultureInfo.InvariantCulture);
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

        private async Task<List<DiskIoInfo>> TryReadDiskIoAsync()
        {
            var result = new List<DiskIoInfo>();

            try
            {
                var logicalDrives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => NormalizeDrive(d.Name))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var drive in logicalDrives)
                {
                    EnsureDiskCounters(drive);
                }

                var sqlIoByDrive = await TryReadSqlDriveIoPerSecondAsync();
                var sqlDrives = new HashSet<string>(sqlIoByDrive.Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var drive in logicalDrives)
                {
                    var readBytes = NonNegative(ReadCounter(_diskReadCounters.TryGetValue(drive, out var readPc) ? readPc : null));
                    var writeBytes = NonNegative(ReadCounter(_diskWriteCounters.TryGetValue(drive, out var writePc) ? writePc : null));
                    var busyPct = Clamp(ReadCounter(_diskBusyCounters.TryGetValue(drive, out var busyPc) ? busyPc : null), 0, 100);

                    sqlIoByDrive.TryGetValue(drive, out var sqlIo);
                    
                    var totalBytes = readBytes + writeBytes;
                    var sqlReadBytes = Math.Max(0, sqlIo?.ReadBytesPerSec ?? 0);
                    var sqlWriteBytes = Math.Max(0, sqlIo?.WriteBytesPerSec ?? 0);
                    var sqlBytes = sqlReadBytes + sqlWriteBytes;
                    var sqlSharePct = totalBytes <= 0 ? 0 : Clamp((sqlBytes / totalBytes) * 100.0, 0, 100);

                    result.Add(new DiskIoInfo
                    {
                        Drive = drive,
                        ReadBytesPerSec = readBytes,
                        WriteBytesPerSec = writeBytes,
                        BusyPct = busyPct,
                        IsSqlDisk = sqlDrives.Contains(drive),
                        SqlReadBytesPerSec = sqlReadBytes,
                        SqlWriteBytesPerSec = sqlWriteBytes,
                        SqlSharePct = sqlSharePct
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private async Task<Dictionary<string, SqlDriveIoRate>> TryReadSqlDriveIoPerSecondAsync()
        {
            var result = new Dictionary<string, SqlDriveIoRate>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(_sqlConnectionString))
                return result;

            try
            {
                var now = DateTime.UtcNow;
                var current = new Dictionary<string, SqlDriveIoSnapshot>(StringComparer.OrdinalIgnoreCase);

                using (var cn = new Microsoft.Data.SqlClient.SqlConnection(_sqlConnectionString))
                {
                    await cn.OpenAsync();

                    using (var cmd = new Microsoft.Data.SqlClient.SqlCommand(@"
                    SELECT
                        UPPER(LEFT(mf.physical_name, 2)) AS Drive,
                        SUM(CAST(vfs.num_of_bytes_read AS bigint)) AS BytesRead,
                        SUM(CAST(vfs.num_of_bytes_written AS bigint)) AS BytesWritten
                    FROM sys.dm_io_virtual_file_stats(NULL, NULL) vfs
                    INNER JOIN sys.master_files mf
                        ON mf.database_id = vfs.database_id
                       AND mf.file_id = vfs.file_id
                    WHERE LEFT(mf.physical_name, 2) LIKE '[A-Z]:'
                    GROUP BY UPPER(LEFT(mf.physical_name, 2));", cn))
                    using (var rd = await cmd.ExecuteReaderAsync())
                    {
                        while (await rd.ReadAsync())
                        {
                            var drive = rd[0] == DBNull.Value ? "" : Convert.ToString(rd[0], CultureInfo.InvariantCulture) ?? "";
                            if (string.IsNullOrWhiteSpace(drive))
                                continue;

                            var bytesRead = rd[1] == DBNull.Value ? 0L : Convert.ToInt64(rd[1], CultureInfo.InvariantCulture);
                            var bytesWritten = rd[2] == DBNull.Value ? 0L : Convert.ToInt64(rd[2], CultureInfo.InvariantCulture);

                            current[NormalizeDrive(drive)] = new SqlDriveIoSnapshot
                            {
                                AtUtc = now,
                                BytesRead = bytesRead,
                                BytesWritten = bytesWritten
                            };
                        }
                    }
                }

                foreach (var kvp in current)
                {
                    if (!_prevSqlDriveIo.TryGetValue(kvp.Key, out var prev))
                    {
                        _prevSqlDriveIo[kvp.Key] = kvp.Value;
                        result[kvp.Key] = new SqlDriveIoRate();
                        continue;
                    }

                    var elapsed = (kvp.Value.AtUtc - prev.AtUtc).TotalSeconds;
                    if (elapsed <= 0)
                    {
                        _prevSqlDriveIo[kvp.Key] = kvp.Value;
                        result[kvp.Key] = new SqlDriveIoRate();
                        continue;
                    }

                    var deltaRead = Math.Max(0L, kvp.Value.BytesRead - prev.BytesRead);
                    var deltaWrite = Math.Max(0L, kvp.Value.BytesWritten - prev.BytesWritten);

                    result[kvp.Key] = new SqlDriveIoRate
                    {
                        ReadBytesPerSec = deltaRead / elapsed,
                        WriteBytesPerSec = deltaWrite / elapsed
                    };

                    _prevSqlDriveIo[kvp.Key] = kvp.Value;
                }

                foreach (var stale in _prevSqlDriveIo.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).ToList())
                    _prevSqlDriveIo.Remove(stale);
            }
            catch
            {
            }

            return result;
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

        private void UpdateDiskIo(IEnumerable<DiskIoInfo> disks)
        {
            foreach (var disk in disks)
            {
                var title = BuildDiskCardTitle(disk.Drive, disk.IsSqlDisk);
                if (!_map.TryGetValue(title, out var card))
                    continue;

                var readMb = disk.ReadBytesPerSec / 1024.0 / 1024.0;
                var writeMb = disk.WriteBytesPerSec / 1024.0 / 1024.0;
                var totalMb = readMb + writeMb;
                var busy = Clamp(disk.BusyPct, 0, 100);

                card.Value = $"{busy:0}% activo";

                if (disk.IsSqlDisk)
                {
                    var sqlReadMb = disk.SqlReadBytesPerSec / 1024.0 / 1024.0;
                    var sqlWriteMb = disk.SqlWriteBytesPerSec / 1024.0 / 1024.0;
                    card.Delta = $"L {readMb:0.0} MB/s • E {writeMb:0.0} MB/s • SQL {disk.SqlSharePct:0}% del disco • Inst L {sqlReadMb:0.0} / E {sqlWriteMb:0.0}";
                }
                else
                {
                    card.Delta = $"L {readMb:0.0} MB/s • E {writeMb:0.0} MB/s • Total {totalMb:0.0} MB/s";
                }

                if (busy >= 90) card.Status = KpiStatus.Critical;
                else if (busy >= 70) card.Status = KpiStatus.Warning;
                else card.Status = KpiStatus.Ok;

                var prevKey = $"diskio:{disk.Drive}";
                _prev.TryGetValue(prevKey, out var prevBusy);
                _prev[prevKey] = busy;

                var diff = busy - prevBusy;
                var sign = diff >= 0 ? "+" : "-";
                var change = $"{sign}{Math.Abs(diff):0.00}% vs anterior";

                if (disk.IsSqlDisk)
                    card.Delta = card.Delta + " • " + change;
                else
                    card.Delta = card.Delta + " • " + change;
            }
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

            SetMetric("RAM", used, $"{used:0}% usado • {availMb / 1024:0} GB libre", "%", status);
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

        private static string NormalizeDrive(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var drive = value.Trim().ToUpperInvariant();
            if (drive.Length >= 2 && drive[1] == ':')
                return drive.Substring(0, 2);
            return drive.TrimEnd('\\');
        }

        private static double NonNegative(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return value < 0 ? 0 : value;
        }

        private static double Clamp(double v, double min, double max)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return min;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static void DisposeCounters(Dictionary<string, PerformanceCounter> counters)
        {
            foreach (var counter in counters.Values)
            {
                try
                {
                    counter.Dispose();
                }
                catch
                {
                }
            }

            counters.Clear();
        }

        private static void DisposeCounter(ref PerformanceCounter? counter)
        {
            try
            {
                counter?.Dispose();
            }
            catch
            {
            }

            counter = null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        private sealed class SqlDriveIoSnapshot
        {
            public DateTime AtUtc { get; set; }
            public long BytesRead { get; set; }
            public long BytesWritten { get; set; }
        }

        private sealed class SqlDriveIoRate
        {
            public double ReadBytesPerSec { get; set; }
            public double WriteBytesPerSec { get; set; }
        }

        private sealed class DiskIoInfo
        {
            public string Drive { get; set; } = "";
            public double ReadBytesPerSec { get; set; }
            public double WriteBytesPerSec { get; set; }
            public double BusyPct { get; set; }
            public bool IsSqlDisk { get; set; }
            public double SqlReadBytesPerSec { get; set; }
            public double SqlWriteBytesPerSec { get; set; }
            public double SqlSharePct { get; set; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }
    }
}
