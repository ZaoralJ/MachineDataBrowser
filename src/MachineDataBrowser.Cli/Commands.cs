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
            await BrowseAsync(client, start.Id, prefix, Math.Max(1, r.GetValue(depth)), rows, ct).ConfigureAwait(false);
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
        var recursiveDepth = new Option<int>("--depth") { Description = "With --recursive: levels below each node", DefaultValueFactory = _ => DefaultRecursiveDepth };
        var maxItems = new Option<int>("--max-items") { Description = "With --recursive: at most this many variables", DefaultValueFactory = _ => DefaultMaxItems };

        Task<List<Node>> NodesAsync(IDeviceClient client, ParseResult r, CancellationToken ct) => r.GetValue(recursive)
            ? ExpandAsync(client, r.GetValue(nodes)!, Math.Max(1, r.GetValue(recursiveDepth)), Math.Max(1, r.GetValue(maxItems)), stderr, ct)
            : ResolveAllAsync(client, r.GetValue(nodes)!, ct);

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
        var monitor = WithConnection(new Command("monitor", "Stream live values until Ctrl+C, --duration or --count"));
        monitor.Arguments.Add(nodes);
        AddRecursiveOptions(monitor);
        foreach (var option in new Option[] { refresh, duration, count })
        {
            monitor.Options.Add(option);
        }

        monitor.SetAction((r, ct) => Guard(stderr, async () =>
        {
            var target = Connection(r);
            await using var client = await Cli.Connection.ConnectAsync(target, ct).ConfigureAwait(false);
            var resolved = await NodesAsync(client, r, ct).ConfigureAwait(false);
            var interval = r.GetValue(refresh) ?? (DeviceClient.IsMqtt(target.Url) ? 0 : 250);
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

        return new RootCommand("Machine Data Browser on the command line: OPC UA, EtherNet/IP (Logix) and MQTT")
        {
            endpoints, browse, read, monitor, run,
        };
    }

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
        IOException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    private static async Task<List<Node>> ResolveAllAsync(IDeviceClient client, IEnumerable<string> texts, CancellationToken cancellationToken)
    {
        var result = new List<Node>();
        foreach (var text in texts)
        {
            result.Add(await Cli.Connection.ResolveAsync(client, text, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>The app's "Monitor folder" limits: levels below a node and variables in total.</summary>
    private const int DefaultRecursiveDepth = 10;

    private const int DefaultMaxItems = 500;

    /// <summary>
    /// Every variable below each node (OPC UA: a variable's own properties, like EURange, are left out; MQTT: JSON fields
    /// are included). Names are the path below
    /// the node given, so equal names in different folders stay apart. A variable given directly is kept as it is, except
    /// an MQTT topic with JSON fields.
    /// </summary>
    private static async Task<List<Node>> ExpandAsync(IDeviceClient client, IEnumerable<string> texts, int depth, int maxItems, TextWriter stderr, CancellationToken cancellationToken)
    {
        var result = new List<Node>();
        var seen = new HashSet<NodeId>();

        // MQTT: a JSON topic's fields are what you want to watch. OPC UA: a variable's children are properties (EURange …).
        var descendIntoVariables = client is IDynamicAddressSpace;
        foreach (var root in await ResolveAllAsync(client, texts, cancellationToken).ConfigureAwait(false))
        {
            if (result.Count >= maxItems)
            {
                break;
            }

            // A variable given directly is watched itself (OPC UA: collecting below it would return its properties);
            // an MQTT topic with a JSON payload expands to its fields.
            var attributes = await client.ReadAttributesAsync(root.Id, cancellationToken).ConfigureAwait(false);
            var isVariable = attributes.Any(a => a.Name == "NodeClass" && a.Value == nameof(NodeClass.Variable));
            var found = isVariable && !descendIntoVariables
                ? []
                : await client.CollectVariablesWithPathsAsync(root.Id, depth, maxItems - result.Count, descendIntoVariables, cancellationToken).ConfigureAwait(false);
            if (isVariable && found.Count == 0)
            {
                if (seen.Add(root.Id))
                {
                    result.Add(root);
                }

                continue;
            }

            if (found.Count == 0)
            {
                await stderr.WriteLineAsync($"mdbrowser: no variables within {depth} level(s) below {root.Name}.").ConfigureAwait(false);
                continue;
            }

            foreach (var (item, path) in found.Where(f => seen.Add(f.Item.NodeId)))
            {
                result.Add(new Node(item.NodeId, path.Length == 0 ? item.DisplayName : $"{path}/{item.DisplayName}", client.ToDisplayId(item.NodeId)));
            }
        }

        if (result.Count == 0)
        {
            throw new CliException("No variables found; try a larger --depth.");
        }

        if (result.Count >= maxItems)
        {
            await stderr.WriteLineAsync($"mdbrowser: limited to the first {maxItems} variables (--max-items).").ConfigureAwait(false);
        }

        return result;
    }

    private static async Task BrowseAsync(IDeviceClient client, NodeId parent, string path, int levels, List<IReadOnlyList<string>> rows, CancellationToken cancellationToken)
    {
        foreach (var child in await client.BrowseAsync(parent, cancellationToken).ConfigureAwait(false))
        {
            var childPath = $"{path}/{child.DisplayName}";
            rows.Add([childPath, child.NodeClass.ToString(), client.ToDisplayId(child.NodeId)]);
            if (levels > 1 && child.HasChildren)
            {
                await BrowseAsync(client, child.NodeId, childPath, levels - 1, rows, cancellationToken).ConfigureAwait(false);
            }
        }
    }

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
