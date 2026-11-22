// NikomiCli: lets AI agents and scripts read Nikomi's figures as JSON, and ask the user to end a
// process. It asks the running app over a per-user pipe; without the app it measures by itself.
//
//   NikomiCli top --count 10          one answer as JSON, then exit
//   NikomiCli mcp                     a Model Context Protocol server on standard input and output

using System.Reflection;
using System.Text;
using Nikomi.Core;

const string Usage = """
    NikomiCli <command> [options]

    Reading (answers are JSON):
      status                     total CPU load, the runaway rule, current runaways
      top                        busiest processes       [--count N] [--sort S] [--name NAME]
      names                      one row per program     [--count N] [--sort S]
      process                    one process in detail   --id ID | --pid N
      tree                       process trees           [--id ID | --pid N] [--count N] [--sort S]
      hot                        processes at or above the runaway threshold
      history                    the last hours          [--minutes N] [--name NAME] [--count N] [--lines N]

    Ending (needs Nikomi running; the user confirms each request in its window):
      end                        ask to end a process    --id ID --reason "TEXT" [--tree]
      end-result                 outcome of the last request, when it answered "pending"

    For AI agent hosts:
      mcp                        run as a Model Context Protocol server (stdio)

    Options:
      --sort cores|memory|cpu_time|io     what to rank by (default cores)
      --pretty                            indented JSON
      --data-dir FOLDER                   the data folder of the running app to talk to

    Agent access is off until the user turns it on in Nikomi: Settings > Let AI agents read this data.
    """;

var parsed = Arguments.Parse(args,
    switches: ["--tree", "--pretty", "-h", "--help", "/?"],
    options: ["--count", "--sort", "--id", "--pid", "--name", "--minutes", "--lines", "--reason", "--data-dir"],
    command: true);
if (parsed.Unknown.Count > 0)
{
    Console.Error.WriteLine($"Unknown option: {parsed.Unknown[0]}");
    return 2;
}
var request = new AgentRequest
{
    Count = parsed.Number("--count"), Sort = parsed.Value("--sort"), Id = parsed.Value("--id"), Pid = parsed.Number("--pid"),
    Name = parsed.Value("--name"), Minutes = parsed.Number("--minutes"), Lines = parsed.Number("--lines"),
    Reason = parsed.Value("--reason"), Tree = parsed.Has("--tree"),
};
bool pretty = parsed.Has("--pretty");
string? dataDir = parsed.Value("--data-dir");
string? command = parsed.Has("-h") || parsed.Has("--help") || parsed.Has("/?") ? "help" : parsed.Command;

string? known = command switch
{
    "status" or "top" or "names" or "process" or "tree" or "history" or "end" => command,
    "hot" or "runaways" => "runaways",
    "end-result" or "end_result" => "end_result",
    _ => null,
};
if (command is null or "help" || (known == null && command != "mcp"))
{
    Console.Error.WriteLine(Usage);
    return command == "help" ? 0 : 2;
}

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var client = new AgentClient(DataFolder.Resolve(dataDir));

if (command == "mcp")
{
    // Standard output carries protocol messages only: nothing else may be written to it.
    using var input = new StreamReader(Console.OpenStandardInput(), utf8);
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
    string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    await new McpServer(AgentTools.All, (request, cancel) => client.AskAsync(request, cancel), version).RunAsync(input, output);
    return 0;
}

request.Command = known!;
var answer = await client.AskAsync(request);
Console.OutputEncoding = utf8;
Console.WriteLine(answer.ToJsonString(pretty ? AgentClient.Pretty : AgentClient.Compact));
return answer.ContainsKey("error") ? 1 : 0;
