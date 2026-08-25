// ============================================================================
// TailscaleTunnel — brings up a userspace Tailscale node inside the Function
// App sandbox and exposes the private PostgreSQL server as a plain local port.
//
// The chain, in order:
//   tailscaled --tun=userspace-networking  -> joins the tailnet, offers SOCKS5
//   tailscale up --authkey=...             -> authenticates the node
//   Socks5Forwarder 127.0.0.1:15432        -> Npgsql-friendly TCP entry point
//   ConnectionStrings__DefaultConnection   -> Host=127.0.0.1;Port=15432;...
//
// WHY userspace: an Azure Functions sandbox cannot create a TUN device, so the
// normal kernel-networking mode is out. Userspace mode routes the tailnet over
// a SOCKS5 proxy instead, which Npgsql cannot speak — hence the forwarder.
//
// Without TAILSCALE_AUTHKEY the whole thing is inert: local development, the
// production host and the unit tests never have the key, and must keep working
// exactly as before. See Tailscale/README.md for setup, ACLs and known limits.
// ============================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Tailscale;

public static class TailscaleTunnel
{
    /// <summary>App Setting / environment variable holding the Tailscale auth key. Absent = feature off.</summary>
    public const string AuthKeyEnv = "TAILSCALE_AUTHKEY";

    /// <summary>Environment variable overriding the tailnet IPv4 of the database server.</summary>
    public const string TargetHostEnv = "TAILSCALE_TARGET_HOST";

    /// <summary>Environment variable overriding the database port on that host.</summary>
    public const string TargetPortEnv = "TAILSCALE_TARGET_PORT";

    // The default location of the daemon socket (/var/run/tailscale/) is not writable in the
    // Flex Consumption sandbox, so both the daemon and every CLI call are pointed at /tmp.
    public const string SocketPath = "/tmp/tailscaled.sock";

    /// <summary>Loopback port the daemon serves SOCKS5 on.</summary>
    public const int SocksPort = 1055;

    /// <summary>Loopback port the forwarder listens on — this is what the connection string targets.</summary>
    public const int ListenPort = 15432;

    // fakvio-db-server on the tailnet. An IPv4 literal on purpose: MagicDNS would need the
    // daemon's DNS resolver, which userspace mode does not wire into the process.
    private const string DefaultTargetHost = "100.69.241.17";
    private const int DefaultTargetPort = 5544;

    // Binaries are downloaded by the deploy workflow into Fakvio.Functions/tsbin/ and copied to
    // the output folder. The package mount may be read-only, so they are copied to /tmp before
    // the executable bit is set.
    private const string BinDirectoryName = "tsbin";
    private const string RuntimeBinDirectory = "/tmp/tsbin";

    // Everything below runs before the host serves its first request, so the whole sequence has one
    // hard ceiling. Worst case inside it: 3 × 30 s of 'tailscale up' plus 2 s + 4 s backoff ≈ 96 s,
    // which leaves the probe whatever is left of the budget. The platform kills a worker that takes
    // too long to start, so exceeding this would turn a tunnel problem into a restart loop.
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(100);

    private const int UpAttempts = 3;
    private static readonly TimeSpan UpProcessTimeout = TimeSpan.FromSeconds(30);
    private const int ProbeAttempts = 10;
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Arguments for the daemon. <c>--state=mem:</c> keeps no state on disk, which pairs with an
    /// ephemeral auth key: every cold start registers a fresh node and Tailscale reaps the old one.
    /// </summary>
    public static string TailscaledArguments =>
        $"--tun=userspace-networking --socks5-server=localhost:{SocksPort} --socket={SocketPath} --state=mem:";

    /// <summary>
    /// Arguments for <c>tailscale up</c>. <c>--accept-dns=false</c> because MagicDNS is not used and
    /// rewriting /etc/resolv.conf fails in the sandbox; <c>--timeout</c> so a stuck login gives up
    /// instead of hanging startup.
    /// </summary>
    public static string UpArguments(string authKey) =>
        $"--socket={SocketPath} up --authkey={authKey} --hostname=fakvio-func --accept-dns=false --timeout=30s";

    /// <summary>
    /// Starts the tunnel when an auth key is configured. Returns false when the feature is off.
    /// Throws when the key is present but the tunnel cannot be established — the caller logs that
    /// and lets the host start anyway, so telemetry and health checks stay reachable.
    /// The whole sequence is capped by <see cref="StartupBudget"/>; pass ApplicationStopping as
    /// <paramref name="cancellationToken"/> so a shutdown during startup cuts it short too.
    /// </summary>
    public static async Task<bool> StartIfConfiguredAsync(
        string? authKey, IHostApplicationLifetime lifetime, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authKey))
        {
            logger.LogInformation("Tailscale: {AuthKeyEnv} not set, tunnel disabled", AuthKeyEnv);
            return false;
        }

        // One deadline for the whole bring-up, shared by the login retries and the probe. Without it
        // the two budgets add up and the host blocks for minutes.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartupBudget);

        var target = ResolveTarget();
        var (tailscalePath, tailscaledPath) = PrepareBinaries();

        var daemon = await StartDaemonIfNeededAsync(tailscaledPath, lifetime, logger);
        await RunUpAsync(tailscalePath, authKey, daemon, logger, budget.Token);

        Socks5Forwarder.Start(ListenPort, SocksPort, target.Address, target.Port, logger);
        await ProbeTargetAsync(target, logger, budget.Token);
        return true;
    }

    /// <summary>Reads the target from the environment, falling back to the known database node.</summary>
    private static IPEndPoint ResolveTarget()
    {
        var host = Environment.GetEnvironmentVariable(TargetHostEnv) ?? DefaultTargetHost;
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            // Fail fast at the boundary: a hostname here would silently never connect, because the
            // SOCKS5 CONNECT this code sends carries an IPv4 literal only. TryParse alone is not
            // enough — it also accepts IPv6, whose 16 address bytes would not fit the request and
            // would surface only per connection, as a debug-level log nobody reads.
            throw new InvalidOperationException(
                $"{TargetHostEnv} must be a tailnet IPv4 address (MagicDNS names are not supported), got '{host}'.");
        }

        var portValue = Environment.GetEnvironmentVariable(TargetPortEnv);
        if (string.IsNullOrWhiteSpace(portValue))
        {
            return new IPEndPoint(address, DefaultTargetPort);
        }

        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{TargetPortEnv} must be a TCP port between 1 and 65535, got '{portValue}'.");
        }

        return new IPEndPoint(address, port);
    }

    /// <summary>
    /// Copies both binaries next to /tmp and marks them executable. The build output may sit on a
    /// read-only package mount, where chmod is not an option.
    /// </summary>
    private static (string TailscalePath, string TailscaledPath) PrepareBinaries()
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, BinDirectoryName);
        Directory.CreateDirectory(RuntimeBinDirectory);

        return (CopyExecutable(sourceDirectory, "tailscale"), CopyExecutable(sourceDirectory, "tailscaled"));
    }

    private static string CopyExecutable(string sourceDirectory, string fileName)
    {
        var source = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                $"Tailscale binary '{fileName}' is missing from '{sourceDirectory}'. " +
                "It is fetched by the 'Download Tailscale binaries' step in the deploy workflow — " +
                "see Fakvio.Functions/Tailscale/README.md.", source);
        }

        var destination = Path.Combine(RuntimeBinDirectory, fileName);
        File.Copy(source, destination, overwrite: true);

        // The tunnel only ever runs on the Linux Function App, but the same assembly is compiled on
        // Windows dev machines where SetUnixFileMode is not supported — hence the guard.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return destination;
    }

    /// <summary>
    /// Starts tailscaled unless something is already answering on the SOCKS5 port. Returns null when
    /// the daemon was already running (nothing for us to supervise in that case).
    /// </summary>
    private static async Task<Process?> StartDaemonIfNeededAsync(string tailscaledPath, IHostApplicationLifetime lifetime, ILogger logger)
    {
        if (await IsSocksPortOpenAsync())
        {
            logger.LogInformation("Tailscale: daemon already listening on 127.0.0.1:{SocksPort}, reusing it", SocksPort);
            return null;
        }

        var daemon = StartProcess(tailscaledPath, TailscaledArguments);

        // Debug level: the daemon is chatty and its lines only matter while diagnosing the tunnel.
        daemon.OutputDataReceived += (_, e) => LogDaemonLine(logger, e.Data);
        daemon.ErrorDataReceived += (_, e) => LogDaemonLine(logger, e.Data);
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();

        // Without this the daemon outlives a graceful shutdown and holds /tmp/tailscaled.sock,
        // which would make the next start in the same sandbox reuse a half-dead node.
        lifetime.ApplicationStopping.Register(() => KillQuietly(daemon));

        logger.LogInformation("Tailscale: daemon started (pid {Pid})", daemon.Id);
        return daemon;
    }

    private static void LogDaemonLine(ILogger logger, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            logger.LogDebug("tailscaled: {Line}", line);
        }
    }

    private static async Task<bool> IsSocksPortOpenAsync()
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, SocksPort).WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Authenticates the node, retrying a couple of times because the daemon needs a moment after
    /// launch before it can answer the CLI.
    /// </summary>
    private static async Task RunUpAsync(
        string tailscalePath, string authKey, Process? daemon, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= UpAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"'tailscale up' did not succeed within the {StartupBudget.TotalSeconds:0} s startup budget.");
            }

            if (daemon is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"tailscaled exited with code {daemon.ExitCode} before the node could be authenticated.");
            }

            // NEVER log the argument string — it carries the auth key.
            var (exitCode, output) = await RunToCompletionAsync(tailscalePath, UpArguments(authKey), authKey, cancellationToken);
            if (exitCode == 0)
            {
                logger.LogInformation("Tailscale: up OK (attempt {Attempt})", attempt);
                return;
            }

            logger.LogWarning("Tailscale: up failed on attempt {Attempt} with exit code {ExitCode}: {Output}",
                attempt, exitCode, output);

            if (attempt < UpAttempts)
            {
                // Deliberately not cancellable: a spent budget is reported by the check at the top
                // of the loop, as a message that names the budget, not as a bare OperationCanceled.
                // The wait itself is at most 4 s.
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), CancellationToken.None);
            }
        }

        throw new InvalidOperationException($"'tailscale up' failed after {UpAttempts} attempts.");
    }

    /// <summary>
    /// Runs a child process to completion under a timeout, returning its exit code and merged output.
    /// The output is untrusted text from a process we handed the auth key to, so the key is stripped
    /// from it here — nothing downstream has to remember to do it.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunToCompletionAsync(
        string fileName, string arguments, string secret, CancellationToken cancellationToken)
    {
        using var process = StartProcess(fileName, arguments);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UpProcessTimeout);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            return (-1, $"timed out after {UpProcessTimeout.TotalSeconds:0} s");
        }

        var output = $"{await stdout}{await stderr}".Trim();
        return (process.ExitCode, Redact(output, secret));
    }

    /// <summary>Replaces the auth key with a marker, so no log line or exception message can carry it.</summary>
    // Internal, not private, so the test can pin this contract without spawning a child process.
    internal static string Redact(string text, string secret) =>
        string.IsNullOrEmpty(secret) ? text : text.Replace(secret, "<redacted>", StringComparison.Ordinal);

    private static Process StartProcess(string fileName, string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.Start();
        return process;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone, or the sandbox refuses the signal — nothing useful to do either way.
        }
    }

    /// <summary>
    /// Checks that the database port actually answers through the tunnel. A failure is only a
    /// warning: EF Core retries the migration, so a slow WireGuard handshake still recovers.
    /// </summary>
    private static async Task ProbeTargetAsync(IPEndPoint target, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ProbeAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                using var connection = await Socks5Forwarder.ConnectViaSocksAsync(
                    SocksPort, target.Address, target.Port, timeout.Token);
                logger.LogInformation("Tailscale: target reachable ({Target}:{Port}) after {Attempt} attempt(s)",
                    target.Address, target.Port, attempt);
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Tailscale: reachability probe {Attempt}/{Total} failed", attempt, ProbeAttempts);
            }

            // No point sleeping after the last attempt — nobody is going to use that pause.
            if (attempt < ProbeAttempts)
            {
                try
                {
                    await Task.Delay(ProbeDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogWarning("Tailscale: target {Target}:{Port} not reachable after {Total} probes — " +
                          "continuing, the database retry policy gets another chance",
            target.Address, target.Port, ProbeAttempts);
    }
}
