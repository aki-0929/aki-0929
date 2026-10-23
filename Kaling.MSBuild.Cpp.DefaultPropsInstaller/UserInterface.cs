using System.Security.Principal;
using Spectre.Console;

namespace Kaling.MSBuild.Cpp.DefaultPropsInstaller;

internal static class UserInterface
{
    private const string OtherChoice = "Other…";
    private const string BackChoice = "Back";
    private const string QuitChoice = "Quit";

    public static int Run()
    {
        WriteBanner();

        var targets = AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("aqua"))
            .Start("Discovering…", _ => VCTargetsDiscoverer.Discover());

        while (true)
        {
            var propsPath = PromptTarget(targets);
            if (propsPath is null)
                return 0;

            ResetScreen();
            WriteContext(propsPath);

            while (true)
            {
                var action = Prompt(
                    "Action",
                    "Install", "Uninstall", BackChoice, QuitChoice);

                if (action == QuitChoice)
                    return 0;

                if (action == BackChoice)
                {
                    ResetScreen();
                    break;
                }

                try
                {
                    RunAction(action, propsPath);
                }
                catch (Exception ex)
                {
                    ResetScreen();
                    WriteContext(propsPath);
                    WritePanel("Error", Color.Red, ex.Message);
                }
            }
        }
    }

    private static void WriteBanner()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new FigletText("Kaling")
            .Centered()
            .Color(Color.Aqua));
        AnsiConsole.WriteLine();
    }

    private static void ResetScreen()
    {
        AnsiConsole.Clear();
        WriteBanner();
    }

    private static void WriteContext(string propsPath)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(propsPath)}[/]");
        if (!IsElevated() && IsUnderProgramFiles(propsPath))
            AnsiConsole.MarkupLine("[yellow]Program Files — Administrator may be required[/]");
        AnsiConsole.WriteLine();
    }

    private static string? PromptTarget(IReadOnlyList<DiscoveredVCTargets> targets)
    {
        if (targets.Count == 0)
            AnsiConsole.MarkupLine("[yellow]No Visual C++ MSBuild targets found.[/]");

        var choices = targets.Select(item => item.Label).ToList();
        choices.Add(OtherChoice);
        choices.Add(QuitChoice);

        while (true)
        {
            var selected = Prompt("Target", choices.ToArray());

            if (selected == QuitChoice)
                return null;

            if (selected != OtherChoice)
                return targets.First(item => item.Label == selected).PropsPath;

            var path = PromptPath();
            if (path is not null)
                return path;
        }
    }

    private static string? PromptPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>(string.Empty)
                .AllowEmpty()
                .ClearOnFinish(true)
                .Validate(value =>
                {
                    if (string.IsNullOrWhiteSpace(value))
                        return ValidationResult.Success();

                    var error = PropsInstaller.ValidateDirectory(value);
                    return error is null
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"[red]{Markup.Escape(error)}[/]");
                }));

        return string.IsNullOrWhiteSpace(path)
            ? null
            : PropsInstaller.PathIn(path);
    }

    private static void RunAction(string action, string propsPath)
    {
        switch (action)
        {
            case "Install":
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("green"))
                    .Start("Installing…", _ => PropsInstaller.Install(propsPath));

                ResetScreen();
                WriteContext(propsPath);
                WritePanel("Success", Color.Green, "Installed",
                    propsPath,
                    PropsInstaller.PathBeside(propsPath));
                WriteHooks(propsPath);
                return;

            case "Uninstall":
                var removed = 0;
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("gold1"))
                    .Start("Uninstalling…", _ => removed = PropsInstaller.Uninstall(propsPath));

                ResetScreen();
                WriteContext(propsPath);
                if (removed == 0)
                    WritePanel("Notice", Color.Yellow, "Not installed");
                else
                    WritePanel("Success", Color.Green, $"Uninstalled  {removed} item(s)");

                WriteHooks(propsPath);
                return;
        }
    }

    private static string Prompt(string title, params string[] choices)
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold aqua]{title}[/]")
                .HighlightStyle(new Style(Color.Black, Color.Aqua))
                .WrapAround()
                .PageSize(12)
                .AddChoices(choices));
    }

    private static void WritePanel(string header, Color color, string title, params string[] details)
    {
        var lines = new List<string>
        {
            $"[bold {color.ToMarkup()}]{Markup.Escape(title)}[/]"
        };

        foreach (var detail in details)
            lines.Add($"[grey]{Markup.Escape(detail)}[/]");

        AnsiConsole.Write(
            new Panel(new Markup(string.Join("\n", lines)))
                .Header($" {header} ", Justify.Center)
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(color))
                .Padding(1, 0));
    }

    private static void WriteHooks(string propsPath)
    {
        var hooks = PropsInstaller.FindHooks(PropsInstaller.Load(propsPath));
        if (hooks.Count == 0)
            return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[grey]Element[/]")
            .AddColumn("[grey]Label[/]");

        foreach (var el in hooks)
            table.AddRow(
                $"[aqua]{Markup.Escape(el.LocalName)}[/]",
                $"[gold1]{Markup.Escape(el.GetAttribute(PropsInstaller.MarkerAttribute))}[/]");

        AnsiConsole.Write(table);
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsUnderProgramFiles(string path)
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var full = Path.GetFullPath(path);
        return candidates.Any(root =>
            !string.IsNullOrEmpty(root)
            && full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}