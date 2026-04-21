using Microsoft.Data.SqlClient;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace NetcoServerConsole
{
    public partial class QuerySqlView : UserControl, INotifyPropertyChanged, IDisposable
    {
        private const int PreviewRowLimit = 10000;
        private const int PreviewCellBudget = 200000;
        private const int MaxPreviewTextLength = 2048;

        private readonly string _baseConnectionString;
        private readonly object _activeCommandLock = new object();

        private SqlCommand? _activeCommand;
        private CancellationTokenSource? _executionCts;
        private QueryGridResult? _lastCombinedResult;
        private bool _isExecuting;
        private bool _disposed;
        private volatile bool _stopRequested;

        public ObservableCollection<DatabaseSelectionItem> Databases { get; } =
            new ObservableCollection<DatabaseSelectionItem>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public QuerySqlView()
        {
            InitializeComponent();
            DataContext = this;

            if (!DbUdl.TryEnsureValidUdl(out _baseConnectionString))
                _baseConnectionString = "";

            Loaded += QuerySqlView_Loaded;
        }

        private async void QuerySqlView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= QuerySqlView_Loaded;
            btnCopyWithHeaders.IsEnabled = false;
            btnStopExecution.Visibility = Visibility.Collapsed;
            await LoadDatabasesAsync();
        }

        private async void ReloadDatabases_Click(object sender, RoutedEventArgs e)
        {
            if (_disposed)
                return;

            await LoadDatabasesAsync();
        }

        private void SelectAllDatabases_Click(object sender, RoutedEventArgs e)
        {
            foreach (var db in Databases)
                db.IsSelected = true;
        }

        private void ClearDatabases_Click(object sender, RoutedEventArgs e)
        {
            foreach (var db in Databases)
                db.IsSelected = false;
        }

        private async void Execute_Click(object sender, RoutedEventArgs e)
        {
            if (_disposed || _isExecuting)
                return;

            await ExecuteQueryAsync();
        }

        private void StopExecution_Click(object sender, RoutedEventArgs e)
        {
            if (_disposed || !_isExecuting)
                return;

            RequestStopExecution();
            txtStatus.Text = "Deteniendo consulta...";
        }

        private void ClearQuery_Click(object sender, RoutedEventArgs e)
        {
            txtQuery.Text = "";
            txtQuery.Focus();
        }

        private void CopyWithHeaders_Click(object sender, RoutedEventArgs e)
        {
            if (_lastCombinedResult == null || _lastCombinedResult.Columns.Count == 0)
            {
                txtStatus.Text = "No hay resultados para copiar.";
                return;
            }

            var text = BuildClipboardText(_lastCombinedResult);
            Clipboard.SetText(text);
            txtStatus.Text = _lastCombinedResult.IsPreviewTrimmed
                ? "Vista previa copiada. El resultado completo se limitó para proteger memoria."
                : "Resultados copiados con encabezados.";
        }

        private async Task LoadDatabasesAsync()
        {
            if (_disposed)
                return;

            Databases.Clear();
            txtStatus.Text = "Cargando bases de datos...";
            txtFooter.Text = "";
            txtResult.Text = "";
            ClearGridResult();

            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                txtStatus.Text = "No hay una conexión válida configurada.";
                return;
            }

            try
            {
                using (var cn = new SqlConnection(_baseConnectionString))
                using (var cmd = new SqlCommand(@"
SELECT name
FROM sys.databases
WHERE state = 0
  AND HAS_DBACCESS(name) = 1
ORDER BY CASE WHEN database_id <= 4 THEN 0 ELSE 1 END, name;", cn))
                {
                    await cn.OpenAsync();
                    using (var rd = await cmd.ExecuteReaderAsync())
                    {
                        while (await rd.ReadAsync())
                        {
                            var name = rd[0] == DBNull.Value ? "" : Convert.ToString(rd[0]) ?? "";
                            if (!string.IsNullOrWhiteSpace(name))
                                Databases.Add(new DatabaseSelectionItem { Name = name });
                        }
                    }
                }

                txtStatus.Text = Databases.Count == 0
                    ? "No se encontraron bases de datos disponibles."
                    : Databases.Count + " base(s) disponible(s).";
            }
            catch (Exception ex)
            {
                txtStatus.Text = ex.Message;
            }
        }

        private async Task ExecuteQueryAsync()
        {
            if (_disposed)
                return;

            var sql = txtQuery.Text ?? "";
            var selected = Databases.Where(x => x.IsSelected).Select(x => x.Name).ToList();

            txtResult.Text = "";
            txtFooter.Text = "";
            ClearGridResult();

            if (string.IsNullOrWhiteSpace(_baseConnectionString))
            {
                txtStatus.Text = "No hay una conexión válida configurada.";
                return;
            }

            if (selected.Count == 0)
            {
                txtStatus.Text = "Selecciona al menos una base de datos.";
                return;
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                txtStatus.Text = "Escribe una consulta SQL.";
                return;
            }

            _executionCts = new CancellationTokenSource();
            _stopRequested = false;
            SetExecutionState(true);
            txtStatus.Text = "Ejecutando consulta...";

            try
            {
                QueryGridResult? combined = null;
                var text = new StringBuilder();
                var successfulExecutions = 0;
                var includeDatabaseColumn = selected.Count > 1;

                foreach (var database in selected)
                {
                    if (ShouldStopExecution())
                        break;

                    var oneResult = await ExecuteAgainstDatabaseAsync(database, sql, includeDatabaseColumn, _executionCts.Token);

                    if (oneResult.GridResult != null && oneResult.GridResult.Columns.Count > 0)
                    {
                        if (combined == null)
                        {
                            combined = oneResult.GridResult;
                        }
                        else if (CanMerge(combined, oneResult.GridResult))
                        {
                            AppendRows(combined, oneResult.GridResult);
                        }
                        else
                        {
                            AppendMessage(text, "[" + database + "] El resultado no se integró al grid porque su estructura es distinta a la del primer resultado.");
                        }
                    }

                    if (oneResult.WasCanceled)
                    {
                        AppendMessage(text, "[" + database + "] Consulta detenida por el usuario.");
                        break;
                    }

                    if (!oneResult.Success)
                    {
                        AppendMessage(text, "[" + database + "] ERROR");
                        if (!string.IsNullOrWhiteSpace(oneResult.TextResult))
                            AppendMessage(text, oneResult.TextResult);
                        continue;
                    }

                    successfulExecutions++;

                    if (!string.IsNullOrWhiteSpace(oneResult.TextResult))
                    {
                        AppendMessage(text, "[" + database + "]");
                        AppendMessage(text, oneResult.TextResult);
                    }
                }

                if (combined != null && combined.Columns.Count > 0)
                {
                    _lastCombinedResult = combined;
                    DisplayGridResult(combined);
                    tabResult.SelectedIndex = 0;
                    txtFooter.Text = BuildFooterText(combined);
                    btnCopyWithHeaders.IsEnabled = combined.Rows.Count > 0;

                    if (combined.IsPreviewTrimmed)
                    {
                        AppendMessage(text, "La vista Resultados se limitó a una vista previa para mantener la app fluida y contener el uso de RAM.");
                        AppendMessage(text, "Si necesitas todo el dataset, ejecuta una consulta más específica o reduce columnas y filas.");
                    }
                }
                else
                {
                    tabResult.SelectedIndex = 1;
                    txtFooter.Text = "Sin resultado tabular.";
                }

                txtResult.Text = string.IsNullOrWhiteSpace(text.ToString())
                    ? "Sin mensajes."
                    : text.ToString();

                if (ShouldStopExecution())
                    txtStatus.Text = combined != null ? "Consulta detenida por el usuario. Se muestran resultados parciales." : "Consulta detenida por el usuario.";
                else
                    txtStatus.Text = "Consulta finalizada en " + successfulExecutions + " base(s) de datos.";
            }
            catch (OperationCanceledException)
            {
                txtStatus.Text = "Consulta detenida por el usuario.";
                if (string.IsNullOrWhiteSpace(txtResult.Text))
                    txtResult.Text = "Consulta detenida por el usuario.";
                tabResult.SelectedIndex = _lastCombinedResult != null ? 0 : 1;
            }
            catch (Exception ex)
            {
                txtStatus.Text = ex.Message;
                txtResult.Text = ex.ToString();
                tabResult.SelectedIndex = 1;
            }
            finally
            {
                SetExecutionState(false);
                ClearActiveCommand();

                if (_executionCts != null)
                {
                    _executionCts.Dispose();
                    _executionCts = null;
                }

                _stopRequested = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Loaded -= QuerySqlView_Loaded;

            RequestStopExecution();
            ClearActiveCommand();

            if (_executionCts != null)
            {
                _executionCts.Dispose();
                _executionCts = null;
            }

            _lastCombinedResult = null;
            Databases.Clear();
            btnCopyWithHeaders.IsEnabled = false;
            txtFooter.Text = "";
            txtResult.Text = "";
            txtStatus.Text = "Listo.";

            AppMemoryCoordinator.ScheduleIdleTrim(() =>
            {
                gridResult.ItemsSource = null;
                gridResult.Columns.Clear();
            });
        }

        private static async Task<QueryExecutionResult> ReadBatchResultAsync(SqlDataReader rd, string database, bool includeDatabaseColumn, CancellationToken cancellationToken)
        {
            QueryGridResult? accumulatedResult = null;
            var messages = new StringBuilder();
            var setNumber = 0;

            while (true)
            {
                if (cancellationToken.IsCancellationRequested || rd.IsClosed)
                {
                    return new QueryExecutionResult
                    {
                        Success = true,
                        WasCanceled = true,
                        GridResult = accumulatedResult,
                        TextResult = messages.ToString().Trim()
                    };
                }

                setNumber++;

                if (rd.FieldCount > 0)
                {
                    var columns = BuildResultColumns(rd, includeDatabaseColumn);

                    if (accumulatedResult == null)
                        accumulatedResult = new QueryGridResult(columns, PreviewRowLimit, PreviewCellBudget);

                    int rowsRead;

                    if (CanMerge(accumulatedResult, columns))
                    {
                        rowsRead = await ReadCurrentResultSetAsync(rd, accumulatedResult, database, includeDatabaseColumn, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        rowsRead = await DrainCurrentResultSetAsync(rd, cancellationToken).ConfigureAwait(false);
                        AppendMessage(messages, "El resultset " + setNumber + " no se integró porque su estructura es distinta a la del primer resultset.");
                    }

                    AppendMessage(messages, rowsRead.ToString("N0", CultureInfo.CurrentCulture) + " fila(s) en resultset " + setNumber + ".");
                }
                else if (rd.RecordsAffected >= 0)
                {
                    AppendMessage(messages, rd.RecordsAffected.ToString("N0", CultureInfo.CurrentCulture) + " fila(s) afectada(s).");
                }

                bool hasNext;

                try
                {
                    if (cancellationToken.IsCancellationRequested || rd.IsClosed)
                    {
                        return new QueryExecutionResult
                        {
                            Success = true,
                            WasCanceled = true,
                            GridResult = accumulatedResult,
                            TextResult = messages.ToString().Trim()
                        };
                    }

                    hasNext = await rd.NextResultAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.IndexOf("NextResultAsync", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    ex.Message.IndexOf("reader is closed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new QueryExecutionResult
                    {
                        Success = true,
                        WasCanceled = true,
                        GridResult = accumulatedResult,
                        TextResult = messages.ToString().Trim()
                    };
                }
                catch (OperationCanceledException)
                {
                    return new QueryExecutionResult
                    {
                        Success = true,
                        WasCanceled = true,
                        GridResult = accumulatedResult,
                        TextResult = messages.ToString().Trim()
                    };
                }
                catch (SqlException ex) when (ex.Number == 0 && cancellationToken.IsCancellationRequested)
                {
                    return new QueryExecutionResult
                    {
                        Success = true,
                        WasCanceled = true,
                        GridResult = accumulatedResult,
                        TextResult = messages.ToString().Trim()
                    };
                }

                if (!hasNext)
                    break;
            }

            return new QueryExecutionResult
            {
                Success = true,
                GridResult = accumulatedResult,
                TextResult = messages.ToString().Trim()
            };
        }

        private async Task<QueryExecutionResult> ExecuteAgainstDatabaseAsync(string database, string sql, bool includeDatabaseColumn, CancellationToken cancellationToken)
        {
            QueryGridResult? accumulatedResult = null;
            var messages = new StringBuilder();

            try
            {
                var builder = new SqlConnectionStringBuilder(_baseConnectionString)
                {
                    InitialCatalog = database
                };

                using (var cn = new SqlConnection(builder.ConnectionString))
                {
                    await cn.OpenAsync(cancellationToken).ConfigureAwait(false);

                    var batches = SplitSqlBatches(sql);
                    var batchNumber = 0;

                    foreach (var batch in batches)
                    {
                        for (var repetition = 1; repetition <= batch.RepeatCount; repetition++)
                        {
                            if (ShouldStopExecution())
                            {
                                return new QueryExecutionResult
                                {
                                    Success = false,
                                    WasCanceled = true,
                                    GridResult = accumulatedResult,
                                    TextResult = messages.ToString().Trim()
                                };
                            }

                            batchNumber++;

                            using (var cmd = new SqlCommand(batch.CommandText, cn))
                            {
                                cmd.CommandTimeout = 180;
                                SetActiveCommand(cmd);

                                try
                                {
                                    using (var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false))
                                    {
                                        var batchResult = await ReadBatchResultAsync(rd, database, includeDatabaseColumn, cancellationToken).ConfigureAwait(false);

                                        if (batchResult.GridResult != null && batchResult.GridResult.Columns.Count > 0)
                                        {
                                            if (accumulatedResult == null)
                                            {
                                                accumulatedResult = batchResult.GridResult;
                                            }
                                            else if (CanMerge(accumulatedResult, batchResult.GridResult))
                                            {
                                                AppendRows(accumulatedResult, batchResult.GridResult);
                                            }
                                            else
                                            {
                                                AppendMessage(messages, "Lote " + batchNumber + ": el resultset no se integró porque su estructura es distinta a la del primer resultset tabular.");
                                            }
                                        }

                                        if (!string.IsNullOrWhiteSpace(batchResult.TextResult))
                                            AppendMessage(messages, "Lote " + batchNumber + ": " + batchResult.TextResult);

                                        if (batchResult.WasCanceled)
                                        {
                                            return new QueryExecutionResult
                                            {
                                                Success = false,
                                                WasCanceled = true,
                                                GridResult = accumulatedResult,
                                                TextResult = messages.ToString().Trim()
                                            };
                                        }
                                    }
                                }
                                finally
                                {
                                    ClearActiveCommand(cmd);
                                }
                            }
                        }
                    }

                    return new QueryExecutionResult
                    {
                        Success = true,
                        GridResult = accumulatedResult,
                        TextResult = messages.ToString().Trim()
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return new QueryExecutionResult
                {
                    Success = false,
                    WasCanceled = true,
                    GridResult = accumulatedResult,
                    TextResult = string.IsNullOrWhiteSpace(messages.ToString())
                        ? "Consulta detenida por el usuario."
                        : messages.ToString().Trim()
                };
            }
            catch (SqlException ex) when (ShouldStopExecution())
            {
                AppendMessage(messages, string.IsNullOrWhiteSpace(ex.Message) ? "Consulta detenida por el usuario." : ex.Message);

                return new QueryExecutionResult
                {
                    Success = false,
                    WasCanceled = true,
                    GridResult = accumulatedResult,
                    TextResult = messages.ToString().Trim()
                };
            }
            catch (SqlException ex) when (ex.Number == 0 && cancellationToken.IsCancellationRequested)
            {
                return new QueryExecutionResult
                {
                    Success = true,
                    WasCanceled = true,
                    GridResult = accumulatedResult,
                    TextResult = messages.ToString().Trim()
                };
            }
            catch (Exception ex)
            {
                AppendMessage(messages, ex.Message);

                return new QueryExecutionResult
                {
                    Success = false,
                    GridResult = accumulatedResult,
                    TextResult = messages.ToString().Trim()
                };
            }
        }

        private static async Task<int> ReadCurrentResultSetAsync(SqlDataReader rd, QueryGridResult targetResult, string database, bool includeDatabaseColumn, CancellationToken cancellationToken)
        {
            var rowsRead = 0;
            var offset = includeDatabaseColumn ? 1 : 0;

            while (!rd.IsClosed && await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object?[rd.FieldCount + offset];

                if (includeDatabaseColumn)
                    values[0] = database;

                for (var i = 0; i < rd.FieldCount; i++)
                    values[i + offset] = ReadCellPreviewValue(rd, i);

                targetResult.AddRow(values);
                rowsRead++;
            }

            return rowsRead;
        }

        private void SetExecutionState(bool isExecuting)
        {
            _isExecuting = isExecuting;
            btnExecute.IsEnabled = !isExecuting;
            btnStopExecution.Visibility = isExecuting ? Visibility.Visible : Visibility.Collapsed;
            btnStopExecution.IsEnabled = isExecuting;
        }

        private bool ShouldStopExecution()
        {
            return _stopRequested || (_executionCts != null && _executionCts.IsCancellationRequested);
        }

        private void RequestStopExecution()
        {
            _stopRequested = true;

            try
            {
                _executionCts?.Cancel();
            }
            catch
            {
            }

            SqlCommand? command;
            lock (_activeCommandLock)
                command = _activeCommand;

            try
            {
                command?.Cancel();
            }
            catch
            {
            }
        }

        private void SetActiveCommand(SqlCommand command)
        {
            lock (_activeCommandLock)
                _activeCommand = command;
        }

        private void ClearActiveCommand(SqlCommand? command = null)
        {
            lock (_activeCommandLock)
            {
                if (command == null || ReferenceEquals(_activeCommand, command))
                    _activeCommand = null;
            }
        }

        private void DisplayGridResult(QueryGridResult result)
        {
            gridResult.ItemsSource = null;
            gridResult.Columns.Clear();

            for (var i = 0; i < result.Columns.Count; i++)
            {
                var index = i;

                gridResult.Columns.Add(new DataGridTextColumn
                {
                    Header = result.Columns[index],
                    Binding = new Binding("[" + index + "]")
                    {
                        TargetNullValue = string.Empty,
                        FallbackValue = string.Empty
                    },
                    ClipboardContentBinding = new Binding("[" + index + "]")
                    {
                        TargetNullValue = string.Empty,
                        FallbackValue = string.Empty
                    },
                    IsReadOnly = true
                });
            }

            gridResult.ItemsSource = result.Rows;
        }

        private static string BuildClipboardText(QueryGridResult result)
        {
            var builder = new StringBuilder();

            for (var i = 0; i < result.Columns.Count; i++)
            {
                if (i > 0)
                    builder.Append('\t');

                builder.Append(EscapeClipboardValue(result.Columns[i]));
            }

            builder.AppendLine();

            foreach (var row in result.Rows)
            {
                for (var i = 0; i < result.Columns.Count; i++)
                {
                    if (i > 0)
                        builder.Append('\t');

                    builder.Append(EscapeClipboardValue(i < row.Length ? row[i] : null));
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static string EscapeClipboardValue(object? value)
        {
            if (value == null || value == DBNull.Value)
                return "";

            var text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            return text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        private static List<SqlBatchDefinition> SplitSqlBatches(string sql)
        {
            var batches = new List<SqlBatchDefinition>();
            var lines = (sql ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var current = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? "";
                var match = Regex.Match(line, @"^\s*GO(?:\s+(\d+))?\s*$", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    var commandText = current.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(commandText))
                    {
                        var repeatCount = 1;
                        if (match.Groups[1].Success)
                            int.TryParse(match.Groups[1].Value, out repeatCount);

                        if (repeatCount <= 0)
                            repeatCount = 1;

                        batches.Add(new SqlBatchDefinition
                        {
                            CommandText = commandText,
                            RepeatCount = repeatCount
                        });
                    }

                    current.Clear();
                }
                else
                {
                    current.AppendLine(line);
                }
            }

            var remaining = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                batches.Add(new SqlBatchDefinition
                {
                    CommandText = remaining,
                    RepeatCount = 1
                });
            }

            if (batches.Count == 0 && !string.IsNullOrWhiteSpace(sql))
            {
                batches.Add(new SqlBatchDefinition
                {
                    CommandText = sql,
                    RepeatCount = 1
                });
            }

            return batches;
        }

        private static void AppendMessage(StringBuilder builder, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (builder.Length > 0)
                builder.AppendLine();

            builder.AppendLine(message.Trim());
        }

        private static List<string> BuildResultColumns(SqlDataReader rd, bool includeDatabaseColumn)
        {
            var columns = new List<string>(rd.FieldCount + (includeDatabaseColumn ? 1 : 0));

            if (includeDatabaseColumn)
                columns.Add("BaseDatos");

            for (var i = 0; i < rd.FieldCount; i++)
                columns.Add(rd.GetName(i));

            return columns;
        }

        private static async Task<int> DrainCurrentResultSetAsync(SqlDataReader rd, CancellationToken cancellationToken)
        {
            var rowsRead = 0;

            while (!rd.IsClosed && await rd.ReadAsync(cancellationToken).ConfigureAwait(false))
                rowsRead++;

            return rowsRead;
        }

        private static bool CanMerge(QueryGridResult combined, QueryGridResult incoming)
        {
            return CanMerge(combined, incoming.Columns);
        }

        private static bool CanMerge(QueryGridResult combined, IReadOnlyList<string> incomingColumns)
        {
            if (combined.Columns.Count != incomingColumns.Count)
                return false;

            for (var i = 0; i < combined.Columns.Count; i++)
            {
                if (!string.Equals(combined.Columns[i], incomingColumns[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        private static void AppendRows(QueryGridResult combined, QueryGridResult source)
        {
            combined.Append(source);
        }

        private void ClearGridResult()
        {
            gridResult.ItemsSource = null;
            gridResult.Columns.Clear();
            _lastCombinedResult = null;
            btnCopyWithHeaders.IsEnabled = false;
        }

        private static string BuildFooterText(QueryGridResult result)
        {
            if (result.IsPreviewTrimmed)
            {
                return result.Rows.Count.ToString("N0", CultureInfo.CurrentCulture) + " de " +
                       result.TotalRowCount.ToString("N0", CultureInfo.CurrentCulture) +
                       " fila(s) cargadas en vista previa.";
            }

            return result.TotalRowCount.ToString("N0", CultureInfo.CurrentCulture) + " fila(s) mostrada(s).";
        }

        private static object? ReadCellPreviewValue(SqlDataReader rd, int ordinal)
        {
            if (rd.IsDBNull(ordinal))
                return null;

            Type fieldType;
            try
            {
                fieldType = rd.GetFieldType(ordinal);
            }
            catch
            {
                fieldType = typeof(object);
            }

            if (fieldType == typeof(string))
                return ReadTextPreview(rd, ordinal);

            if (fieldType == typeof(byte[]))
                return ReadBinaryPreview(rd, ordinal);

            var value = rd.GetValue(ordinal);

            if (value is string textValue)
                return LimitText(textValue);

            if (value is byte[] bytes)
                return BuildBinaryPreview(bytes.LongLength);

            return value;
        }

        private static string ReadTextPreview(SqlDataReader rd, int ordinal)
        {
            try
            {
                var totalChars = rd.GetChars(ordinal, 0, null, 0, 0);
                if (totalChars <= 0)
                    return string.Empty;

                var charsToRead = (int)Math.Min(totalChars, MaxPreviewTextLength);
                var buffer = ArrayPool<char>.Shared.Rent(charsToRead);

                try
                {
                    var charsRead = (int)rd.GetChars(ordinal, 0, buffer, 0, charsToRead);
                    var text = new string(buffer, 0, charsRead);
                    return totalChars > charsRead ? text + "..." : text;
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }
            }
            catch
            {
                return LimitText(Convert.ToString(rd.GetValue(ordinal), CultureInfo.CurrentCulture) ?? "");
            }
        }

        private static string ReadBinaryPreview(SqlDataReader rd, int ordinal)
        {
            try
            {
                var length = rd.GetBytes(ordinal, 0, null, 0, 0);
                return BuildBinaryPreview(length);
            }
            catch
            {
                var value = rd.GetValue(ordinal);
                return value is byte[] bytes
                    ? BuildBinaryPreview(bytes.LongLength)
                    : "<BLOB>";
            }
        }

        private static string BuildBinaryPreview(long length)
        {
            return "<BLOB " + length.ToString("N0", CultureInfo.CurrentCulture) + " bytes>";
        }

        private static string LimitText(string value)
        {
            if (value.Length <= MaxPreviewTextLength)
                return value;

            return value.Substring(0, MaxPreviewTextLength) + "...";
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class DatabaseSelectionItem : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isSelected;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class QueryExecutionResult
    {
        public bool Success { get; set; }
        public bool WasCanceled { get; set; }
        public QueryGridResult? GridResult { get; set; }
        public string TextResult { get; set; } = "";


    }

    public class QueryGridResult
    {
        private readonly int _previewRowLimit;
        private readonly int _previewCellBudget;
        private int _previewCellCount;

        public QueryGridResult(IReadOnlyList<string> columns, int previewRowLimit, int previewCellBudget)
        {
            Columns = columns.ToList();
            _previewRowLimit = Math.Max(previewRowLimit, 1);
            _previewCellBudget = Math.Max(previewCellBudget, Columns.Count);
        }

        public List<string> Columns { get; }
        public List<object?[]> Rows { get; } = new List<object?[]>();
        public long TotalRowCount { get; private set; }
        public bool IsPreviewTrimmed { get; private set; }

        public void AddRow(object?[] values)
        {
            TotalRowCount++;
            TryStorePreviewRow(values);
        }

        public void Append(QueryGridResult source)
        {
            foreach (var row in source.Rows)
            {
                if (!TryStorePreviewRow(row))
                    break;
            }

            TotalRowCount += source.TotalRowCount;

            if (source.IsPreviewTrimmed || Rows.Count < TotalRowCount)
                IsPreviewTrimmed = true;
        }

        private bool TryStorePreviewRow(object?[] values)
        {
            if (Rows.Count >= _previewRowLimit)
            {
                IsPreviewTrimmed = true;
                return false;
            }

            if (_previewCellCount + values.Length > _previewCellBudget)
            {
                IsPreviewTrimmed = true;
                return false;
            }

            Rows.Add(values);
            _previewCellCount += values.Length;

            return true;
        }
    }

    public class SqlBatchDefinition
    {
        public string CommandText { get; set; } = "";
        public int RepeatCount { get; set; } = 1;
    }

    public class ThirdPartSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return 24.0;

            double size;
            try
            {
                size = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 24.0;
            }

            var result = size / 3.0;
            if (result < 24.0)
                result = 24.0;

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
