using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Ua;
using Opc.Ua;
using Spectre.Console;

namespace MachineDataBrowser.Cli;

/// <summary>
/// The <c>mdbrowser</c> command line. Writers are injected so tests can capture the output; <c>terminal</c> is set when
/// stdout is an interactive terminal, and text output then uses tables, trees and a live watch view instead of plain
/// lines (pipes, files, JSON and CSV stay plain).
/// </summary>
internal static class Commands
{
    public const string PasswordVariable = "MDBROWSER_PASSWORD";

    public static RootCommand Build(TextWriter stdout, TextWriter stderr, IAnsiConsole? terminal = null)
    {
        var url = new Argument<string>("url") { Description = "Endpoint: opc.tcp://host:4840, eip://host/1,0, mqtt://broker[/topic/#]" };
        var user = new Option<string?>("--user", "-u") { Description = "User name (OPC UA, MQTT)" };
        var password = new Option<string?>("--password", "-p") { Description = $"Password; or set {PasswordVariable}" };
        var secure = new Option<bool>("--secure") { Description = "OPC UA: use the most secure endpoint the server offers" };
        var trustAll = new Option<bool>("--trust-all") { Description = "Accept untrusted server certificates (lab networks only)" };
        var format = new Option<OutputFormat>("--format", "-f") { Description = "Output: text, json or csv", DefaultValueFactory = _ => OutputFormat.Text };
        var refresh = new Option<int?>("--refresh", "-r") { Description = "Refresh time in ms (MQTT: 0 = every message)" };
        var duration = new Option<TimeSpan?>("--duration", "-d")
        {
            Description = "Stop after this long, e.g. 30s, 5m, 8h (default: until Ctrl+C)",
            CustomParser = r => ParseDuration(r.Tokens.Single().Value, r),
        };
        var count = new Option<int?>("--count", "-n") { Description = "Stop after this many updates" };

        IAnsiConsole? Rich(ParseResult r) => r.GetValue(format) == OutputFormat.Text ? terminal : null;

        async Task RowsAsync(ParseResult r, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            if (Rich(r) is { } console)
            {
                Terminal.Table(console, headers, rows);
            }
            else
            {
                await Output.RowsAsync(stdout, r.GetValue(format), headers, rows).ConfigureAwait(false);
            }
        }

        IUpdateSink Sink(ParseResult r, IReadOnlyList<Node> items) =>
            Rich(r) is { } console ? new LiveWatch(console, items) : new UpdateWriter(stdout, r.GetValue(format));

        ConnectionArgs Connection(ParseResult r) => new(
            r.GetValue(url)!,
            r.GetValue(user),
            r.GetValue(password) ?? Environment.GetEnvironmentVariable(PasswordVariable),
            r.GetValue(secure),
            r.GetValue(trustAll));

        Command WithConnection(Command command)
        {
            command.Arguments.Insert(0, url);
            foreach (var option in new Option[] { user, password, secure, trustAll, format })
            {
                command.Options.Add(option);
            }

            return command;
        }

        // endpoints
        var endpoints = new Command("endpoints", "List the endpoints of an OPC UA server (security, policies, logins)");
        endpoints.Arguments.Add(url);
        endpoints.Options.Add(format);
        endpoints.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var target = r.GetValue(url)!;
            if (DeviceClient.IsEip(target) || DeviceClient.IsMqtt(target))
            {
                throw new CliException("endpoints works for OPC UA (opc.tcp://) only.");
            }

            var list = await OpcUaClient.GetEndpointsAsync(target, ct).ConfigureAwait(false);
            await RowsAsync(r, ["url", "mode", "policy", "level", "logins", "thumbprint"],
                list.Select(e => (IReadOnlyList<string>)[e.Url, e.SecurityMode, e.SecurityPolicy, e.SecurityLevel.ToString(CultureInfo.InvariantCulture), string.Join(' ', e.UserTokens), e.ServerCertificateThumbprint ?? string.Empty])).ConfigureAwait(false);
            return 0;
        }));

        // browse
        var browseNode = new Argument<string?>("node") { Description = "Start node: /Objects/Line1 or an id (default: the root)", Arity = ArgumentArity.ZeroOrOne };
        var depth = new Option<int>("--depth") { Description = "Levels below the start node", DefaultValueFactory = _ => 1 };
        var browse = WithConnection(new Command("browse", "List the address space below a node"));
        browse.Arguments.Add(browseNode);
        browse.Options.Add(depth);
        browse.SetAction((r, ct) => Guard(stderr, async () =>
        {
            await using var client = await Cli.Connection.ConnectAsync(Connection(r), ct).ConfigureAwait(false);
            var start = r.GetValue(browseNode) is { } text
                ? await Cli.Connection.ResolveAsync(client, text, ct).ConfigureAwait(false)
                : new Node(client.Root.NodeId, client.Root.DisplayName, client.ToDisplayId(client.Root.NodeId));
            var rows = new List<IReadOnlyList<string>>();
            // Paths are printed in full when the start was a path, so they can be passed to read/monitor as they are.
            var prefix = r.GetValue(browseNode) is { } startText && startText.StartsWith('/') ? startText.TrimEnd('/') : string.Empty;
            await NodeQueries.BrowseAsync(client, start.Id, prefix, Math.Max(1, r.GetValue(depth)), rows, ct).ConfigureAwait(false);
            if (Rich(r) is { } console)
            {
                Terminal.Tree(console, start.Name, prefix, rows);
            }
            else
            {
                await Output.RowsAsync(stdout, r.GetValue(format), ["path", "class", "id"], rows).ConfigureAwait(false);
            }

            return 0;
        }));

        // read
        var nodes = new Argument<string[]>("nodes") { Description = "Nodes: /Objects/Line1/Speed or ids", Arity = ArgumentArity.OneOrMore };
        var recursive = new Option<bool>("--recursive", "-R") { Description = "Folders and structures expand to every variable below them" };
        var recursiveDepth = new Option<int>("--depth") { Description = "With --recursive: levels below each node", DefaultValueFactory = _ => NodeQueries.DefaultRecursiveDepth };
        var maxItems = new Option<int>("--max-items") { Description = "With --recursive: at most this many variables", DefaultValueFactory = _ => NodeQueries.DefaultMaxItems };

        Task<List<Node>> NodesAsync(IDeviceClient client, ParseResult r, CancellationToken ct) => r.GetValue(recursive)
            ? NodeQueries.ExpandAsync(client, r.GetValue(nodes)!, Math.Max(1, r.GetValue(recursiveDepth)), Math.Max(1, r.GetValue(maxItems)), stderr.WriteLineAsync, ct)
            : NodeQueries.ResolveAllAsync(client, r.GetValue(nodes)!, ct);

        void AddRecursiveOptions(Command command)
        {
            foreach (var option in new Option[] { recursive, recursiveDepth, maxItems })
            {
                command.Options.Add(option);
            }
        }

        var read = WithConnection(new Command("read", "Read the current value of one or more variables"));
        read.Arguments.Add(nodes);
        AddRecursiveOptions(read);
        read.SetAction((r, ct) => Guard(stderr, async () =>
        {
            await using var client = await Cli.Connection.ConnectAsync(Connection(r), ct).ConfigureAwait(false);
            var resolved = await NodesAsync(client, r, ct).ConfigureAwait(false);
            var values = await client.ReadValuesAsync([.. resolved.Select(n => n.Id)], ct).ConfigureAwait(false);
            var fmt = r.GetValue(format);
            if (fmt == OutputFormat.Json)
            {
                var array = new JsonArray([.. resolved.Select((n, i) => (JsonNode)new JsonObject
                {
                    ["name"] = n.Name,
                    ["id"] = n.DisplayId,
                    ["type"] = ValueJson.TypeName(values[i]),
                    ["value"] = ValueJson.ToJson(values[i]),
                })]);
                await stdout.WriteLineAsync(array.ToJsonString(Output.Indented)).ConfigureAwait(false);
                await stdout.FlushAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await RowsAsync(r, ["name", "type", "value"],
                    resolved.Select((n, i) => (IReadOnlyList<string>)[n.Name, values[i] is null ? "-" : ValueJson.TypeName(values[i]), values[i] is null ? "(read failed)" : ValueFormatter.Format(new Variant(values[i]))])).ConfigureAwait(false);
            }

            return values.Any(v => v is null) ? 1 : 0;
        }));

        // monitor
        var save = new Option<string?>("--save") { Description = "Also save the monitored items as an app session file (.mdbsession)" };
        var force = new Option<bool>("--force") { Description = "Replace an existing session file" };
        var monitor = WithConnection(new Command("monitor", "Stream live values until Ctrl+C, --duration or --count"));
        monitor.Arguments.Add(nodes);
        AddRecursiveOptions(monitor);
        foreach (var option in new Option[] { refresh, duration, count, save, force })
        {
            monitor.Options.Add(option);
        }

        monitor.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var target = Connection(r);
            await using var client = await Cli.Connection.ConnectAsync(target, ct).ConfigureAwait(false);
            var resolved = await NodesAsync(client, r, ct).ConfigureAwait(false);
            var interval = r.GetValue(refresh) ?? (DeviceClient.IsMqtt(target.Url) ? 0 : 250);
            if (r.GetValue(save) is { } sessionFile)
            {
                var saved = await SessionWriter.CreateAsync(sessionFile, target, interval, client, [.. resolved.Select(n => new SessionWriter.Item(n, null))], r.GetValue(force), ct).ConfigureAwait(false);
                await stderr.WriteLineAsync($"mdbrowser: saved {saved.Total} watch item(s) to {sessionFile}.").ConfigureAwait(false);
            }

            await using var sink = Sink(r, resolved);
            return await StreamAsync(client, [.. resolved.Select(n => (n, interval))], sink, stderr, r.GetValue(duration), r.GetValue(count), ct).ConfigureAwait(false);
        }));

        // run
        var sessionPath = new Argument<string>("session") { Description = "Session file saved by the app (.mdbsession)" };
        var run = new Command("run", "Monitor the watch list of an app session file, without the app");
        run.Arguments.Add(sessionPath);
        foreach (var option in new Option[] { password, trustAll, format, duration, count })
        {
            run.Options.Add(option);
        }

        run.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var session = await SessionFile.LoadAsync(r.GetValue(sessionPath)!, ct).ConfigureAwait(false);
            var watch = session.Watch ?? [];
            if (watch.Count == 0)
            {
                throw new CliException("The session has no watch items.");
            }

            var target = new ConnectionArgs(
                session.EndpointUrl,
                session.UserName,
                r.GetValue(password) ?? Environment.GetEnvironmentVariable(PasswordVariable),
                session.UseSecurity,
                session.AutoAcceptCertificates || r.GetValue(trustAll));
            await using var client = await Cli.Connection.ConnectAsync(target, ct).ConfigureAwait(false);
            var fallback = session.DefaultRefreshMs ?? (DeviceClient.IsMqtt(session.EndpointUrl) ? 0 : 250);
            var items = new List<(Node, int)>();
            foreach (var entry in watch)
            {
                try
                {
                    var id = client.ParsePortableId(entry.NodeId);
                    items.Add((new Node(id, entry.DisplayName, client.ToDisplayId(id)), entry.RefreshMs ?? fallback));
                }
                catch (Exception ex) when (ex is ServiceResultException or FormatException or ArgumentException)
                {
                    await stderr.WriteLineAsync($"mdbrowser: skipped {entry.DisplayName}: {ex.Message}").ConfigureAwait(false);
                }
            }

            await using var sink = Sink(r, [.. items.Select(i => i.Item1)]);
            return await StreamAsync(client, items, sink, stderr, r.GetValue(duration), r.GetValue(count), ct).ConfigureAwait(false);
        }));

        // mcp
        var mcpEndpoints = new Option<string[]>("--endpoint", "-e") { Description = "Endpoint the agent may use; repeat for several" };
        var mcpSessions = new Option<string[]>("--session", "-s") { Description = "Session file whose endpoint and options the agent may use; repeat for several" };
        var mcp = new Command("mcp", "Run an MCP server on stdin/stdout: read-only tools for AI agents (browse, search, read, sample)");
        foreach (var option in new Option[] { mcpEndpoints, mcpSessions, user, password, secure, trustAll })
        {
            mcp.Options.Add(option);
        }

        mcp.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var secret = r.GetValue(password) ?? Environment.GetEnvironmentVariable(PasswordVariable);
            var allowed = new List<ConnectionArgs>();
            allowed.AddRange((r.GetValue(mcpEndpoints) ?? []).Select(url => new ConnectionArgs(url, r.GetValue(user), secret, r.GetValue(secure), r.GetValue(trustAll))));
            foreach (var path in r.GetValue(mcpSessions) ?? [])
            {
                var session = await SessionFile.LoadAsync(path, ct).ConfigureAwait(false);
                allowed.Add(new ConnectionArgs(session.EndpointUrl, session.UserName, secret, session.UseSecurity, session.AutoAcceptCertificates || r.GetValue(trustAll)));
            }

            if (allowed.Count == 0)
            {
                throw new CliException("Give the agent at least one machine: --endpoint <url> or --session <file>.");
            }

            await using var pool = new Mcp.EndpointPool([.. allowed.DistinctBy(e => e.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)]);
            return await Mcp.McpServerHost.RunAsync(pool, ct).ConfigureAwait(false);
        }));

        // write
        var writeNode = new Argument<string?>("node") { Description = "Variable: /Objects/Line1/Setpoint or an id", Arity = ArgumentArity.ZeroOrOne };
        var writeValue = new Argument<string?>("value") { Description = "New value as text, converted to the variable's type; arrays comma-separated, e.g. [1, 2, 3]", Arity = ArgumentArity.ZeroOrOne };
        var sets = new Option<string[]>("--set") { Description = "node=value; repeat to write several. Paths split at the first '=', ids at the last" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Write without asking (scripts)" };
        var write = WithConnection(new Command("write", "Write values to variables; asks first unless --yes"));
        write.Arguments.Add(writeNode);
        write.Arguments.Add(writeValue);
        write.Options.Add(sets);
        write.Options.Add(yes);
        write.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var requests = WriteRequests(r.GetValue(writeNode), r.GetValue(writeValue), r.GetValue(sets) ?? []);
            await using var client = await Cli.Connection.ConnectAsync(Connection(r), ct).ConfigureAwait(false);
            var targets = await NodeQueries.ResolveAllAsync(client, requests.Select(w => w.Node), ct).ConfigureAwait(false);
            var before = await client.ReadValuesAsync([.. targets.Select(n => n.Id)], ct).ConfigureAwait(false);

            if (!r.GetValue(yes))
            {
                // Writing changes a machine: ask, and never guess in a script.
                if (terminal is not { Profile.Capabilities.Interactive: true })
                {
                    throw new CliException("Writing changes the device. Confirm it in a terminal, or add --yes.");
                }

                Terminal.Table(terminal, ["name", "now", "write"], targets.Select((n, i) => (IReadOnlyList<string>)[n.Name, Text(before[i]), requests[i].Value]));
                var question = $"Write {(targets.Count == 1 ? "this value" : $"these {targets.Count} values")} to {r.GetValue(url)}?";
                if (!await new ConfirmationPrompt(question) { DefaultValue = false }.ShowAsync(terminal, ct).ConfigureAwait(false))
                {
                    await stderr.WriteLineAsync("mdbrowser: nothing written.").ConfigureAwait(false);
                    return 1;
                }
            }

            var errors = new string?[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                try
                {
                    await client.WriteValueAsync(targets[i].Id, requests[i].Value, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && Errors.IsRecoverable(ex))
                {
                    errors[i] = Describe(ex);
                }
            }

            var after = await ReadBackAsync(client, targets, before, errors, ct).ConfigureAwait(false);
            if (r.GetValue(format) == OutputFormat.Json)
            {
                var array = new JsonArray([.. targets.Select((n, i) => (JsonNode)new JsonObject
                {
                    ["name"] = n.Name,
                    ["id"] = n.DisplayId,
                    ["before"] = ValueJson.ToJson(before[i]),
                    ["after"] = ValueJson.ToJson(after[i]),
                    ["written"] = errors[i] is null,
                    ["error"] = errors[i],
                })]);
                await stdout.WriteLineAsync(array.ToJsonString(Output.Indented)).ConfigureAwait(false);
                await stdout.FlushAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await RowsAsync(r, ["name", "before", "after", "result"],
                    targets.Select((n, i) => (IReadOnlyList<string>)[n.Name, Text(before[i]), Text(after[i]), errors[i] ?? "written"])).ConfigureAwait(false);
            }

            return errors.Any(e => e is not null) ? 1 : 0;
        }));

        // session create / add
        var sessionFile = new Argument<string>("file") { Description = "Session file (.mdbsession), opened by the app and by run" };
        var create = WithConnection(new Command("create", "Create a session file with these nodes as its watch list"));
        create.Arguments.Insert(0, sessionFile);
        create.Arguments.Add(nodes);
        AddRecursiveOptions(create);
        create.Options.Add(refresh);
        create.Options.Add(force);
        create.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var target = Connection(r);
            await using var client = await Cli.Connection.ConnectAsync(target, ct).ConfigureAwait(false);
            var resolved = await NodesAsync(client, r, ct).ConfigureAwait(false);
            var file = r.GetValue(sessionFile)!;
            var saved = await SessionWriter.CreateAsync(file, target, r.GetValue(refresh), client, [.. resolved.Select(n => new SessionWriter.Item(n, null))], r.GetValue(force), ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"Saved {saved.Total} watch item(s) to {file} ({target.Url}).").ConfigureAwait(false);
            return 0;
        }));

        var add = new Command("add", "Add nodes to the watch list of a session file; everything else in it is kept");
        add.Arguments.Add(sessionFile);
        add.Arguments.Add(nodes);
        AddRecursiveOptions(add);
        foreach (var option in new Option[] { refresh, password, trustAll })
        {
            add.Options.Add(option);
        }

        add.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var file = r.GetValue(sessionFile)!;
            var document = await SessionWriter.LoadAsync(file, ct).ConfigureAwait(false);
            var endpoint = (string?)document["endpointUrl"] ?? throw new CliException($"'{file}' has no endpoint.");
            var target = new ConnectionArgs(
                endpoint,
                (string?)document["userName"],
                r.GetValue(password) ?? Environment.GetEnvironmentVariable(PasswordVariable),
                document["useSecurity"]?.GetValue<bool>() ?? false,
                (document["autoAcceptCertificates"]?.GetValue<bool>() ?? false) || r.GetValue(trustAll));
            await using var client = await Cli.Connection.ConnectAsync(target, ct).ConfigureAwait(false);
            var resolved = await NodesAsync(client, r, ct).ConfigureAwait(false);
            var result = await SessionWriter.AddAsync(file, document, client, [.. resolved.Select(n => new SessionWriter.Item(n, r.GetValue(refresh)))], ct).ConfigureAwait(false);
            await stdout.WriteLineAsync($"Added {result.Added} watch item(s) to {file}" +
                (result.AlreadyThere > 0 ? $" ({result.AlreadyThere} already there)" : string.Empty) + $"; {result.Total} in total.").ConfigureAwait(false);
            return 0;
        }));

        var session = new Command("session", "Create session files for the app, or add nodes to them") { create, add };

        return new RootCommand("Machine Data Browser on the command line: OPC UA, EtherNet/IP (Logix) and MQTT")
        {
            endpoints, browse, read, monitor, run, write, session, mcp,
        };
    }

    /// <summary>MQTT values come back through the broker, so a write shows up a moment later; wait for it briefly.</summary>
    private static readonly TimeSpan DynamicReadBack = TimeSpan.FromSeconds(2);

    internal static List<(string Node, string Value)> WriteRequests(string? node, string? value, IEnumerable<string> sets)
    {
        var requests = new List<(string Node, string Value)>();
        if (node is not null)
        {
            requests.Add((node, value ?? throw new CliException("Give the new value: mdbrowser write <url> <node> <value>.")));
        }

        foreach (var set in sets)
        {
            // Paths can't contain '=' before the value, so values may; ids (ns=3;s=X) contain '=' themselves.
            var split = set.StartsWith('/') ? set.IndexOf('=', StringComparison.Ordinal) : set.LastIndexOf('=');
            if (split <= 0)
            {
                throw new CliException($"--set '{set}' is not node=value.");
            }

            requests.Add((set[..split], set[(split + 1)..]));
        }

        return requests.Count > 0 ? requests : throw new CliException("Nothing to write: give <node> <value> or --set node=value.");
    }

    private static async Task<IReadOnlyList<object?>> ReadBackAsync(IDeviceClient client, List<Node> targets, IReadOnlyList<object?> before, string?[] errors, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (client is IDynamicAddressSpace ? DynamicReadBack : TimeSpan.Zero);
        while (true)
        {
            var after = await client.ReadValuesAsync([.. targets.Select(n => n.Id)], cancellationToken).ConfigureAwait(false);
            var pending = Enumerable.Range(0, targets.Count).Any(i => errors[i] is null && Text(after[i]) == Text(before[i]));
            if (!pending || DateTime.UtcNow >= deadline)
            {
                return after;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Text(object? value) => value is null ? "-" : ValueFormatter.Format(new Variant(value));

    private static async Task<int> Guard(TextWriter stderr, Func<Task<int>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception ex) when (Errors.IsRecoverable(ex))
        {
            await stderr.WriteLineAsync($"mdbrowser: {Describe(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        CliException => ex.Message,
        ServiceResultException sre => $"{sre.Result.StatusCode.SymbolicId ?? sre.StatusCode.ToString(CultureInfo.InvariantCulture)}: {sre.Message}",
        AggregateException { InnerExceptions.Count: 1 } a => Describe(a.InnerExceptions[0]),
        IOException or TimeoutException or InvalidOperationException or FormatException or NotSupportedException
            or System.Net.Sockets.SocketException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    /// <summary>Monitors <paramref name="items"/> and writes every update until cancelled, the duration passes or the count is reached.</summary>
    private static async Task<int> StreamAsync(
        IDeviceClient client,
        IReadOnlyList<(Node Node, int RefreshMs)> items,
        IUpdateSink writer,
        TextWriter stderr,
        TimeSpan? duration,
        int? count,
        CancellationToken cancellationToken)
    {
        var byId = items.GroupBy(i => i.Node.Id).ToDictionary(g => g.Key, g => g.First().Node);
        var received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnUpdate(ValueUpdate update)
        {
            if (done.Task.IsCompleted || !byId.TryGetValue(update.NodeId, out var node))
            {
                return;
            }

            writer.Post(node.Name, node.DisplayId, update);
            if (count is { } max && Interlocked.Increment(ref received) >= max)
            {
                done.TrySetResult();
            }
        }

        if (writer.WantsInitialValues)
        {
            // Show what the device holds right now, before the first change arrives.
            var ids = byId.Keys.ToList();
            var current = await client.ReadValuesAsync(ids, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            for (var i = 0; i < ids.Count; i++)
            {
                if (current[i] is { } value)
                {
                    writer.Seed(byId[ids[i]].DisplayId, new ValueUpdate(ids[i], ValueFormatter.Format(new Variant(value)), StatusCodes.Good, now, now, Raw: value));
                }
            }
        }

        var handles = new List<IAsyncDisposable>();
        try
        {
            foreach (var group in items.GroupBy(i => i.RefreshMs))
            {
                var results = await client.MonitorManyAsync([.. group.Select(i => i.Node.Id).Distinct()], OnUpdate, group.Key, cancellationToken).ConfigureAwait(false);
                foreach (var result in results)
                {
                    if (result.Handle is { } handle)
                    {
                        handles.Add(handle);
                    }
                    else
                    {
                        await stderr.WriteLineAsync($"mdbrowser: cannot monitor {byId[result.NodeId].Name}: {result.Error}").ConfigureAwait(false);
                    }
                }
            }

            if (handles.Count == 0)
            {
                throw new CliException("Nothing could be monitored.");
            }

            try
            {
                await done.Task.WaitAsync(duration ?? Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // --duration elapsed or Ctrl+C: both are a normal end of a stream.
            }

            return 0;
        }
        finally
        {
            done.TrySetResult();
            await DeviceClient.StopMonitoringAsync(handles, CancellationToken.None).ConfigureAwait(false);
        }
    }


    /// <summary>Durations like <c>30s</c>, <c>5m</c>, <c>8h</c>, <c>1d</c>, <c>500ms</c>, or <c>hh:mm:ss</c>.</summary>
    internal static TimeSpan? ParseDuration(string text, System.CommandLine.Parsing.ArgumentResult? result = null)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^(\d+(?:\.\d+)?)(ms|s|m|h|d)$");
        if (match.Success)
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return match.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(value),
                "s" => TimeSpan.FromSeconds(value),
                "m" => TimeSpan.FromMinutes(value),
                "h" => TimeSpan.FromHours(value),
                _ => TimeSpan.FromDays(value),
            };
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
        {
            return span;
        }

        result?.AddError($"'{text}' is not a duration; use e.g. 30s, 5m, 8h.");
        return null;
    }
}
