using System.IO;

namespace UltraZip.Models;

public enum StartupAction
{
    Open,
    Add,
    ExtractHere,
    ExtractHereDelete,
    ExtractToDialog,
    ExtractTo,
    Test
}

public sealed class StartupRequest
{
    public StartupAction Action { get; set; } = StartupAction.Open;
    public List<string> Paths { get; set; } = new();
    public string? Destination { get; set; }
    public string? Password { get; set; }
    public bool Quiet { get; set; }

    public static StartupRequest Parse(IReadOnlyList<string> args)
    {
        var request = new StartupRequest();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--open":
                    request.Action = StartupAction.Open;
                    break;
                case "--add":
                    request.Action = StartupAction.Add;
                    break;
                case "--extract-here":
                    request.Action = StartupAction.ExtractHere;
                    break;
                case "--extract-here-delete":
                    request.Action = StartupAction.ExtractHereDelete;
                    break;
                case "--extract-to-dialog":
                    request.Action = StartupAction.ExtractToDialog;
                    break;
                case "--extract-to":
                    request.Action = StartupAction.ExtractTo;
                    request.Destination = RequireValue(args, ref i, arg);
                    break;
                case "--test":
                    request.Action = StartupAction.Test;
                    break;
                case "--password":
                    request.Password = RequireValue(args, ref i, arg);
                    break;
                case "--quiet":
                    request.Quiet = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("未知参数：" + arg);
                    request.Paths.Add(Path.GetFullPath(arg));
                    break;
            }
        }
        if (request.Action == StartupAction.ExtractTo && string.IsNullOrWhiteSpace(request.Destination))
            throw new ArgumentException("--extract-to 需要目标目录。");
        if (request.Action != StartupAction.Open && request.Paths.Count == 0)
            throw new ArgumentException("该操作需要至少一个文件或文件夹路径。");
        return request;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count) throw new ArgumentException(option + " 缺少参数值。");
        return args[index];
    }
}
