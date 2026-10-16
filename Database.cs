using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints
{
    public class Database(string dbConnectionString)
    {
        // Key set on exceptions whose connect failure was already reported here, so callers
        // (e.g. the repeating web refresh poll) can skip logging the same error again.
        public const string ConnectFailureReportedKey = "WeaponPaints.ConnectFailureReported";

        // While the database stays unreachable, repeat the error at most this often.
        private static readonly TimeSpan ConnectErrorLogInterval = TimeSpan.FromSeconds(60);

        private readonly object _errorLock = new();
        private string? _lastConnectError;
        private DateTime _lastConnectErrorLogged;
        private int _suppressedConnectErrors;

        // Expose connection string for synchronous access from ThreadPool if needed
        public string ConnectionString => dbConnectionString;

        public async Task<MySqlConnection> GetConnectionAsync()
        {
            try
            {
                var connection = new MySqlConnection(dbConnectionString);
                await connection.OpenAsync();
                ReportConnectSuccess();
                return connection;
            }
            catch (Exception ex)
            {
                ReportConnectFailure(ex);
                ex.Data[ConnectFailureReportedKey] = true;
                throw;
            }
        }

        public static bool IsReportedConnectFailure(Exception ex) => ex.Data.Contains(ConnectFailureReportedKey);

        private void ReportConnectFailure(Exception ex)
        {
            int suppressed;
            lock (_errorLock)
            {
                var now = DateTime.UtcNow;
                if (ex.Message == _lastConnectError && now - _lastConnectErrorLogged < ConnectErrorLogInterval)
                {
                    _suppressedConnectErrors++;
                    return;
                }

                suppressed = ex.Message == _lastConnectError ? _suppressedConnectErrors : 0;
                _lastConnectError = ex.Message;
                _lastConnectErrorLogged = now;
                _suppressedConnectErrors = 0;
            }

            if (suppressed > 0)
                WeaponPaints.Instance.Logger.LogError("Unable to connect to database: {Message} ({Count} repeats suppressed)", ex.Message, suppressed);
            else
                WeaponPaints.Instance.Logger.LogError("Unable to connect to database: {Message}", ex.Message);
        }

        private void ReportConnectSuccess()
        {
            lock (_errorLock)
            {
                if (_lastConnectError == null)
                    return;
                _lastConnectError = null;
                _suppressedConnectErrors = 0;
            }

            WeaponPaints.Instance.Logger.LogInformation("Database connection restored");
        }
    }
}
