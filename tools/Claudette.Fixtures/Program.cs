// Turns a protocol log into a protocol fixture (DESIGN.md §15, "Protocol fixtures"):
//
//   dotnet run --project tools/Claudette.Fixtures -- <protocol.log> <fixture.jsonl> [--root <folder>]...
//
// A protocol log comes from Settings → Advanced → Log protocol traffic (in <data folder>/logs/protocol), or from the
// recording tests (CLAUDETTE_RECORD_FIXTURES). Each --root is a folder to hide, such as the project the session ran
// in; the home folder is always hidden. Check the result before committing it: the scrubbing covers paths, emails,
// account details and session ids, not what the prompts or files themselves said.
using Claudette.Fixtures;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: <protocol.log> <fixture.jsonl> [--root <folder>]...");
    return 2;
}
var roots = new List<string>();
for (var i = 2; i < args.Length - 1; i++)
{
    if (args[i] == "--root")
    {
        roots.Add(Path.GetFullPath(args[++i]));
    }
}
var lines = new ProtocolFixtureWriter(roots).FromProtocolLog(File.ReadLines(args[0]));
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllLines(args[1], lines);
Console.WriteLine($"Wrote {lines.Count} lines to {args[1]}.");
return 0;
