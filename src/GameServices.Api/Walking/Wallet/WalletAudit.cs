using GameServices.Api.Data;
using GameServices.Api.Walking.Battles;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Wallet;

/// <summary>Everything the audit found. Empty Problems means every Glimmer is accounted for.</summary>
public record AuditReport(int Wallets, long InCirculation, long EverCreated, long EverSpent, Dictionary<string, long> ByKind, int BattlesChecked, List<string> Problems);

/// <summary>
/// EVERY GLIMMER ACCOUNTED FOR. Nothing changes a balance except WalkingWallet.Change, and that writes a line in the ledger
/// each time - so the books can always be checked, on the server machine:
///
///     dotnet GameServices.Api.dll audit
///
///   * each wallet must equal the sum of its ledger, and every line must follow on from the one before
///   * every fought battle is played again from its stored seed, and must come out the same
///   * and it shows how much Glimmer exists, where it came from and where it went
/// </summary>
public static class WalletAudit
{
    public static async Task<bool> TryRunAdmin(IServiceProvider services, string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "audit", StringComparison.OrdinalIgnoreCase)) return false;

        using IServiceScope scope = services.CreateScope();
        AuditReport report = await RunAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Console.WriteLine(report.Wallets + " wallets hold " + report.InCirculation.ToString("N0") + " Glimmer. Ever added " + report.EverCreated.ToString("N0") + ", ever taken " + report.EverSpent.ToString("N0") + " (Glimmer held and given back counts both ways).");
        foreach ((string kind, long amount) in report.ByKind.OrderByDescending(k => k.Value)) Console.WriteLine("  " + kind + ": " + amount.ToString("N0"));
        Console.WriteLine(report.BattlesChecked + " battles played again from their seeds.");
        Console.WriteLine(report.Problems.Count == 0 ? "Everything adds up." : report.Problems.Count + " problem(s):");
        foreach (string problem in report.Problems) Console.WriteLine("  " + problem);
        return true;
    }

    /// <param name="onlyUser">Just one walker's books (the admin command checks everyone).</param>
    public static async Task<AuditReport> RunAsync(AppDbContext db, Guid? onlyUser = null, CancellationToken cancel = default)
    {
        var problems = new List<string>();

        var wallets = await db.WalkingProfiles.AsNoTracking().Where(p => onlyUser == null || p.UserId == onlyUser).Select(p => new { p.UserId, p.Glimmer }).ToListAsync(cancel);
        var balances = wallets.ToDictionary(w => w.UserId, w => w.Glimmer);

        long created = 0, spent = 0;
        var kinds = new Dictionary<string, long>();
        var totals = new Dictionary<Guid, long>();

        // Through every line in order: each one has to follow on from the one before. (Lines written at the very same
        // moment could be in either order, so they're checked together.)
        var lines = db.WalkingLedger.AsNoTracking().Where(l => onlyUser == null || l.UserId == onlyUser).OrderBy(l => l.UserId).ThenBy(l => l.CreatedUtc)
            .Select(l => new { l.UserId, l.CreatedUtc, l.Amount, l.BalanceAfter, l.Kind });
        Guid who = Guid.Empty;
        DateTime when = default;
        long running = 0, moment = 0, sum = 0;
        var balancesThen = new List<int>();

        void CheckMoment()
        {
            if (balancesThen.Count == 0) return;
            running += moment;
            sum += moment;
            if (!balancesThen.Contains((int)running))
            {
                problems.Add("Wallet " + who + ": the ledger line at " + when.ToString("u") + " doesn't follow on from the one before.");
                running = balancesThen[^1];   // carry on from here, so one break is reported once and not on every line after it
            }
            moment = 0;
            balancesThen.Clear();
        }

        await foreach (var line in lines.AsAsyncEnumerable().WithCancellation(cancel))
        {
            if (line.UserId != who || line.CreatedUtc != when)
            {
                CheckMoment();
                if (line.UserId != who)
                {
                    if (who != Guid.Empty) totals[who] = sum;
                    who = line.UserId;
                    running = 0;
                    sum = 0;
                }
                when = line.CreatedUtc;
            }

            moment += line.Amount;
            balancesThen.Add(line.BalanceAfter);
            if (line.Amount > 0) created += line.Amount;
            else spent -= line.Amount;
            kinds[line.Kind] = kinds.GetValueOrDefault(line.Kind) + line.Amount;
        }

        CheckMoment();
        if (who != Guid.Empty) totals[who] = sum;

        foreach ((Guid user, int glimmer) in balances)
        {
            long total = totals.GetValueOrDefault(user);
            if (total != glimmer) problems.Add("Wallet " + user + " holds " + glimmer + " but its ledger adds up to " + total + ".");
        }

        int checkedBattles = 0;
        var battles = db.WalkingBattles.AsNoTracking().Where(b => b.Status == WalkingBattleStatus.Fought && (onlyUser == null || b.UserId == onlyUser));
        await foreach (WalkingBattle battle in battles.AsAsyncEnumerable().WithCancellation(cancel))
        {
            checkedBattles++;
            Ranked.DuelOutcome outcome = BattleEndpoints.Decide(battle, (Ranked.DuelStrategy)battle.Strategy, out Ranked.DuelStrategy theirs);
            if (outcome.PlayerWon != battle.Won || (int)theirs != battle.TheirStrategy) problems.Add("Battle " + battle.Id + " doesn't come out the same when played again.");
        }

        return new AuditReport(balances.Count, balances.Values.Sum(g => (long)g), created, spent, kinds, checkedBattles, problems);
    }
}
