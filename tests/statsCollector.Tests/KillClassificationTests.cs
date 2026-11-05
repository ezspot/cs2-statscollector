using System.Numerics;
using statsCollector.Domain;
using statsCollector.Services;
using Xunit;

namespace statsCollector.Tests;

/// <summary>How a death is scored with bots in the game: as the engine's PlayerKilled scores it.</summary>
public class KillClassificationTests
{
    private static PlayerControllerState Player(PlayerTeam team, bool isBot = false, bool isValid = true) => new()
    {
        SteamId = isBot || !isValid ? 0UL : 76561198000000001UL,
        PlayerName = isBot ? "Bot" : "Human",
        Health = 100,
        Team = team,
        Position = Vector3.Zero,
        GrenadeThrowPosition = Vector3.Zero,
        Money = 0,
        EquipmentValue = 0,
        IsValid = isValid,
        IsBot = isBot,
        HasPawn = isValid
    };

    private static readonly PlayerControllerState Nobody = Player(PlayerTeam.Spectator, isValid: false);

    [Fact]
    public void A_bot_killing_a_human_is_an_enemy_kill_and_the_human_died()
    {
        // Arrange
        var bot = Player(PlayerTeam.Terrorist, isBot: true);
        var human = Player(PlayerTeam.CounterTerrorist);

        // Act
        var (suicide, teamKill, enemyKill) = CombatEventProcessor.ClassifyKill(bot, human, isSelfKill: false, killedByBomb: false);

        // Assert
        Assert.False(suicide);
        Assert.False(teamKill);
        Assert.True(enemyKill);
    }

    [Fact]
    public void A_human_killing_a_bot_is_an_enemy_kill()
    {
        var (suicide, teamKill, enemyKill) = CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.CounterTerrorist), Player(PlayerTeam.Terrorist, isBot: true), isSelfKill: false, killedByBomb: false);

        Assert.False(suicide);
        Assert.False(teamKill);
        Assert.True(enemyKill);
    }

    [Fact]
    public void Two_bots_make_an_enemy_kill_for_the_round_state()
    {
        var (_, _, enemyKill) = CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.Terrorist, isBot: true), Player(PlayerTeam.CounterTerrorist, isBot: true), isSelfKill: false, killedByBomb: false);

        Assert.True(enemyKill);
    }

    [Fact]
    public void No_scorer_or_the_victim_as_scorer_is_a_suicide()
    {
        var victim = Player(PlayerTeam.Terrorist);

        Assert.Equal((true, false, false), CombatEventProcessor.ClassifyKill(Nobody, victim, isSelfKill: false, killedByBomb: false));
        Assert.Equal((true, false, false), CombatEventProcessor.ClassifyKill(victim, victim, isSelfKill: true, killedByBomb: false));
    }

    [Fact]
    public void The_bomb_scores_nothing_even_with_the_planter_as_attacker()
    {
        Assert.Equal((false, false, false), CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.Terrorist), Player(PlayerTeam.CounterTerrorist), isSelfKill: false, killedByBomb: true));
        Assert.Equal((false, false, false), CombatEventProcessor.ClassifyKill(
            Nobody, Player(PlayerTeam.CounterTerrorist), isSelfKill: false, killedByBomb: true));
    }

    [Fact]
    public void A_human_killing_a_bot_teammate_is_a_team_kill_but_a_bot_gets_no_team_kill()
    {
        Assert.Equal((false, true, false), CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.Terrorist), Player(PlayerTeam.Terrorist, isBot: true), isSelfKill: false, killedByBomb: false));
        Assert.Equal((false, false, false), CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.Terrorist, isBot: true), Player(PlayerTeam.Terrorist), isSelfKill: false, killedByBomb: false));
    }

    [Fact]
    public void A_spectator_scorer_makes_no_enemy_kill()
    {
        var (suicide, teamKill, enemyKill) = CombatEventProcessor.ClassifyKill(
            Player(PlayerTeam.Spectator), Player(PlayerTeam.Terrorist), isSelfKill: false, killedByBomb: false);

        Assert.False(suicide);
        Assert.False(teamKill);
        Assert.False(enemyKill);
    }
}
