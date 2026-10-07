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

    /// <summary>The longest stretch between two records which counts as time on a map, in minutes.</summary>
    private const double MaxGapMinutes = 2;

    /// <summary>The minutes a map must have been measured for before it is compared.</summary>
    private const double MinSampleMinutes = 10;

    /// <summary>How much better (experience per minute) a proven map must be to move there.</summary>
    private const double ProvenMargin = 1.25;

    /// <summary>The deaths on a map from which on it can count as deadly (with <see cref="DeadlyDeathsPerHour"/>).</summary>
    private const int DeadlyDeaths = 3;

    /// <summary>The deaths per hour on a map which make it deadly (seen: 33 in 1 h 46 min on Lost Tower).</summary>
    private const double DeadlyDeathsPerHour = 2;

    /// <summary>
    /// How far ahead a map's measured zen loss is projected (seen: test300Dl in Devias, 22 K earned and
    /// 1.58 M spent on 4 repairs in 10 minutes).
    /// </summary>
    private const double SustainMinutes = 60;

    private string? _clockMap;

    private DateTime _clockUtc;

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
            lock (this.MapRates)
            {
                return JsonSerializer.Serialize(this, JsonOptions);
            }
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

    /// <summary>
    /// Gets or sets what each map earned in this session: the summary's breakdown, and what the map
    /// choice measures maps by (<see cref="BetterProvenMap"/>).
    /// </summary>
    public List<MapRate> MapRates { get; set; } = [];

    /// <summary>
    /// Counts the time since the last record towards the map the character was on, then adds what it
    /// just earned on <paramref name="map"/>. A gap longer than <see cref="MaxGapMinutes"/> (a pause, a
    /// restart) counts as that much.
    /// </summary>
    /// <param name="map">The map the character is on now.</param>
    /// <param name="experience">Experience (normal and master) just earned.</param>
    /// <param name="zen">Zen just earned.</param>
    /// <param name="kills">Kills just made.</param>
    /// <param name="deaths">Deaths just suffered.</param>
    /// <param name="at">The time of the record; now when <c>null</c> (tests pass their own).</param>
    /// <param name="spent">Zen just spent (repairs, potions, the MU Helper fee, warps).</param>
    public void Record(string? map, long experience = 0, long zen = 0, long kills = 0, long deaths = 0, DateTime? at = null, long spent = 0)
    {
        lock (this.MapRates)
        {
            var now = at ?? DateTime.UtcNow;
            if (this._clockMap is { } previous)
            {
                this.RateOf(previous).Minutes += Math.Min((now - this._clockUtc).TotalMinutes, MaxGapMinutes);
            }

            this._clockMap = map;
            this._clockUtc = now;
            if (map is null)
            {
                return;
            }

            var rate = this.RateOf(map);
            rate.Experience += experience;
            rate.Zen += zen;
            rate.Spent += spent;
            rate.Kills += kills;
            rate.Deaths += deaths;
        }
    }

    /// <summary>
    /// Whether hunting on the map drains the character (D20): measured over at least
    /// <see cref="MinSampleMinutes"/>, it spends more zen there than it earns (repairs of high-end gear
    /// cost millions), and at that rate the money would fall below the reserve within
    /// <see cref="SustainMinutes"/>.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="money">The character's zen now.</param>
    /// <param name="reserve">The zen HUNT keeps (the MU Helper fee reserve).</param>
    /// <returns><c>true</c>, if so.</returns>
    public bool IsUnsustainable(string? map, long money, long reserve)
    {
        lock (this.MapRates)
        {
            if (this.MapRates.FirstOrDefault(r => r.Map == map) is not { Minutes: >= MinSampleMinutes } rate)
            {
                return false;
            }

            var netPerMinute = (rate.Zen - rate.Spent) / rate.Minutes;
            return netPerMinute < 0 && money + (netPerMinute * SustainMinutes) < reserve;
        }
    }

    /// <summary>
    /// Whether the character dies too often on the map this session (at least <see cref="DeadlyDeaths"/>,
    /// at <see cref="DeadlyDeathsPerHour"/> or more): repairs, potions and lost experience eat what it earns.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <returns><c>true</c>, if so.</returns>
    public bool IsDeadly(string? map)
    {
        lock (this.MapRates)
        {
            return this.MapRates.FirstOrDefault(r => r.Map == map) is { } rate
                   && rate.Deaths >= DeadlyDeaths
                   && rate.Deaths * 60 / Math.Max(rate.Minutes, 1) >= DeadlyDeathsPerHour;
        }
    }

    /// <summary>
    /// Whether the map earned less Zen per minute this session (over at least <see cref="MinSampleMinutes"/>)
    /// than the running costs per minute take.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="costPerMinute">The running costs per minute (the MU Helper fee).</param>
    /// <returns><c>true</c>, if so.</returns>
    public bool IsLosing(string? map, double costPerMinute)
    {
        lock (this.MapRates)
        {
            return costPerMinute > 0
                   && this.MapRates.FirstOrDefault(r => r.Map == map) is { Minutes: >= MinSampleMinutes } rate
                   && rate.Zen / rate.Minutes < costPerMinute;
        }
    }

    /// <summary>
    /// Gets the visited map which has proven clearly better than the current one: experience per
    /// minute at least <see cref="ProvenMargin"/> times the current map's, both measured over at least
    /// <see cref="MinSampleMinutes"/>. The margin keeps the hunt from bouncing between two similar maps.
    /// </summary>
    /// <param name="currentMap">The map the character is on.</param>
    /// <returns>The better map's name, or <c>null</c>.</returns>
    public string? BetterProvenMap(string? currentMap)
    {
        lock (this.MapRates)
        {
            var current = this.MapRates.FirstOrDefault(r => r.Map == currentMap);
            if (current is null || current.Minutes < MinSampleMinutes)
            {
                return null;
            }

            // A map it keeps dying on has proven nothing but its cost.
            var best = this.MapRates
                .Where(r => r.Map != currentMap && r.Minutes >= MinSampleMinutes)
                .Where(r => !(r.Deaths >= DeadlyDeaths && r.Deaths * 60 / Math.Max(r.Minutes, 1) >= DeadlyDeathsPerHour))
                .MaxBy(r => r.ExperiencePerMinute);
            return best is not null && best.ExperiencePerMinute >= current.ExperiencePerMinute * ProvenMargin
                ? best.Map
                : null;
        }
    }

    private MapRate RateOf(string map)
    {
        var rate = this.MapRates.FirstOrDefault(r => r.Map == map);
        if (rate is null)
        {
            rate = new MapRate { Map = map };
            this.MapRates.Add(rate);
        }

        return rate;
    }
}

/// <summary>
/// What one map earned during an offline session.
/// </summary>
public sealed class MapRate
{
    /// <summary>Gets or sets the map name.</summary>
    public string Map { get; set; } = string.Empty;

    /// <summary>Gets or sets the minutes spent there.</summary>
    public double Minutes { get; set; }

    /// <summary>Gets or sets the experience (normal and master) earned there.</summary>
    public long Experience { get; set; }

    /// <summary>Gets or sets the zen earned there (pickups and sales; spending is not subtracted).</summary>
    public long Zen { get; set; }

    /// <summary>Gets or sets the zen spent there (repairs, potions, the MU Helper fee, warps).</summary>
    public long Spent { get; set; }

    /// <summary>Gets or sets the kills made there.</summary>
    public long Kills { get; set; }

    /// <summary>Gets or sets the deaths suffered there.</summary>
    public long Deaths { get; set; }

    /// <summary>Gets the experience per minute.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ExperiencePerMinute => this.Minutes > 0 ? this.Experience / this.Minutes : 0;
}
