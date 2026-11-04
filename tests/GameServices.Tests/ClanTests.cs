using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;

namespace GameServices.Tests;

/// <summary>
/// THE SIX CLANS: every monster belongs to one, every clan has the same number of wins and losses against the others, and
/// the edge is real without being the whole fight.
/// </summary>
public class ClanTests
{
    private static readonly MonsterCatalog Catalog = MonsterCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Walking", "monster-catalog.json"));

    [Fact]
    public void Every_clan_beats_two_loses_to_two_and_is_level_with_one()
    {
        foreach (Clan clan in Clans.All)
        {
            List<ClanMatchup> against = Clans.All.Where(c => c != clan).Select(c => Clans.Matchup(clan, c)).ToList();
            Assert.Equal(2, against.Count(m => m == ClanMatchup.Strong));
            Assert.Equal(2, against.Count(m => m == ClanMatchup.Weak));
            Assert.Equal(1, against.Count(m => m is ClanMatchup.Even or ClanMatchup.Rivals));
            Assert.Equal(ClanMatchup.Even, Clans.Matchup(clan, clan));
        }
    }

    [Fact]
    public void A_matchup_looks_the_same_from_both_sides()
    {
        foreach (Clan a in Clans.All)
        {
            foreach (Clan b in Clans.All)
            {
                ClanMatchup there = Clans.Matchup(a, b), back = Clans.Matchup(b, a);
                if (there == ClanMatchup.Strong) Assert.Equal(ClanMatchup.Weak, back);
                else if (there == ClanMatchup.Weak) Assert.Equal(ClanMatchup.Strong, back);
                else Assert.Equal(there, back);
            }
        }
    }

    [Theory]
    [InlineData(Clan.Fire, Clan.Wind)]
    [InlineData(Clan.Water, Clan.Fire)]
    [InlineData(Clan.Earth, Clan.Water)]
    [InlineData(Clan.Wind, Clan.Earth)]
    public void The_ones_everyone_expects(Clan winner, Clan loser)
    {
        Assert.True(Clans.Beats(winner, loser));
        Assert.False(Clans.Beats(loser, winner));
    }

    [Fact]
    public void Light_and_Darkness_are_rivals()
    {
        Assert.Equal(ClanMatchup.Rivals, Clans.Matchup(Clan.Light, Clan.Darkness));
        Assert.Equal(ClanMatchup.Rivals, Clans.Matchup(Clan.Darkness, Clan.Light));
    }

    [Fact]
    public void Every_species_has_a_home_clan_and_every_clan_has_species()
    {
        Assert.All(Catalog.Species, s => Assert.True(Enum.TryParse(s.Element, out Clan _), s.Id + " is " + s.Element));
        foreach (Clan clan in Clans.All) Assert.Contains(Catalog.Species, s => s.Wild && s.Element == clan.ToString());
    }

    [Fact]
    public void Most_monsters_are_their_home_clan_and_the_rest_are_shared_evenly()
    {
        var counts = Clans.All.ToDictionary(c => c, _ => 0);
        const int monsters = 60000;
        for (long seed = 0; seed < monsters; seed++) counts[Clans.Of("Fire", seed * 7919)]++;

        Assert.InRange(counts[Clan.Fire], monsters * 0.53, monsters * 0.57);
        foreach (Clan other in Clans.All.Where(c => c != Clan.Fire)) Assert.InRange(counts[other], monsters * 0.08, monsters * 0.10);
    }

    [Theory]
    [InlineData("Fire", 42L)]
    [InlineData("Earth", 123456789L)]
    public void A_monsters_clan_never_changes(string home, long seed)
    {
        Assert.Equal(Clans.Of(home, seed), Clans.Of(home, seed));
    }

    // The same numbers are in the game's tests (ClanTests): a monster has to be the same clan on the phone and the server.
    [Theory]
    [InlineData("MON_0001", 42L, Clan.Earth)]
    [InlineData("MON_0002", 123456789L, Clan.Earth)]
    [InlineData("MON_0016", 7L, Clan.Wind)]
    [InlineData("MON_0010", -99L, Clan.Wind)]
    [InlineData("MON_0014", long.MaxValue, Clan.Wind)]
    [InlineData("MON_0005", 1L, Clan.Light)]
    public void A_monsters_clan_is_the_same_as_in_the_game(string species, long seed, Clan clan)
    {
        Assert.Equal(clan, Clans.Of(Catalog, species, seed));
    }

    [Fact]
    public void A_monster_with_an_unknown_home_still_gets_a_clan()
    {
        Assert.Contains(Clans.Of("Banana", 42L), Clans.All);
        Assert.Contains(Clans.Of((string?)null, 42L), Clans.All);
    }

    // ------------------------------------------------------------------ in a battle

    [Fact]
    public void The_strong_side_gains_and_the_weak_side_loses()
    {
        var s = new DuelStats(100, 200, 300, 400);

        Assert.Equal(new DuelStats(115, 230, 345, 460), Clans.InBattle(s, Clan.Fire, Clan.Wind));
        Assert.Equal(new DuelStats(90, 180, 270, 360), Clans.InBattle(s, Clan.Wind, Clan.Fire));
        Assert.Equal(s, Clans.InBattle(s, Clan.Fire, Clan.Earth));
        Assert.Equal(s, Clans.InBattle(s, Clan.Fire, Clan.Fire));
    }

    [Fact]
    public void Rivals_hit_harder_but_defend_no_better()
    {
        Assert.Equal(new DuelStats(110, 200, 330, 400), Clans.InBattle(new DuelStats(100, 200, 300, 400), Clan.Light, Clan.Darkness));
    }

    [Fact]
    public void Health_isnt_touched()
    {
        Assert.Equal(77, Clans.InBattle(new MonsterBattleStats(100, 100, 100, 100, 77), Clan.Fire, Clan.Wind).Health);
    }

    private static float ChanceOf(DuelStats mine, Clan myClan, DuelStats theirs, Clan theirClan) =>
        DuelRules.Resolve(1, Clans.InBattle(mine, myClan, theirClan), DuelStrategy.Balanced, false, 0, false, Clans.InBattle(theirs, theirClan, myClan), DuelStrategy.Balanced, false, 0, false).Player.Chance;

    [Fact]
    public void Between_equals_the_edge_is_real_but_no_sure_thing()
    {
        var even = new DuelStats(100, 100, 100, 100);

        Assert.InRange(ChanceOf(even, Clan.Fire, even, Clan.Wind), 0.56f, 0.62f);
        Assert.InRange(ChanceOf(even, Clan.Wind, even, Clan.Fire), 0.38f, 0.44f);
        Assert.Equal(0.5f, ChanceOf(even, Clan.Fire, even, Clan.Earth));
        Assert.Equal(0.5f, ChanceOf(even, Clan.Light, even, Clan.Darkness));
    }

    [Fact]
    public void A_much_stronger_monster_can_still_beat_a_bad_matchup()
    {
        var strong = new DuelStats(150, 150, 150, 150);
        var weak = new DuelStats(100, 100, 100, 100);

        Assert.True(ChanceOf(strong, Clan.Wind, weak, Clan.Fire) > 0.5f);
    }

    [Fact]
    public void No_clan_comes_out_ahead_overall()
    {
        var even = new DuelStats(100, 100, 100, 100);
        foreach (Clan clan in Clans.All)
        {
            float average = Clans.All.Average(c => ChanceOf(even, clan, even, c));
            Assert.InRange(average, 0.49f, 0.51f);
        }
    }

    // ------------------------------------------------------------------ on its card

    [Fact]
    public void Each_clan_has_its_own_way_of_fighting()
    {
        Assert.Equal(115, Clans.LeanPower(100, Clan.Fire));
        Assert.Equal(95, Clans.LeanGuard(100, Clan.Fire));
        Assert.Equal(115, Clans.LeanWits(100, Clan.Water));
        Assert.Equal(115, Clans.LeanGuard(100, Clan.Earth));
        Assert.Equal(115, Clans.LeanTricks(100, Clan.Wind));
        Assert.Equal(110, Clans.LeanGuard(100, Clan.Light));
        Assert.Equal(110, Clans.LeanTricks(100, Clan.Darkness));
    }

    [Fact]
    public void Every_clans_way_of_fighting_adds_up_to_the_same()
    {
        foreach (Clan clan in Clans.All)
        {
            int total = Clans.LeanPower(100, clan) + Clans.LeanGuard(100, clan) + Clans.LeanTricks(100, clan) + Clans.LeanWits(100, clan);
            Assert.Equal(410, total);
        }
    }
}
