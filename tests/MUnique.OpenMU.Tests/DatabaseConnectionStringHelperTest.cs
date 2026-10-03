// <copyright file="DatabaseConnectionStringHelperTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.Persistence.EntityFramework;

/// <summary>
/// Tests the per-role password variables (<c>DB_PW_&lt;ROLE&gt;</c>) of <see cref="DatabaseConnectionStringHelper"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
public class DatabaseConnectionStringHelperTest
{
    private const string AccountConnection = "Server=127.0.0.1;Port=5432;User Id=account;Password=old;Database=openmu;Command Timeout=120;";

    /// <summary>Clears the variables the tests set.</summary>
    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("DB_PW_ACCOUNT", null);
        Environment.SetEnvironmentVariable("DB_PW_CONFIG", null);
    }

    /// <summary>Without a variable the connection string stays as configured.</summary>
    [Test]
    public void UnsetVariableKeepsThePassword()
    {
        Assert.That(DatabaseConnectionStringHelper.ApplyEnvironmentVariables(AccountConnection), Is.EqualTo(AccountConnection));
    }

    /// <summary>The variable named after the role replaces its password, and only its password.</summary>
    [Test]
    public void RoleVariableReplacesThePassword()
    {
        Environment.SetEnvironmentVariable("DB_PW_ACCOUNT", "n3w$1");
        Environment.SetEnvironmentVariable("DB_PW_CONFIG", "other");

        Assert.That(
            DatabaseConnectionStringHelper.ApplyEnvironmentVariables(AccountConnection),
            Is.EqualTo("Server=127.0.0.1;Port=5432;User Id=account;Password=n3w$1;Database=openmu;Command Timeout=120;"));
    }

    /// <summary>The admin connection keeps its own variables (DB_ADMIN_USER / DB_ADMIN_PW).</summary>
    [Test]
    public void AdminConnectionIgnoresRoleVariables()
    {
        Environment.SetEnvironmentVariable("DB_PW_ACCOUNT", "n3w");
        const string admin = "Server=127.0.0.1;Port=5432;User Id=postgres;Password=admin;Database=openmu;";

        Assert.That(DatabaseConnectionStringHelper.ApplyEnvironmentVariables(admin), Is.EqualTo(admin));
    }
}
