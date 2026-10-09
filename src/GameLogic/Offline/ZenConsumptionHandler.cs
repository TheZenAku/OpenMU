// <copyright file="ZenConsumptionHandler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Offline;

using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Views.MuHelper;

/// <summary>
/// Handles the periodic Zen consumption for the offline player.
/// </summary>
internal sealed class ZenConsumptionHandler
{
    private readonly Player _player;

    private readonly IHunter _hunter;
    private readonly MuHelperConfiguration _configuration;
    private DateTime _lastPayTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZenConsumptionHandler"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public ZenConsumptionHandler(IHunter hunter)
    {
        this._player = hunter.Player;
        this._hunter = hunter;
        this._configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>()?.Configuration
                              ?? new MuHelperConfiguration();
        this._lastPayTimestamp = hunter.StartTimestamp;
    }

    /// <summary>
    /// Deducts Zen from the player based on the server configuration and the player's level.
    /// </summary>
    /// <returns><c>true</c> if the player can continue; <c>false</c> if insufficient Zen.</returns>
    public async ValueTask<bool> DeductZenAsync()
    {
        // Bots are exempt from the PC-Cafe fee: they don't accumulate Zen fast enough
        // to cover it and would otherwise go bankrupt and stop. Human offline-leveling
        // players (IsBot == false) keep paying as before.
        if (this._player.Account?.IsBot == true)
        {
            return true;
        }

        if (DateTime.UtcNow - this._lastPayTimestamp < this._configuration.PayInterval)
        {
            return true;
        }

        var amount = MuHelperZenCostCalculator.Calculate(this._player, this._configuration, this._hunter.StartTimestamp);

        if (amount > 0 && this._player.TryRemoveMoney(amount))
        {
            this._lastPayTimestamp = DateTime.UtcNow;
            await this._player
                .InvokeViewPlugInAsync<IMuHelperStatusUpdatePlugIn>(p => p.ConsumeMoneyAsync((uint)amount))
                .ConfigureAwait(false);
            return true;
        }

        if (amount > 0)
        {
            this._player.Logger.LogDebug("Insufficient Zen for {CharacterName}.", this._player.Name);
            return false;
        }

        return true;
    }
}