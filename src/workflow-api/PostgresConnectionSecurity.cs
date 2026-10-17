using Npgsql;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Every PostgreSQL connection this service opens is encrypted (INF-27).
///
/// Cloud SQL enforces the same thing from its side (<c>ssl_mode = "ENCRYPTED_ONLY"</c>), and has no
/// public address; this is the client half, so a connection string that forgot the setting, or
/// asked for plaintext, cannot quietly connect without TLS. Npgsql's default (<c>Prefer</c>) would
/// fall back to plaintext, so an unset or <c>Prefer</c> mode becomes <c>Require</c>. An explicit
/// <c>Disable</c> or <c>Allow</c> is a request for plaintext and stops the service at startup.
/// <c>Require</c>, <c>VerifyCA</c> and <c>VerifyFull</c> are kept as given.
/// </summary>
public static class PostgresConnectionSecurity
{
    public static string RequireEncryption(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException error)
        {
            // The message is not passed on: it can quote the connection string, password included.
            throw new InvalidOperationException("The PostgreSQL connection string could not be parsed.", error.InnerException);
        }

        switch (builder.SslMode)
        {
            case SslMode.Disable:
            case SslMode.Allow:
                throw new InvalidOperationException(
                    $"The PostgreSQL connection string sets SslMode={builder.SslMode}; use Require, VerifyCA or VerifyFull.");
            case SslMode.Prefer:
                builder.SslMode = SslMode.Require;
                break;
        }

        return builder.ConnectionString;
    }
}
