using System.Globalization;

namespace SquishyDisk.Core;

public static class DiskSpdCommand
{
    public static IReadOnlyList<string> Build(TestCase test, BenchmarkSettings settings, string file, string eventPrefix)
    {
        var w = test.Workload;
        var args = new List<string> { $"-b{w.BlockSize}", $"-o{w.QueueDepth}", $"-t{w.Threads}",
            $"-d{settings.DurationSeconds}", $"-W{settings.WarmupSeconds}", "-C0", "-L", "-Rxml",
            "-ap", "-bsp", "-z0", settings.Cache == CacheMode.WriteThrough ? "-Suw" : "-Su",
            test.Direction == TestDirection.Read ? "-w0" : "-w100",
            $"-ys{eventPrefix}-start", $"-yf{eventPrefix}-finish", $"-yp{eventPrefix}-stop" };
        if (w.Pattern == AccessPattern.Random) args.Add("-r");
        else args.Add(w.Threads > 1 ? "-si" : "-s");
        if (test.Direction == TestDirection.Write) args.Add(settings.Data == DataPattern.Zero ? "-Z" : "-Z16M");
        args.Add(file);
        return args;
    }

    public static string Display(string executable, IReadOnlyList<string> args) =>
        string.Join(" ", new[] { executable }.Concat(args).Select(Quote));
    private static string Quote(string arg) => arg.Any(c => char.IsWhiteSpace(c) || c == '"') ?
        "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : arg;
}
