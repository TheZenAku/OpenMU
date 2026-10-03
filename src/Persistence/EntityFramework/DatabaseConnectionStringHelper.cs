// <copyright file="DatabaseConnectionStringHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Text.RegularExpressions;

/// <summary>
/// Shared helper to apply the <c>DB_HOST</c>, <c>DB_ADMIN_USER</c> and <c>DB_ADMIN_PW</c>
/// environment variables to a connection string.
/// When the variables are not set (or empty), the connection string is left unchanged,
/// so the default password <c>admin</c> keeps working and existing databases are unaffected.
/// </summary>
public static partial class DatabaseConnectionStringHelper
{
    /// <summary>
    /// Gets the environment variable name for the database host.
    /// </summary>
    public static string DbHostVariableName { get; } = "DB_HOST";
    /// <summary>
    /// Gets the environment variable name for the admin user.
    /// </summary>
    public static string DbAdminUserVariableName { get; } = "DB_ADMIN_USER";
    /// <summary>
    /// Gets the environment variable name for the admin password.
    /// </summary>
    public static string DbAdminPasswordVariableName { get; } = "DB_ADMIN_PW";

    /// <summary>
    /// Gets the prefix of the environment variables which override the password of the other roles,
    /// by their upper-case name: <c>DB_PW_ACCOUNT</c>, <c>DB_PW_CONFIG</c>, <c>DB_PW_FRIEND</c>, <c>DB_PW_GUILD</c>.
    /// A rotated password then lives outside the (versioned) ConnectionSettings.xml.
    /// </summary>
    public static string DbRolePasswordVariablePrefix { get; } = "DB_PW_";

    /// <summary>
    /// Applies the environment variables to the connection string of the specified setting, in place.
    /// </summary>
    /// <param name="setting">The setting to adjust.</param>
    public static void ApplyEnvironmentVariables(ConnectionSetting setting)
    {
        if (setting.ConnectionString is null)
        {
            return;
        }

        setting.ConnectionString = ApplyEnvironmentVariables(setting.ConnectionString);
    }

    /// <summary>
    /// Applies the environment variables to the specified connection string.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The adjusted connection string.</returns>
    public static string ApplyEnvironmentVariables(string connectionString)
    {
        if (Environment.GetEnvironmentVariable(DbHostVariableName) is { } dbHost
            && !string.IsNullOrEmpty(dbHost))
        {
            connectionString = ServerRegex().Replace(connectionString, $"Server={dbHost};");
        }

        if (connectionString.Contains("User Id=postgres;", StringComparison.Ordinal))
        {
            if (Environment.GetEnvironmentVariable(DbAdminUserVariableName) is { } dbAdminUser
                && !string.IsNullOrEmpty(dbAdminUser))
            {
                connectionString = connectionString.Replace("User Id=postgres;", $"User Id={dbAdminUser};", StringComparison.Ordinal);
            }

            if (Environment.GetEnvironmentVariable(DbAdminPasswordVariableName) is { } dbAdminPassword
                && !string.IsNullOrEmpty(dbAdminPassword))
            {
                connectionString = connectionString.Replace("Password=admin;", $"Password={dbAdminPassword};", StringComparison.Ordinal);
            }
        }
        else if (UserRegex().Match(connectionString) is { Success: true } user
                 && Environment.GetEnvironmentVariable(DbRolePasswordVariablePrefix + user.Groups[1].Value.ToUpperInvariant()) is { Length: > 0 } rolePassword)
        {
            // An evaluator, so a '$' in the password is not read as a substitution.
            connectionString = PasswordRegex().Replace(connectionString, _ => $"Password={rolePassword};");
        }

        return connectionString;
    }

    [GeneratedRegex("Server=[^;]+;")]
    private static partial Regex ServerRegex();

    [GeneratedRegex("User Id=([^;]+?);")]
    private static partial Regex UserRegex();

    [GeneratedRegex("Password=[^;]*;")]
    private static partial Regex PasswordRegex();
}
