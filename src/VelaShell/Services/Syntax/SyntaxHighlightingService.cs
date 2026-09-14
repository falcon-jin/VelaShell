using System.Reflection;
using System.Xml;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using VelaShell.Core.Models;

namespace VelaShell.Services.Syntax;

/// <summary>
/// 语法高亮的装配点:注册本项目自带的语法定义,并把所有定义(含 AvaloniaEdit 内置的)
/// 重着色成当前界面主题的配色。
/// <para>
/// **为什么必须重着色**:AvaloniaEdit 内置的 20 份定义全是给浅色背景调的 ——
/// 关键字 Blue、标点 Black。编辑器底色跟着界面主题走(暗色主题上是深底),
/// 直接用会出现"标点看不见、关键字发暗"的结果。好在这些定义用的都是**命名颜色**且实例被规则共享,
/// 按名字改写 <see cref="HighlightingColor.Foreground" /> 即可整体换肤。
/// </para>
/// </summary>
public static class SyntaxHighlightingService
{
    /// <summary>本项目自带的 xshd(嵌入资源名 → 该定义认领的扩展名)。</summary>
    private static readonly (string Resource, string[] Extensions)[] BundledDefinitions =
    [
        ("VelaShell.Syntax.Shell.xshd", [".sh", ".bash", ".zsh", ".ksh"]),
        ("VelaShell.Syntax.Yaml.xshd", [".yaml", ".yml"]),
        ("VelaShell.Syntax.Ini.xshd", [".ini", ".conf", ".cfg", ".properties", ".env"]),
        ("VelaShell.Syntax.Dockerfile.xshd", [".dockerfile"]),
        ("VelaShell.Syntax.Log.xshd", [".log"]),
        ("VelaShell.Syntax.Nginx.xshd", [".nginx"]),
        ("VelaShell.Syntax.Toml.xshd", [".toml"]),
        ("VelaShell.Syntax.Makefile.xshd", [".mk", ".mak"]),
        ("VelaShell.Syntax.Go.xshd", [".go"]),
        ("VelaShell.Syntax.Rust.xshd", [".rs"]),
        ("VelaShell.Syntax.Lua.xshd", [".lua"]),
        ("VelaShell.Syntax.Ruby.xshd", [".rb", ".rake", ".gemspec"]),
        ("VelaShell.Syntax.Perl.xshd", [".pl", ".pm"]),
        ("VelaShell.Syntax.Sql.xshd", [".sql"]),
        ("VelaShell.Syntax.Hcl.xshd", [".tf", ".tfvars", ".hcl"]),
    ];

    private static readonly Lock RegistrationGate = new();
    private static bool _registered;

    /// <summary>
    /// 解析文件应使用的语法定义并按主题着色;无法判定类型时返回 <c>null</c>(纯文本)。
    /// </summary>
    /// <param name="fileName">文件名或完整远端路径(路径能多提供一层判定依据,如 /etc/nginx/ 下的 .conf)。</param>
    /// <param name="firstLine">文件首行,用于 shebang 判定。</param>
    /// <param name="theme">当前生效的界面主题,语法配色从它的种子色派生。</param>
    public static IHighlightingDefinition? Resolve(string? fileName, string? firstLine, UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        EnsureRegistered();
        string? name = FileTypeDetector.Detect(fileName, firstLine);
        if (name is null)
        {
            return null;
        }
        IHighlightingDefinition? definition = HighlightingManager.Instance.GetDefinition(name);
        if (definition is null)
        {
            return null;
        }

        // 每次打开都按当前主题重着色:定义是全局共享的单例,用户中途切换主题后
        // 下次解析就会拿到正确配色。
        Recolor(definition, SyntaxPalette.From(theme));
        return definition;
    }

    /// <summary>全部已注册的定义(内置 + 自带),供回归用例逐个检查命名颜色是否都有角色。</summary>
    internal static IReadOnlyCollection<IHighlightingDefinition> AllDefinitions
    {
        get
        {
            EnsureRegistered();
            return HighlightingManager.Instance.HighlightingDefinitions;
        }
    }

    /// <summary>把本项目自带的 xshd 注册进 AvaloniaEdit 的全局管理器(只做一次)。</summary>
    private static void EnsureRegistered()
    {
        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }
            _registered = true;
            Assembly assembly = typeof(SyntaxHighlightingService).Assembly;
            foreach ((string resource, string[] extensions) in BundledDefinitions)
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resource);
                    if (stream is null)
                    {
                        continue;
                    }
                    using var reader = XmlReader.Create(stream);
                    IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    HighlightingManager.Instance.RegisterHighlighting(definition.Name, extensions, definition);
                }
                catch (Exception)
                {
                    // 单份定义坏掉不该让编辑器打不开 —— 其余定义照常注册,该类型退化为纯文本。
                }
            }
        }
    }

    /// <summary>
    /// 按命名颜色把定义重着色。名字对不上的颜色兜底做对比度修正,
    /// 免得留下 Black / Blue 之类在深色背景上等于隐形的值。
    /// </summary>
    private static void Recolor(IHighlightingDefinition definition, SyntaxPalette palette)
    {
        foreach (HighlightingColor color in definition.NamedHighlightingColors)
        {
            Color? mapped = palette.ForRole(color.Name);
            if (mapped is { } role)
            {
                color.Foreground = new SimpleHighlightingBrush(role);
                continue;
            }

            // 未收录的角色:只在它与背景过于接近时才动它,尽量保留原定义的表达意图。
            if (color.Foreground?.GetColor(null) is { } current
                && SyntaxPalette.Contrast(current, palette.Background) < SyntaxPalette.MinimumContrast)
            {
                color.Foreground = new SimpleHighlightingBrush(palette.Default);
            }
        }
    }
}

/// <summary>
/// 语法配色:从当前界面主题的**种子色**派生,与终端配色、界面令牌同源 ——
/// 编辑器看起来是应用的一部分,而不是一块贴上去的 Dracula。
/// <para>
/// 取法对 VelaDark / VelaLight 恰好还原 Dracula / Alucard 的正典语法色(字符串黄、关键字粉、
/// 数字紫、函数绿、类型青);其余主题(Nord、Gruvbox、GitHub Light……)各自取自己的对应色。
/// 这里**不写色值字面量**(DESIGN.md:颜色只活在主题目录与令牌派生里)。
/// </para>
/// </summary>
public sealed record SyntaxPalette(
    Color Background,
    Color Default,
    Color Comment,
    Color String,
    Color Keyword,
    Color Number,
    Color Variable,
    Color Function,
    Color Type,
    Color Error,
    Color Link)
{
    /// <summary>
    /// 每个语法角色压在编辑器底色上的最低对比度(WCAG 图形/组件档 3:1)。
    /// 语法色是"认得出类别"而不是长段正文,默认前景仍是主题正文色。
    /// </summary>
    public const double MinimumContrast = 3.0;

    /// <summary>从界面主题派生语法配色;不够 <see cref="MinimumContrast" /> 的角色向正文色靠拢到够为止。</summary>
    public static SyntaxPalette From(UiTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        UiThemePalette p = theme.Palette;
        Color background = Parse(p.BgTerminal);
        Color text = Parse(p.TextPrimary);
        Color yellow = Parse(p.Yellow);
        Color warning = Parse(p.Warning);
        Color error = Parse(p.Error);
        Color info = Parse(p.Info);

        // 亮色主题里 Warning 与 Yellow 往往是同一个值(见 UiThemePalette.Yellow),
        // 直接用会让字符串与变量撞色 —— 那时变量取橙与红的中点,把色相拉开。
        Color variable = warning == yellow ? Blend(warning, error, 0.5) : warning;

        Color Readable(Color color) => EnsureReadable(color, background, text);
        return new(
            Background: background,
            Default: text,
            Comment: Readable(Parse(p.TextTertiary)),
            String: Readable(yellow),
            Keyword: Readable(Parse(p.Magenta)),
            Number: Readable(Parse(p.Accent)),
            Variable: Readable(variable),
            Function: Readable(Parse(p.Success)),
            Type: Readable(info),
            Error: Readable(error),
            // 链接取信息色:Dracula 的链接本来就是青色,亮色主题上是深青蓝 ——
            // 而不是 AvaloniaEdit 缺省的纯蓝(#0000FF 压在暗底上几乎读不出来)。
            Link: Readable(info));
    }

    /// <summary>
    /// 把定义里的颜色名归到调色板角色。
    /// 名字来自各 xshd 作者,同一角色叫法很多(Keyword/Keywords、Number/NumberLiteral……),
    /// 这里统一收口;没收录的返回 <c>null</c>,由调用方走对比度兜底。
    /// 内置与自带定义里出现的每一个名字都必须在这里有归属(<c>SyntaxHighlightingTests</c> 逐个核对)。
    /// </summary>
    public Color? ForRole(string? name) => name switch
    {
        null => null,
        "Comment" or "Comments" or "XmlDoc" or "DocComment" or "Debug" or "BlockQuote"
            or "CommentTags" or "JavaDocTags" or "XmlString" or "KnownDocTags" or "XmlPunctuation" => Comment,
        "String" or "Strings" or "Char" or "Character" or "StringInterpolation" or "Regex"
            or "AttributeValue" or "Code" or "DateLiteral" or "Value" => String,
        "Keyword" or "Keywords" or "ControlFlow" or "ExceptionKeywords" or "GotoKeywords"
            or "ThisOrBaseReference" or "NullOrValueKeywords" or "ContextKeywords"
            or "ReferenceTypeKeywords" or "ValueTypeKeywords" or "OperatorKeywords"
            or "ParameterModifiers" or "Modifiers" or "Visibility" or "NamespaceKeywords"
            or "TrueFalse" or "SemanticKeywords" or "CompoundKeywords" or "This" or "LoopKeywords"
            or "JumpKeywords" or "ExceptionHandling" or "CheckedKeyword" or "UnsafeKeywords"
            or "GetSetAddRemove" or "AccessKeywords" or "SelectionStatements" or "IterationStatements"
            or "ExceptionHandlingStatements" or "JumpStatements" or "AccessModifiers"
            or "ControlStatements" or "FunctionKeywords" or "JavaScriptKeyWords" or "Friend"
            or "Namespace" or "Package" or "StrongEmphasis" => Keyword,
        "Number" or "NumberLiteral" or "Digits" or "Constant" or "Constants" or "Bool" or "Null"
            or "BooleanConstants" or "Literals" or "JavaScriptLiterals" => Number,
        "Variable" or "Variables" or "AttributeName" or "FieldName" or "Key" or "Entity"
            or "Entities" or "EntityReference" or "Attributes" or "UnknownAttribute" or "Property"
            or "Class" or "Emphasis" or "Warning" => Variable,
        "Function" or "MethodCall" or "MethodName" or "FunctionCall" or "Command" or "Preprocessor"
            or "Section" or "Heading" or "Heading1" or "Heading2" or "Heading3" or "Heading4"
            or "Heading5" or "Heading6" or "Success" or "JavaScriptGlobalFunctions"
            or "JavaScriptIntrinsics" or "Selector" or "AddedText" or "Image" => Function,
        "Type" or "TypeKeywords" or "XmlTag" or "CData" or "DocType" or "XmlDeclaration"
            or "TagName" or "ElementName" or "ValueTypes" or "ReferenceTypes" or "OtherTypes"
            or "Void" or "DataTypes" or "HtmlTag" or "Tags" or "ScriptTag" or "JavaScriptTag"
            or "JScriptTag" or "VBScriptTag" or "UnknownScriptTag" or "ASPSectionStartEndTags"
            or "Position" or "Header" or "FileName" or "Info" => Type,
        "Error" or "Errors" or "Invalid" or "RemovedText" or "BrokenEntity" => Error,
        "Link" or "Url" or "Hyperlink" => Link,
        "Punctuation" or "Operator" or "Operators" or "Text" or "Default" or "CurlyBraces"
            or "Colon" or "Slash" or "Assignment" or "LineBreak" or "ASPSection" or "UnchangedText" => Default,
        _ => null,
    };

    /// <summary>WCAG 相对对比度(1:1 ~ 21:1)。</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// 对比度不够就逐步向正文色混合,直到够为止(最坏情况即正文色本身,正文色恒满足 AA)。
    /// 只修"读不出来"的那几个,够的原样保留主题的色相。
    /// </summary>
    private static Color EnsureReadable(Color color, Color background, Color text)
    {
        for (int step = 0; step <= 10; step++)
        {
            Color candidate = Blend(text, color, step / 10.0);
            if (Contrast(candidate, background) >= MinimumContrast)
            {
                return candidate;
            }
        }
        return text;
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * Linearize(color.R)) + (0.7152 * Linearize(color.G)) + (0.0722 * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        double value = channel / 255.0;
        return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary><paramref name="ratio" /> 为 <paramref name="top" /> 的占比(0 = 全是 bottom)。</summary>
    private static Color Blend(Color top, Color bottom, double ratio) =>
        Color.FromRgb(
            (byte)Math.Round((top.R * ratio) + (bottom.R * (1 - ratio))),
            (byte)Math.Round((top.G * ratio) + (bottom.G * (1 - ratio))),
            (byte)Math.Round((top.B * ratio) + (bottom.B * (1 - ratio))));

    private static Color Parse(string hex) =>
        Color.TryParse(hex, out Color color)
            ? Color.FromRgb(color.R, color.G, color.B)
            : throw new ArgumentException($@"Invalid theme color literal: '{hex}'.", nameof(hex));
}
