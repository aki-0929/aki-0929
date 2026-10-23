using System.Xml;

namespace Kaling.MSBuild.Cpp.DefaultPropsInstaller;

/// <summary>
/// Team C++ conventions (runtime library, includes, output dirs).
/// Install writes Kaling.Conventions.props and hooks it from Microsoft.Cpp.Default.props.
/// </summary>
public static class PropsInstaller
{
    public const string TargetFileName = "Microsoft.Cpp.Default.props";
    public const string FileName = "Kaling.Conventions.props";
    public const string MarkerAttribute = "Label";
    public const string MarkerValue = "Kaling";

    private const string MSBuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

    public static string PathIn(string directory)
    {
        return Path.Combine(Path.GetFullPath(directory.Trim().Trim('"')), TargetFileName);
    }

    public static string PathBeside(string defaultPropsPath)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(defaultPropsPath))!, FileName);
    }

    public static XmlDocument Load(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Props file not found: {fullPath}", fullPath);

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(fullPath);
        return doc;
    }

    public static void Save(XmlDocument doc, string filePath)
    {
        doc.Save(Path.GetFullPath(filePath));
    }

    public static bool Matches(XmlDocument doc)
    {
        var root = doc.DocumentElement;
        if (root is null || !string.Equals(root.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
            return false;

        if (root.GetElementsByTagName("VCTargetsPathActual").Count == 0
            && root.GetElementsByTagName("CurrentVCTargetsPath").Count == 0)
            return false;

        var treatAs = root.GetAttribute("TreatAsLocalProperty");
        var treatsPlatform = treatAs
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, "Platform", StringComparison.OrdinalIgnoreCase));

        if (treatsPlatform)
            return true;

        foreach (XmlElement task in root.GetElementsByTagName("UsingTask"))
            if (string.Equals(task.GetAttribute("TaskName"), "CLCommandLine", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    public static string? ValidateDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Directory is required.";

        var dir = Path.GetFullPath(path.Trim().Trim('"'));
        if (File.Exists(dir))
            return "Need a directory, not a file.";

        if (!Directory.Exists(dir))
            return $"Not found: {dir}";

        var props = Path.Combine(dir, TargetFileName);
        if (!File.Exists(props))
            return $"{TargetFileName} not found.";

        try
        {
            if (!Matches(Load(props)))
                return "Not a Visual C++ Default.props.";
        }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            return $"Invalid XML: {ex.Message}";
        }

        return null;
    }

    public static List<XmlElement> FindHooks(XmlDocument doc)
    {
        var result = new List<XmlElement>();
        if (doc.DocumentElement is null)
            return result;

        foreach (var element in EnumerateElements(doc.DocumentElement))
            if (HasMarker(element))
                result.Add(element);

        return result;
    }

    public static void Install(string defaultPropsPath)
    {
        var doc = Load(defaultPropsPath);
        if (doc.DocumentElement is null)
            throw new InvalidDataException("Props XML has no root element.");

        RemoveHooksAndCompanions(doc, defaultPropsPath);
        WriteFile(PathBeside(defaultPropsPath), Nodes());
        AppendForceImportHook(doc);
        Save(doc, defaultPropsPath);
    }

    public static int Uninstall(string defaultPropsPath)
    {
        var doc = Load(defaultPropsPath);
        var removed = RemoveHooksAndCompanions(doc, defaultPropsPath);
        if (removed > 0)
            Save(doc, defaultPropsPath);
        return removed;
    }

    private static int RemoveHooksAndCompanions(XmlDocument defaultPropsDoc, string defaultPropsPath)
    {
        var hooks = FindHooks(defaultPropsDoc);
        var removed = 0;

        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            var el = hooks[i];
            if (el.ParentNode is null)
                continue;

            el.ParentNode.RemoveChild(el);
            removed++;
        }

        removed += DeleteIfExists(PathBeside(defaultPropsPath));
        return removed;
    }

    private static IReadOnlyList<MSBuildElement> Nodes()
    {
        var nl = "\n";
        var banner =
            @" _  __    _    _     ___ _   _  ____ " + nl +
            @"| |/ /   / \  | |   |_ _| \ | |/ ___|" + nl +
            @"| ' /   / _ \ | |    | ||  \| | |  _ " + nl +
            @"| . \  / ___ \| |___ | || |\  | |_| |" + nl +
            @"|_|\_\/_/   \_\_____|___|_| \_|\____|";

        return
        [
            new MSBuildElement
            {
                Name = "Target",
                Attributes = new Dictionary<string, string>
                {
                    ["Name"] = "KalingAnnounceEnabled",
                    ["BeforeTargets"] = "PrepareForBuild"
                },
                Children =
                [
                    Msg(banner),
                    Msg(@"Time: $([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))"),
                    Msg(@"Configuration: $(Configuration)"),
                    Msg(@"Platform: $(Platform)")
                ]
            },
            ItemDefinition("'$(Configuration)' == 'Debug'",
                ClCompile("RuntimeLibrary", "MultiThreadedDebug")),
            ItemDefinition("'$(Configuration)' == 'Release'",
                ClCompile("RuntimeLibrary", "MultiThreaded")),
            ItemDefinition(null,
                ClCompile("AdditionalIncludeDirectories", @"$(ProjectDir)Include;%(AdditionalIncludeDirectories)")),
            new MSBuildElement
            {
                Name = "PropertyGroup",
                Children =
                [
                    Prop("OutDir", @"$(SolutionDir)Build\$(MSBuildProjectName)\$(Platform)\$(Configuration)\"),
                    Prop("IntDir",
                        @"$(SolutionDir)Build\$(MSBuildProjectName)\$(Platform)\$(Configuration)\Intermediate\"),
                    Prop("OutDirWasSpecified", "true")
                ]
            }
        ];
    }

    private static MSBuildElement Msg(string text)
    {
        return new MSBuildElement
        {
            Name = "Message",
            Attributes = new Dictionary<string, string>
            {
                ["Importance"] = "high",
                ["Text"] = text
            }
        };
    }

    private static MSBuildElement Prop(string name, string value)
    {
        return new MSBuildElement
        {
            Name = name,
            Value = value
        };
    }

    private static MSBuildElement ClCompile(string name, string value)
    {
        return new MSBuildElement
        {
            Name = "ClCompile",
            Children = [Prop(name, value)]
        };
    }

    private static MSBuildElement ItemDefinition(string? condition, MSBuildElement child)
    {
        return new MSBuildElement
        {
            Name = "ItemDefinitionGroup",
            Attributes = condition is null
                ? null
                : new Dictionary<string, string> { ["Condition"] = condition },
            Children = [child]
        };
    }

    private static bool HasMarker(XmlElement element)
    {
        return element.HasAttribute(MarkerAttribute)
               && string.Equals(element.GetAttribute(MarkerAttribute), MarkerValue, StringComparison.Ordinal);
    }

    private static IEnumerable<XmlElement> EnumerateElements(XmlElement root)
    {
        yield return root;
        foreach (XmlElement child in root.GetElementsByTagName("*"))
            yield return child;
    }

    private static void WriteFile(string path, IReadOnlyList<MSBuildElement> nodes)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        var project = doc.CreateElement("Project", MSBuildNamespace);
        doc.AppendChild(project);

        foreach (var node in nodes)
            AppendNode(doc, project, MSBuildNamespace, node, 1, true);

        project.AppendChild(doc.CreateWhitespace("\r\n"));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        doc.Save(path);
    }

    private static void AppendForceImportHook(XmlDocument defaultPropsDoc)
    {
        var ns = defaultPropsDoc.DocumentElement!.NamespaceURI;
        var hook = new MSBuildElement
        {
            Name = "PropertyGroup",
            Children =
            [
                new MSBuildElement
                {
                    Name = "ForceImportAfterCppProps",
                    Value = $"$(MSBuildThisFileDirectory){FileName}"
                }
            ]
        };

        AppendNode(defaultPropsDoc, defaultPropsDoc.DocumentElement, ns, hook, 1, true);
    }

    private static int DeleteIfExists(string path)
    {
        if (!File.Exists(path))
            return 0;

        File.Delete(path);
        return 1;
    }

    private static void AppendNode(
        XmlDocument doc,
        XmlElement parent,
        string ns,
        MSBuildElement node,
        int depth,
        bool applyMarker)
    {
        var indent = "\r\n" + new string(' ', depth * 2);
        parent.AppendChild(doc.CreateWhitespace(indent));

        var element = doc.CreateElement(node.Name, ns);
        ApplyAttributes(element, node, applyMarker);

        if (node.HasChildren)
        {
            foreach (var child in node.Children!)
                AppendNode(doc, element, ns, child, depth + 1, false);

            element.AppendChild(doc.CreateWhitespace(indent));
        }
        else if (node.HasValue)
        {
            element.InnerText = node.Value!;
        }

        parent.AppendChild(element);
    }

    private static void ApplyAttributes(XmlElement element, MSBuildElement node, bool applyMarker)
    {
        if (node.Attributes is not null)
            foreach (var (key, value) in node.Attributes)
            {
                if (applyMarker && string.Equals(key, MarkerAttribute, StringComparison.OrdinalIgnoreCase))
                    continue;

                element.SetAttribute(key, value);
            }

        if (applyMarker)
            element.SetAttribute(MarkerAttribute, MarkerValue);
    }
}