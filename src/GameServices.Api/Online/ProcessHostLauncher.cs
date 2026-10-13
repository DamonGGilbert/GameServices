using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace GameServices.Api.Online;

/// <summary>
/// Starts the duel host: the game, built as a program for this PC, run without a window
/// (<c>-batchmode -nographics</c>). One process per match.
/// </summary>
public class ProcessHostLauncher : IHostLauncher
{
    private readonly OnlineOptions options;
    private readonly ILogger<ProcessHostLauncher> log;
    private readonly ConcurrentDictionary<string, Process> running = new();

    public ProcessHostLauncher(IOptions<OnlineOptions> options, ILogger<ProcessHostLauncher> log)
    {
        this.options = options.Value;
        this.log = log;
    }

    public Task<string?> LaunchAsync(string matchId, string secret, string hostSocketUrl)
    {
        if (string.IsNullOrWhiteSpace(options.HostExecutable) || !File.Exists(options.HostExecutable))
        {
            log.LogError("Online:HostExecutable isn't set to a real file ('{Path}').", options.HostExecutable);
            return Task.FromResult<string?>("Online duels aren't set up on this server yet (no duel host program).");
        }

        try
        {
            Directory.CreateDirectory(options.HostLogDirectory);

            ProcessStartInfo start = new(options.HostExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.HostExecutable))!
            };

            foreach (string arg in new[]
            {
                "-batchmode", "-nographics", "-duelHost",
                "-hostUrl", hostSocketUrl,
                "-matchId", matchId,
                "-secret", secret,
                "-logFile", Path.GetFullPath(Path.Combine(options.HostLogDirectory, $"host-{matchId}.log"))
            })
            {
                start.ArgumentList.Add(arg);
            }

            Process process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
            running[matchId] = process;
            log.LogInformation("Started duel host for match {Id} (process {Pid}).", matchId, process.Id);
            return Task.FromResult<string?>(null);
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not start the duel host.");
            return Task.FromResult<string?>("The duel host could not be started.");
        }
    }

    public void Stop(string matchId)
    {
        if (!running.TryRemove(matchId, out Process? process))
        {
            return;
        }

        // The host closes itself when its socket closes; this is the safety net.
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }
}
