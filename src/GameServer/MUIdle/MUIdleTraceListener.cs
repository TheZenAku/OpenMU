// <copyright file="MUIdleTraceListener.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Diagnostics;

/// <summary>
/// Reports failed debug assertions instead of ending the process. The server runs a Debug build, where
/// <see cref="Debug.Fail(string?, string?)"/> - the upstream catch-all of timers, monster AI and periodic
/// tasks - ends the process through the <see cref="DefaultTraceListener"/>: one plug-in's database
/// timeout stopped the whole game server (2026-10-03 23:29).
/// </summary>
public sealed class MUIdleTraceListener : TraceListener
{
    private readonly Action<string?, string?> _report;

    /// <summary>
    /// Initializes a new instance of the <see cref="MUIdleTraceListener"/> class.
    /// </summary>
    /// <param name="report">Reports a failed assertion: the message and the detail (a stack trace).</param>
    public MUIdleTraceListener(Action<string?, string?> report)
    {
        this._report = report;
    }

    /// <summary>
    /// Replaces the default trace listener (which ends the process on a failed assertion) by one that
    /// reports it.
    /// </summary>
    /// <param name="report">Reports a failed assertion: the message and the detail.</param>
    /// <returns>The installed listener; <see cref="Uninstall"/> puts the default one back.</returns>
    public static MUIdleTraceListener Install(Action<string?, string?> report)
    {
        var listener = new MUIdleTraceListener(report);
        foreach (var defaultListener in Trace.Listeners.OfType<DefaultTraceListener>().ToList())
        {
            Trace.Listeners.Remove(defaultListener);
        }

        Trace.Listeners.Add(listener);
        return listener;
    }

    /// <summary>
    /// Removes the listener and puts a default trace listener back.
    /// </summary>
    public void Uninstall()
    {
        Trace.Listeners.Remove(this);
        if (!Trace.Listeners.OfType<DefaultTraceListener>().Any())
        {
            Trace.Listeners.Add(new DefaultTraceListener());
        }
    }

    /// <inheritdoc />
    public override void Fail(string? message, string? detailMessage) => this._report(message, detailMessage);

    /// <inheritdoc />
    public override void Write(string? message)
    {
        // Debug output is not logged (it went to the debugger only before).
    }

    /// <inheritdoc />
    public override void WriteLine(string? message)
    {
        // Debug output is not logged (it went to the debugger only before).
    }
}
