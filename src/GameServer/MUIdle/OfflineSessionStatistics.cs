// <copyright file="OfflineSessionStatistics.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MUIdle;

using System.Text.Json;
using System.Threading;

/// <summary>
/// What happened during an offline hunting session. Collected on the server from the game events of
/// the offline player (never computed by the client), and stored as JSON in
/// <see cref="MUIdleCharacterState.OfflineStatisticsJson"/> until the player saw it.
/// </summary>
public sealed class OfflineSessionStatistics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private long _monstersKilled;
    private long _experience;
    private long _masterExperience;
    private long _zenEarned;
    private long _zenSpent;
    private long _itemsCollected;
    private long _itemsSold;
    private long _repairs;
    private long _potionsUsed;
    private long _deaths;

    /// <summary>Gets or sets the start of the session (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets the end of the session (UTC); null while it's running.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Gets or sets why the session ended (e.g. "returned", "died", "allowance", "zen", "server-stop").</summary>
    public string? EndReason { get; set; }

    /// <summary>Gets or sets the character level at the start.</summary>
    public int StartLevel { get; set; }

    /// <summary>Gets or sets the character level at the end (or the last save).</summary>
    public int EndLevel { get; set; }

    /// <summary>Gets or sets the master level at the start.</summary>
    public int StartMasterLevel { get; set; }

    /// <summary>Gets or sets the master level at the end (or the last save).</summary>
    public int EndMasterLevel { get; set; }

    /// <summary>Gets or sets the zen at the start.</summary>
    public long StartMoney { get; set; }

    /// <summary>Gets or sets the zen at the end (or the last save).</summary>
    public long EndMoney { get; set; }

    /// <summary>Gets or sets the number of killed monsters.</summary>
    public long MonstersKilled { get => Interlocked.Read(ref this._monstersKilled); set => this._monstersKilled = value; }

    /// <summary>Gets or sets the gained (normal) experience.</summary>
    public long Experience { get => Interlocked.Read(ref this._experience); set => this._experience = value; }

    /// <summary>Gets or sets the gained master experience.</summary>
    public long MasterExperience { get => Interlocked.Read(ref this._masterExperience); set => this._masterExperience = value; }

    /// <summary>Gets or sets the zen which was earned (picked up or received for sold items).</summary>
    public long ZenEarned { get => Interlocked.Read(ref this._zenEarned); set => this._zenEarned = value; }

    /// <summary>Gets or sets the zen which was spent (repairs, potions, warps, helper costs).</summary>
    public long ZenSpent { get => Interlocked.Read(ref this._zenSpent); set => this._zenSpent = value; }

    /// <summary>Gets or sets the number of picked up items.</summary>
    public long ItemsCollected { get => Interlocked.Read(ref this._itemsCollected); set => this._itemsCollected = value; }

    /// <summary>Gets or sets the number of items which were sold to merchants.</summary>
    public long ItemsSold { get => Interlocked.Read(ref this._itemsSold); set => this._itemsSold = value; }

    /// <summary>Gets or sets the number of repaired items.</summary>
    public long Repairs { get => Interlocked.Read(ref this._repairs); set => this._repairs = value; }

    /// <summary>Gets or sets the number of used potions.</summary>
    public long PotionsUsed { get => Interlocked.Read(ref this._potionsUsed); set => this._potionsUsed = value; }

    /// <summary>Gets or sets the number of deaths.</summary>
    public long Deaths { get => Interlocked.Read(ref this._deaths); set => this._deaths = value; }

    /// <summary>Gets or sets the names of the visited maps.</summary>
    public List<string> MapsVisited { get; set; } = [];

    /// <summary>
    /// Parses the statistics from JSON.
    /// </summary>
    /// <param name="json">The JSON.</param>
    /// <returns>The statistics; null, if there are none.</returns>
    public static OfflineSessionStatistics? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OfflineSessionStatistics>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes the statistics to JSON.
    /// </summary>
    /// <returns>The JSON.</returns>
    public string ToJson()
    {
        lock (this.MapsVisited)
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }
    }

    /// <summary>Counts a killed monster.</summary>
    public void AddKill() => Interlocked.Increment(ref this._monstersKilled);

    /// <summary>Adds gained experience.</summary>
    /// <param name="amount">The amount.</param>
    /// <param name="master">If set to <c>true</c>, it's master experience.</param>
    public void AddExperience(long amount, bool master)
    {
        if (master)
        {
            Interlocked.Add(ref this._masterExperience, amount);
        }
        else
        {
            Interlocked.Add(ref this._experience, amount);
        }
    }

    /// <summary>Adds a change of the zen.</summary>
    /// <param name="delta">The change; positive for earned, negative for spent zen.</param>
    public void AddZenDelta(long delta)
    {
        if (delta > 0)
        {
            Interlocked.Add(ref this._zenEarned, delta);
        }
        else if (delta < 0)
        {
            Interlocked.Add(ref this._zenSpent, -delta);
        }
    }

    /// <summary>Counts a collected item.</summary>
    public void AddCollectedItem() => Interlocked.Increment(ref this._itemsCollected);

    /// <summary>Takes back an item which appeared in the backpack by a purchase, not by a pickup.</summary>
    public void RemoveCollectedItem() => Interlocked.Decrement(ref this._itemsCollected);

    /// <summary>Counts a sold item.</summary>
    public void AddSoldItem() => Interlocked.Increment(ref this._itemsSold);

    /// <summary>Counts a repaired item.</summary>
    public void AddRepair() => Interlocked.Increment(ref this._repairs);

    /// <summary>Counts a used potion.</summary>
    public void AddPotion() => Interlocked.Increment(ref this._potionsUsed);

    /// <summary>Counts a death.</summary>
    public void AddDeath() => Interlocked.Increment(ref this._deaths);

    /// <summary>Records a visited map.</summary>
    /// <param name="mapName">The name of the map.</param>
    public void AddMap(string mapName)
    {
        lock (this.MapsVisited)
        {
            if (!this.MapsVisited.Contains(mapName))
            {
                this.MapsVisited.Add(mapName);
            }
        }
    }
}
