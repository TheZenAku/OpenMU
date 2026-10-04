// <copyright file="MUIdleTraceListenerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Diagnostics;
using MUnique.OpenMU.GameServer.MUIdle;

/// <summary>
/// Tests that a failed debug assertion is reported instead of ending the process (<see cref="MUIdleTraceListener"/>).
/// </summary>
[TestFixture]
public class MUIdleTraceListenerTest
{
    /// <summary>
    /// <see cref="Debug.Fail(string?, string?)"/>, as in the upstream catch-all blocks, reaches the report
    /// and the process goes on (with the default listener a Debug build ends here).
    /// </summary>
    [Test]
    public void AFailedAssertionIsReported()
    {
        var reports = new List<(string? Message, string? Detail)>();
        var listener = MUIdleTraceListener.Install((message, detail) => reports.Add((message, detail)));
        try
        {
            Debug.Fail("database timeout", "at a periodic task");
        }
        finally
        {
            listener.Uninstall();
        }

        Assert.That(reports, Is.EqualTo(new List<(string?, string?)> { ("database timeout", "at a periodic task") }));
    }
}
