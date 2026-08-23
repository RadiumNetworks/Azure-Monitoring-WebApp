using AlertConsole.Configuration;

namespace AlertConsoleCli;

internal sealed record CliOptions(
    string Url,
    int? Minutes,
    int? Hours,
    bool IncludeResolved,
    bool Headed,
    bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        string? explicitUrl = null;
        int? minutes = null;
        int? hours = null;
        var includeResolved = false;
        var headed = false;
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--minutes":
                case "-m":
                    var value = ReadValue(args, ref index, args[index]);
                    if (!int.TryParse(value, out var parsedMinutes) || parsedMinutes <= 0)
                    {
                        throw new ArgumentException("--minutes must be a positive whole number.");
                    }
                    minutes = parsedMinutes;
                    break;
                case "--hours":
                    var hoursValue = ReadValue(args, ref index, args[index]);
                    if (!int.TryParse(hoursValue, out var parsedHours) || parsedHours <= 0)
                    {
                        throw new ArgumentException("--hours must be a positive whole number.");
                    }
                    hours = parsedHours;
                    break;
                case "--include-resolved":
                    includeResolved = true;
                    break;
                case "--url":
                case "-u":
                    explicitUrl = ReadValue(args, ref index, args[index]);
                    break;
                case "--headed":
                    headed = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        if (!showHelp && includeResolved != (hours is not null))
        {
            throw new ArgumentException("--include-resolved and --hours must be used together.");
        }

        if (minutes is not null && hours is not null)
        {
            throw new ArgumentException("--minutes cannot be combined with --include-resolved and --hours.");
        }

        var url = showHelp ? string.Empty : AlertConsoleUrlResolver.Resolve(explicitUrl);
        return new CliOptions(url, minutes, hours, includeResolved, headed, showHelp);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }
}
