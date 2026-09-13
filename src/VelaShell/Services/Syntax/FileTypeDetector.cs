namespace VelaShell.Services.Syntax;

/// <summary>
/// 按文件名与首行内容判定语法类型,返回语法定义名(见 <see cref="SyntaxHighlightingService" />)。
/// <para>
/// 四级判定,依次尝试:特殊文件名 → 所在目录(nginx)→ 扩展名 → shebang。
/// 最后一级对远端编辑尤其重要:服务器上大量可执行脚本是**没有扩展名**的
/// (<c>/usr/local/bin/deploy</c>、<c>/etc/cron.daily/logrotate</c>),
/// 只有首行的 <c>#!</c> 能说明它是什么。
/// </para>
/// </summary>
public static class FileTypeDetector
{
    /// <summary>扩展名(小写,含点)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // 运维日常:AvaloniaEdit 未内置,由本项目的 xshd 提供。
        [".sh"] = SyntaxNames.Shell,
        [".bash"] = SyntaxNames.Shell,
        [".zsh"] = SyntaxNames.Shell,
        [".ksh"] = SyntaxNames.Shell,
        [".profile"] = SyntaxNames.Shell,
        [".bashrc"] = SyntaxNames.Shell,
        [".yaml"] = SyntaxNames.Yaml,
        [".yml"] = SyntaxNames.Yaml,
        [".ini"] = SyntaxNames.Ini,
        [".conf"] = SyntaxNames.Ini,
        [".cfg"] = SyntaxNames.Ini,
        [".cnf"] = SyntaxNames.Ini,
        [".properties"] = SyntaxNames.Ini,
        [".env"] = SyntaxNames.Ini,
        [".repo"] = SyntaxNames.Ini,
        [".desktop"] = SyntaxNames.Ini,
        // systemd 单元文件:都是 [Section] + Key=Value。
        [".service"] = SyntaxNames.Ini,
        [".timer"] = SyntaxNames.Ini,
        [".socket"] = SyntaxNames.Ini,
        [".mount"] = SyntaxNames.Ini,
        [".path"] = SyntaxNames.Ini,
        [".log"] = SyntaxNames.Log,
        [".nginx"] = SyntaxNames.Nginx,
        [".toml"] = SyntaxNames.Toml,
        [".mk"] = SyntaxNames.Makefile,
        [".mak"] = SyntaxNames.Makefile,
        [".go"] = SyntaxNames.Go,
        [".rs"] = SyntaxNames.Rust,
        [".lua"] = SyntaxNames.Lua,
        [".rb"] = SyntaxNames.Ruby,
        [".rake"] = SyntaxNames.Ruby,
        [".gemspec"] = SyntaxNames.Ruby,
        [".pl"] = SyntaxNames.Perl,
        [".pm"] = SyntaxNames.Perl,
        [".sql"] = SyntaxNames.Sql,
        [".tf"] = SyntaxNames.Hcl,
        [".tfvars"] = SyntaxNames.Hcl,
        [".hcl"] = SyntaxNames.Hcl,

        // 以下由 AvaloniaEdit 内置定义提供(名称须与其 Name 一致)。
        [".json"] = "Json",
        [".jsonc"] = "Json",
        [".json5"] = "Json",
        [".jsonl"] = "Json",
        [".ndjson"] = "Json",
        [".xml"] = "XML",
        [".xsd"] = "XML",
        [".xsl"] = "XML",
        [".xslt"] = "XML",
        [".wsdl"] = "XML",
        [".config"] = "XML",
        [".csproj"] = "XML",
        [".fsproj"] = "XML",
        [".vbproj"] = "XML",
        [".props"] = "XML",
        [".targets"] = "XML",
        [".nuspec"] = "XML",
        [".resx"] = "XML",
        [".xaml"] = "XML",
        [".axaml"] = "XML",
        [".slnx"] = "XML",
        [".plist"] = "XML",
        [".svg"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".xhtml"] = "HTML",
        [".vue"] = "HTML",
        [".svelte"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "CSS",
        [".less"] = "CSS",
        [".js"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".ts"] = "JavaScript",
        [".tsx"] = "JavaScript",
        [".mts"] = "JavaScript",
        [".cts"] = "JavaScript",
        [".py"] = "Python",
        [".pyw"] = "Python",
        [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell",
        [".psd1"] = "PowerShell",
        [".md"] = "MarkDown",
        [".markdown"] = "MarkDown",
        [".cs"] = "C#",
        [".c"] = "C++",
        [".h"] = "C++",
        [".cpp"] = "C++",
        [".hpp"] = "C++",
        [".cc"] = "C++",
        [".cxx"] = "C++",
        [".hh"] = "C++",
        [".hxx"] = "C++",
        [".ino"] = "C++",
        [".java"] = "Java",
        [".php"] = "PHP",
        [".patch"] = "Patch",
        [".diff"] = "Patch",
        // .tex 刻意不映射:AvaloniaEdit 12 内置的 TeX 定义自身是坏的(加载即抛 "Could not find main RuleSet"),
        // 映射过去只会在编辑器里静默退化成纯文本 —— 不如直接说"不认识"。
        [".vb"] = "VB",
    };

    /// <summary>判定器可能返回的全部定义名(回归用例逐个核对它们都能真的加载)。</summary>
    /// <remarks>
    /// 必须是按需计算的 getter,不能写成 <c>{ get; } =</c> 初始化器:静态字段按源码顺序初始化,
    /// 而它排在 <see cref="ByFileName" /> / <see cref="ByInterpreter" /> 之前 —— 那样初始化时
    /// 两张表还是 null,整个类型初始化器抛出,所有判定一起失效。
    /// </remarks>
    internal static IReadOnlySet<string> KnownDefinitionNames =>
        ByExtension.Values.Concat(ByFileName.Values).Concat(ByInterpreter.Values)
                   .Append(SyntaxNames.Nginx)
                   .ToHashSet(StringComparer.Ordinal);

    /// <summary>无扩展名但含义明确的文件名(小写)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = SyntaxNames.Dockerfile,
        ["containerfile"] = SyntaxNames.Dockerfile,
        ["makefile"] = SyntaxNames.Makefile,
        ["gnumakefile"] = SyntaxNames.Makefile,
        [".bashrc"] = SyntaxNames.Shell,
        [".bash_profile"] = SyntaxNames.Shell,
        [".bash_aliases"] = SyntaxNames.Shell,
        [".zshrc"] = SyntaxNames.Shell,
        [".profile"] = SyntaxNames.Shell,
        ["crontab"] = SyntaxNames.Shell,

        // /etc 下的常见配置,绝大多数是 key=value 或 key value 风格。
        ["sshd_config"] = SyntaxNames.Ini,
        ["ssh_config"] = SyntaxNames.Ini,
        ["my.cnf"] = SyntaxNames.Ini,
        ["redis.conf"] = SyntaxNames.Ini,
        ["fstab"] = SyntaxNames.Ini,
        ["hosts"] = SyntaxNames.Ini,
        ["resolv.conf"] = SyntaxNames.Ini,
        ["known_hosts"] = SyntaxNames.Ini,
        ["authorized_keys"] = SyntaxNames.Ini,
        [".gitconfig"] = SyntaxNames.Ini,
        [".npmrc"] = SyntaxNames.Ini,
        [".editorconfig"] = SyntaxNames.Ini,
        [".htaccess"] = SyntaxNames.Ini,

        ["nginx.conf"] = SyntaxNames.Nginx,

        ["docker-compose.yml"] = SyntaxNames.Yaml,
        ["docker-compose.yaml"] = SyntaxNames.Yaml,

        ["cargo.lock"] = SyntaxNames.Toml,
        ["pipfile"] = SyntaxNames.Toml,
        ["poetry.lock"] = SyntaxNames.Toml,

        ["gemfile"] = SyntaxNames.Ruby,
        ["rakefile"] = SyntaxNames.Ruby,
        ["vagrantfile"] = SyntaxNames.Ruby,
        ["guardfile"] = SyntaxNames.Ruby,

        [".terraformrc"] = SyntaxNames.Hcl,
    };

    /// <summary>shebang 解释器名(小写)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByInterpreter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sh"] = SyntaxNames.Shell,
        ["bash"] = SyntaxNames.Shell,
        ["zsh"] = SyntaxNames.Shell,
        ["ksh"] = SyntaxNames.Shell,
        ["dash"] = SyntaxNames.Shell,
        ["ash"] = SyntaxNames.Shell,
        ["python"] = "Python",
        ["python2"] = "Python",
        ["python3"] = "Python",
        ["pwsh"] = "PowerShell",
        ["powershell"] = "PowerShell",
        ["node"] = "JavaScript",
        ["php"] = "PHP",
        ["ruby"] = SyntaxNames.Ruby,
        ["perl"] = SyntaxNames.Perl,
        ["lua"] = SyntaxNames.Lua,
        ["luajit"] = SyntaxNames.Lua,
        ["make"] = SyntaxNames.Makefile,
    };

    /// <summary>
    /// 判定语法定义名;无法判定时返回 <c>null</c>(调用方应表现为纯文本)。
    /// </summary>
    /// <param name="fileName">文件名或完整路径(目录只用于 nginx 这类"按所在目录认"的判定)。</param>
    /// <param name="firstLine">文件首行,用于 shebang 判定;为空则跳过该级。</param>
    public static string? Detect(string? fileName, string? firstLine = null)
    {
        string name = GetName(fileName);

        // 完整文件名优先于扩展名:docker-compose.yml 既命中 ".yml" 也命中全名,
        // nginx.conf 更是"扩展名说 Ini、全名说 Nginx",必须全名先说话。
        if (name.Length > 0 && ByFileName.TryGetValue(name, out string? byName))
        {
            return byName;
        }
        string extension = Path.GetExtension(name);
        if (IsNginxConfig(fileName, extension))
        {
            return SyntaxNames.Nginx;
        }
        if (extension.Length > 0 && ByExtension.TryGetValue(extension, out string? byExtension))
        {
            return byExtension;
        }
        return DetectByShebang(firstLine);
    }

    /// <summary>
    /// nginx 的站点配置散在 <c>conf.d/*.conf</c> 与 <c>sites-available/</c> 下,文件名千奇百怪
    /// (<c>default</c>、<c>api.example.com</c>),只有"在 nginx 目录里"这件事是共同的。
    /// 按目录认,才不会把它们当成 key=value 的 Ini 着色。
    /// </summary>
    private static bool IsNginxConfig(string? path, string extension)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        string normalized = path.Replace('\\', '/');
        if (!normalized.Contains("/nginx/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return extension.Equals(".conf", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/sites-available/", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("/sites-enabled/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析 shebang。同时处理直接形式 <c>#!/bin/bash</c> 与 env 形式
    /// <c>#!/usr/bin/env python3</c>,后者真正的解释器在第二个词上。
    /// </summary>
    private static string? DetectByShebang(string? firstLine)
    {
        if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }
        string[] parts = firstLine[2..]
                         .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        string interpreter = LastSegment(parts[0]);

        // env 形式:真正的解释器是它后面那个词(还要跳过 -S / -i 之类的选项)。
        if (interpreter.Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            string? next = parts.Skip(1).FirstOrDefault(p => !p.StartsWith('-') && !p.Contains('='));
            if (next is null)
            {
                return null;
            }
            interpreter = LastSegment(next);
        }
        string stripped = StripVersionSuffix(interpreter);

        // python3.11 → python3 表里有;lua5.4 → lua5 表里没有,再剥掉尾部数字落到 lua。
        return ByInterpreter.GetValueOrDefault(stripped)
               ?? ByInterpreter.GetValueOrDefault(stripped.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'));
    }

    /// <summary>取路径最后一段(shebang 里写的是绝对路径)。</summary>
    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>python3.11 → python3;表里已收 python/python2/python3。</summary>
    private static string StripVersionSuffix(string interpreter)
    {
        int dot = interpreter.IndexOf('.');
        return dot > 0 ? interpreter[..dot] : interpreter;
    }

    private static string GetName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        // 远端是 unix 路径,本地可能是 windows 路径 —— 两种分隔符都要认。
        int slash = fileName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? fileName[(slash + 1)..] : fileName;
    }
}

/// <summary>本项目自带的语法定义名(AvaloniaEdit 未内置的那些)。</summary>
public static class SyntaxNames
{
    /// <summary>Shell / Bash 脚本。</summary>
    public const string Shell = "Shell";

    /// <summary>YAML。</summary>
    public const string Yaml = "YAML";

    /// <summary>INI / conf / properties 风格的配置。</summary>
    public const string Ini = "Ini";

    /// <summary>Dockerfile。</summary>
    public const string Dockerfile = "Dockerfile";

    /// <summary>日志文件(按级别着色)。</summary>
    public const string Log = "Log";

    /// <summary>nginx 配置(块 + 指令 + $变量)。</summary>
    public const string Nginx = "Nginx";

    /// <summary>TOML(Cargo.toml、pyproject.toml……)。</summary>
    public const string Toml = "TOML";

    /// <summary>Makefile。</summary>
    public const string Makefile = "Makefile";

    /// <summary>Go。</summary>
    public const string Go = "Go";

    /// <summary>Rust。</summary>
    public const string Rust = "Rust";

    /// <summary>Lua(OpenResty / Redis 脚本 / Neovim 配置)。</summary>
    public const string Lua = "Lua";

    /// <summary>Ruby(Gemfile、Vagrantfile、Chef/Puppet 周边)。</summary>
    public const string Ruby = "Ruby";

    /// <summary>Perl。</summary>
    public const string Perl = "Perl";

    /// <summary>通用 SQL(MySQL / PostgreSQL / SQLite 的公共子集)。</summary>
    public const string Sql = "SQL";

    /// <summary>HCL / Terraform。</summary>
    public const string Hcl = "HCL";
}
