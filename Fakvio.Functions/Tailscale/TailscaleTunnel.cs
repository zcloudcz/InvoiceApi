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
//
// WHY probe first: Flex Consumption runs several worker processes on one
// instance and they share /tmp. The first worker copies the binaries and starts
// the daemon; every later worker would overwrite a file the running daemon holds
// open, which Linux answers with ETXTBSY ("Text file busy"). So the SOCKS port is
// probed before anything is copied, and every step below tolerates a sibling
// worker having done it already.
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

    /// <summary>
    /// Environment variable overriding the name the node registers under in the tailnet. Each
    /// environment must use its own (prod and test share one tailnet and one codebase), so the
    /// admin console and the ACLs can tell the two Function Apps apart.
    /// </summary>
    public const string HostnameEnv = "TAILSCALE_HOSTNAME";

    /// <summary>Node name used when <see cref="HostnameEnv"/> is not set.</summary>
    public const string DefaultHostname = "fakvio-func-prod";

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
    private const string CliFileName = "tailscale";
    private const string DaemonFileName = "tailscaled";

    // The whole sequence has one hard ceiling. Worst case inside it: 3 × 30 s of 'tailscale up' plus
    // 2 s + 4 s backoff ≈ 96 s, which leaves the probe whatever is left of the budget. Program.cs
    // runs this in the background — the Functions host gives up on a worker that does not report
    // ready within roughly a minute, and a bring-up that blocked startup turned every tunnel problem
    // into a 502/503 restart loop (the first deployment did exactly that). The budget still matters:
    // it bounds how long the migration behind it waits for a tunnel that is not coming.
    // The ceiling is a cancellation token, so it only cuts steps that can be cancelled: the copy of
    // the binaries into /tmp runs to completion regardless (tens of milliseconds, and a half-copied
    // executable would be worse than a late one).
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(100);

    // Last few lines the daemon printed. tailscaled explains a stuck login on stderr ("auth key
    // expired", "control server unreachable", ...) but those lines only go to the worker logger at
    // Debug level, which nothing in Azure collects — so when 'tailscale up' fails, this tail is
    // written to stdout next to the failure. Small and bounded: it is a diagnostic, not a log.
    private const int DaemonTailLines = 40;
    // The first lines the daemon prints are where sandbox trouble shows up (netns, permissions,
    // socket paths) — by the time the login hangs they have long left the tail.
    private const int DaemonHeadLines = 40;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> DaemonHead = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> DaemonTail = new();

    private const int UpAttempts = 3;
    // 45 s, deliberately longer than the CLI's own --timeout=30s: the CLI must get to print *why* it gave up
    // before this kill lands — a bare "timed out" was all the first deployment left behind.
    private static readonly TimeSpan UpProcessTimeout = TimeSpan.FromSeconds(45);
    private const int ProbeAttempts = 10;
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    // How long to keep looking for a sibling worker's daemon after our own bring-up failed. The
    // daemon that loses the race exits within milliseconds, but the one that won still has to bind
    // 1055 — a single instant re-probe reliably arrives too early and throws away a working tunnel.
    private const int SocksWaitAttempts = 15;
    private static readonly TimeSpan SocksWaitDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Arguments for the daemon. <c>--state=mem:</c> keeps no state on disk, which pairs with an
    /// ephemeral auth key: every cold start registers a fresh node and Tailscale reaps the old one.
    /// </summary>
    public static string TailscaledArguments =>
        $"--tun=userspace-networking --socks5-server=localhost:{SocksPort} --socket={SocketPath} --state=mem: --verbose=1";

    /// <summary>
    /// Arguments for <c>tailscale up</c>. <c>--accept-dns=false</c> because MagicDNS is not used and
    /// rewriting /etc/resolv.conf fails in the sandbox; <c>--timeout</c> so a stuck login gives up
    /// instead of hanging startup.
    /// </summary>
    public static string UpArguments(string authKey, string hostname) =>
        $"--socket={SocketPath} up --authkey={authKey} --hostname={hostname} --accept-dns=false --timeout=30s";

    /// <summary>Reads the node name from the environment, falling back to <see cref="DefaultHostname"/>.</summary>
    public static string ResolveHostname()
    {
        var hostname = Environment.GetEnvironmentVariable(HostnameEnv);
        return string.IsNullOrWhiteSpace(hostname) ? DefaultHostname : hostname.Trim();
    }

    /// <summary>
    /// Starts the tunnel when an auth key is configured. Returns false when the feature is off.
    /// Throws when the key is present but the tunnel cannot be established — the caller logs that
    /// and lets the host start anyway, so telemetry and health checks stay reachable.
    /// The whole sequence is capped by <see cref="StartupBudget"/>; pass ApplicationStopping as
    /// <paramref name="cancellationToken"/> so a shutdown during startup cuts it short too.
    /// </summary>
    public static Task<bool> StartIfConfiguredAsync(
        string? authKey, IHostApplicationLifetime lifetime, ILogger logger, CancellationToken cancellationToken = default)
        => StartIfConfiguredAsync(authKey, lifetime, logger, SocksPort, ListenPort, bringUp: null, cancellationToken);

    /// <summary>
    /// The same bring-up with the seams a test needs: both ports (so a test never touches the ports
    /// a real deployment uses) and the step that produces a running daemon, which otherwise copies
    /// ~50 MB and spawns a child process. Production always passes the constants and no
    /// <paramref name="bringUp"/> override.
    /// </summary>
    internal static async Task<bool> StartIfConfiguredAsync(
        string? authKey,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        int socksPort,
        int listenPort,
        Func<CancellationToken, Task>? bringUp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authKey))
        {
            Milestone(logger, $"{AuthKeyEnv} not set, tunnel disabled");
            return false;
        }

        // One deadline for the whole bring-up, shared by the login retries and the probe. Without it
        // the two budgets add up and the host blocks for minutes.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartupBudget);

        var target = ResolveTarget();
        string key = authKey;
        bringUp ??= async token =>
        {
            var (tailscalePath, daemon) = await EnsureDaemonAsync(socksPort, lifetime, logger);
            await RunUpAsync(tailscalePath, key, daemon, logger, token);
        };

        try
        {
            await bringUp(budget.Token);
        }
        catch (FileNotFoundException)
        {
            // Not a race: a binary missing from the deployment package means no worker on this
            // instance can ever have a daemon, so waiting for one would only delay the failure.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else fails the same way when a sibling worker got there first — the file is
            // busy (ETXTBSY), the daemon socket is taken, the losing daemon exits before it can be
            // authenticated. None of that matters as long as *someone's* daemon serves SOCKS: the
            // forwarder and the connection string only care about that port. Hence the whole
            // bring-up sits inside this block, and the wait is a short poll rather than one instant
            // probe — the winner needs a moment to bind after the loser dies (issue #321).
            if (!await WaitForSocksPortAsync(socksPort, budget.Token))
            {
                throw;
            }

            Milestone(logger, $"bring-up failed, continuing on another worker's daemon: {ex.Message}", LogLevel.Warning);
        }

        Socks5Forwarder.Start(listenPort, socksPort, target.Address, target.Port, logger);
        await ProbeTargetAsync(target, socksPort, logger, budget.Token);
        return true;
    }

    /// <summary>
    /// Polls the SOCKS port for a few seconds and reports whether anything ended up serving it.
    /// The probe deliberately runs before the cancellation check: a tunnel that is up is worth a
    /// forwarder even when the startup budget has already been spent.
    /// </summary>
    private static async Task<bool> WaitForSocksPortAsync(int socksPort, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= SocksWaitAttempts; attempt++)
        {
            if (await IsSocksPortOpenAsync(socksPort))
            {
                return true;
            }

            if (attempt == SocksWaitAttempts)
            {
                break;
            }

            try
            {
                await Task.Delay(SocksWaitDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes one tunnel milestone to the logger and to stdout. Both on purpose: worker
    /// <c>ILogger</c> categories do not reach App Insights today (see the follow-up issue linked from
    /// Tailscale/README.md §6), while stdout arrives as <c>Host.Function.Console</c> — without this
    /// line the tunnel is undiagnosable in Azure. Callers never pass the auth key in
    /// <paramref name="message"/>.
    /// </summary>
    internal static void Milestone(ILogger logger, string message, LogLevel level = LogLevel.Information)
    {
        // One milestone stays one line: messages built from an exception can carry CR/LF from the
        // platform, and Console.Out would turn those into separate Host.Function.Console rows that
        // read like unrelated events.
        var oneLine = message.ReplaceLineEndings(" ");
        logger.Log(level, "Tailscale: {Milestone}", oneLine);
        Console.Out.WriteLine($"Tailscale: {oneLine}");
    }

    /// <summary>
    /// Makes sure a daemon is available and returns the CLI path used to talk to it plus the daemon
    /// process this worker owns — null means a sibling worker already had one running, so there is
    /// nothing here to supervise. The SOCKS probe runs <em>before</em> any binary is touched —
    /// copying over a file the running daemon executes is exactly what throws 'Text file busy'.
    /// </summary>
    // Internal with an injectable prepare step so the probe-first order can be pinned by a test
    // without a tailnet, a /tmp directory or a child process.
    internal static async Task<(string TailscalePath, Process? Daemon)> EnsureDaemonAsync(
        int socksPort,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        Func<(string TailscalePath, string TailscaledPath)>? prepareBinaries = null)
    {
        if (await IsSocksPortOpenAsync(socksPort))
        {
            // Whoever started that daemon also copied the CLI to the shared /tmp directory, so the
            // path is known without touching a single file here.
            Milestone(logger, $"tailscaled already running on 127.0.0.1:{socksPort}, reusing it");
            return (Path.Combine(RuntimeBinDirectory, CliFileName), null);
        }

        var (tailscalePath, tailscaledPath) = (prepareBinaries ?? (() => PrepareBinaries(logger)))();
        return (tailscalePath, StartDaemon(tailscaledPath, lifetime, logger));
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
    private static (string TailscalePath, string TailscaledPath) PrepareBinaries(ILogger logger)
    {
        var sourceDirectory = Path.Combine(AppContext.BaseDirectory, BinDirectoryName);
        Directory.CreateDirectory(RuntimeBinDirectory);

        return (CopyExecutable(sourceDirectory, RuntimeBinDirectory, CliFileName, logger),
                CopyExecutable(sourceDirectory, RuntimeBinDirectory, DaemonFileName, logger));
    }

    /// <summary>
    /// Puts one binary into the runtime directory, tolerating a copy a sibling worker made first.
    /// A failed copy is only survivable when it leaves a complete file behind — see the two
    /// comments inside.
    /// </summary>
    // Internal so the "identical file is left alone" rule can be tested against a temp directory.
    // The copy itself is injectable for one reason only: the tolerated case is a race between the
    // existence check and the copy, and a test cannot slip a sibling worker in between otherwise.
    internal static string CopyExecutable(
        string sourceDirectory,
        string destinationDirectory,
        string fileName,
        ILogger logger,
        Action<string, string>? copyFile = null)
    {
        var source = new FileInfo(Path.Combine(sourceDirectory, fileName));
        if (!source.Exists)
        {
            throw new FileNotFoundException(
                $"Tailscale binary '{fileName}' is missing from '{sourceDirectory}'. " +
                "It is fetched by the 'Download Tailscale binaries' step in the deploy workflow — " +
                "see Fakvio.Functions/Tailscale/README.md.", source.FullName);
        }

        var destination = new FileInfo(Path.Combine(destinationDirectory, fileName));

        // Same name and same length = the copy another worker in this sandbox already made from the
        // very same package. Rewriting it buys nothing and risks ETXTBSY, so it is skipped outright.
        if (!destination.Exists || destination.Length != source.Length)
        {
            try
            {
                (copyFile ?? CopyOverwriting)(source.FullName, destination.FullName);
            }
            catch (IOException ex) when (IsCompleteCopy(destination.FullName, source.Length))
            {
                // 'Text file busy': the destination is being executed right now, which also means it
                // is a working binary — keep it and carry on. Before this, the exception aborted the
                // whole bring-up before the forwarder even started (issue #321).
                logger.LogWarning(ex, "Tailscale: {FileName} is in use, keeping the copy already in {Directory}",
                    fileName, destinationDirectory);
            }
        }

        EnsureExecutable(destination.FullName);
        return destination.FullName;
    }

    private static void CopyOverwriting(string source, string destination) =>
        File.Copy(source, destination, overwrite: true);

    /// <summary>
    /// Fresh look at the destination: is it there, and is it the full length of the source?
    /// A brand new <see cref="FileInfo"/> on purpose. <see cref="FileSystemInfo.Exists"/> caches its
    /// first read, and the snapshot taken before the copy says "missing" in precisely the race this
    /// tolerance exists for — the sibling worker copies and spawns between our check and our copy,
    /// so the stale answer would let the ETXTBSY escape (issue #321). The length check guards the
    /// other direction: a copy cut short by a full disk also leaves the file "existing", and running
    /// a truncated binary is worse than failing here.
    /// </summary>
    private static bool IsCompleteCopy(string path, long expectedLength)
    {
        var current = new FileInfo(path);
        return current.Exists && current.Length == expectedLength;
    }

    /// <summary>Sets the executable bit only when it is missing — chmod on a file another worker is running is pointless work.</summary>
    private static void EnsureExecutable(string path)
    {
        // The tunnel only ever runs on the Linux Function App, but the same assembly is compiled on
        // Windows dev machines where SetUnixFileMode is not supported — hence the guard.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode required = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var mode = File.GetUnixFileMode(path);
        if ((mode & required) != required)
        {
            File.SetUnixFileMode(path, mode | required);
        }
    }

    /// <summary>
    /// Launches tailscaled and ties its lifetime to the host's. The caller has already established
    /// that nothing answers on the SOCKS port, so this always spawns.
    /// </summary>
    private static Process StartDaemon(string tailscaledPath, IHostApplicationLifetime lifetime, ILogger logger)
    {
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
            RecordDaemonLine(line);
        }
    }

    /// <summary>Keeps the last <see cref="DaemonTailLines"/> daemon lines for the failure milestone.</summary>
    // Internal so the cap can be pinned by a test without a daemon.
    internal static void RecordDaemonLine(string line)
    {
        if (DaemonHead.Count < DaemonHeadLines)
        {
            DaemonHead.Enqueue(line);
        }

        DaemonTail.Enqueue(line);
        while (DaemonTail.Count > DaemonTailLines && DaemonTail.TryDequeue(out _))
        {
        }
    }

    /// <summary>The recorded daemon tail as one line, with the auth key stripped just in case.</summary>
    internal static string DaemonTailText(string secret) =>
        Redact(string.Join(" | ", DaemonTail), secret);

    /// <summary>The first lines the daemon printed, redacted the same way.</summary>
    internal static string DaemonHeadText(string secret) =>
        Redact(string.Join(" | ", DaemonHead), secret);

    private static async Task<bool> IsSocksPortOpenAsync(int socksPort)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, socksPort).WaitAsync(TimeSpan.FromSeconds(1));
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
            var (exitCode, output) = await RunToCompletionAsync(tailscalePath, UpArguments(authKey, ResolveHostname()), authKey, cancellationToken);
            if (exitCode == 0)
            {
                Milestone(logger, $"up OK (attempt {attempt})");
                return;
            }

            // A milestone, not a plain warning: the CLI output and the daemon tail are the only clue
            // Azure gives about *why* the login did not happen (rejected key, unreachable control
            // plane, ...), and only stdout reaches App Insights. Both strings are already redacted.
            Milestone(logger,
                $"up failed on attempt {attempt} with exit code {exitCode}: {output} | tailscaled head: {DaemonHeadText(authKey)} | tailscaled tail: {DaemonTailText(authKey)}",
                LogLevel.Warning);

            if (attempt == 1)
            {
                // Once, not per attempt: the first deployment showed the daemon calling
                // control: client.Login and then hearing nothing for 30 s. Whether that is DNS, an
                // outbound block or a slow control plane is undecidable from the daemon tail alone,
                // so one HTTPS round-trip from .NET plus 'tailscale netcheck' are written to stdout.
                await ReportControlPlaneReachabilityAsync(tailscalePath, logger, cancellationToken);
            }

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
    /// Diagnostic only, never throws: can this sandbox resolve and reach the Tailscale control
    /// plane at all, and what does the daemon's own 'netcheck' say about DERP/UDP/DNS? Everything
    /// lands in the stdout milestone, because that is the only channel Azure shows us.
    /// </summary>
    private static async Task ReportControlPlaneReachabilityAsync(
        string tailscalePath, ILogger logger, CancellationToken cancellationToken)
    {
        const string controlHost = "controlplane.tailscale.com";
        string dns;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(controlHost, cancellationToken);
            dns = string.Join(",", addresses.Select(a => a.ToString()));
        }
        catch (Exception ex)
        {
            dns = $"FAILED: {ex.GetType().Name}: {ex.Message}";
        }

        string https;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync($"https://{controlHost}/", cancellationToken);
            https = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            https = $"FAILED: {ex.GetType().Name}: {ex.GetBaseException().Message}";
        }

        string netcheck;
        try
        {
            // No secret in these arguments, so nothing to redact.
            var (exitCode, output) = await RunToCompletionAsync(
                tailscalePath, $"--socket={SocketPath} netcheck", secret: string.Empty, cancellationToken);
            netcheck = $"exit {exitCode}: {output}";
        }
        catch (Exception ex)
        {
            netcheck = $"FAILED: {ex.GetType().Name}: {ex.Message}";
        }

        Milestone(logger,
            $"control-plane diagnostics — DNS {controlHost}: {dns} | HTTPS GET: {https} | netcheck: {netcheck}",
            LogLevel.Warning);
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
            // Whatever the CLI managed to print before the kill is the diagnosis — a rejected key
            // shows up as "To authenticate, visit: https://login.tailscale.com/..." and then waits
            // forever, which is exactly what a bare "timed out" would hide.
            var partial = $"{await stdout}{await stderr}".Trim();
            return (-1, $"timed out after {UpProcessTimeout.TotalSeconds:0} s; output so far: {Redact(partial, secret)}");
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
    private static async Task ProbeTargetAsync(
        IPEndPoint target, int socksPort, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ProbeAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProbeTimeout);
                using var connection = await Socks5Forwarder.ConnectViaSocksAsync(
                    socksPort, target.Address, target.Port, timeout.Token);
                Milestone(logger, $"target reachable ({target.Address}:{target.Port}) after {attempt} attempt(s)");
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
