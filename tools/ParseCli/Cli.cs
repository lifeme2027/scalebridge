using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace ScaleBridge.ParseCli;

/// <summary>命令行入口逻辑（与进程无关，便于测试）。退出码：0 成功；1 --compare 发现差异；2 用法/输入错误。</summary>
public static class Cli
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver(), Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, TypeInfoResolver = new DefaultJsonTypeInfoResolver(), Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string Usage = """
        scalebridge-parse — 用 ScaleBridge.Core 的标准解析器解析 GATT 字节（实验性：仅按 SIG 规范实现，未真机验证）

        单条:
          scalebridge-parse --service 181D --char 2A9D --hex "00 59 36"
        stdin（每行一条；空行与 # 开头的行忽略）:
          echo "181D/2A9D 00 59 36" | scalebridge-parse --stdin
          echo "00 59 36"           | scalebridge-parse --stdin --service 181D --char 2A9D
        向量文件（scalebridge-vectors/1）:
          scalebridge-parse --vectors shared-vectors/weight-scale.json              # 输出同格式文档，里面是实际解析结果
          scalebridge-parse --vectors my-vectors.json --compare                     # 与文件里的 reject/errorCode/warnings/expect 比较，列出差异，有差异退出码 1

        选项:
          --service <UUID>  16 位服务 UUID（181D / 181B），可带 0x
          --char <UUID>     16 位特征 UUID（2A9D / 2A9E / 2A9C / 2A9B）
          --hex <字节>      十六进制，字节间可用空格 : - , 分隔，也可连写
          --stdin           从标准输入逐行读取；输出 JSON Lines（每行一个紧凑 JSON）
          --vectors <文件>  读 scalebridge-vectors/1 文件，可重复
          --compare         与向量里的期望比较（仅配合 --vectors）；输出 {"total","matched","skipped","mismatches":[…]}
          --details         成功的解析额外输出顶层 "details"：测量的 Flags 原始值、Feature 的 32 位 Raw（shared-vectors 的 expect 里没有这些；
                            默认输出不含，与 Web 版逐字节一致；--compare 时忽略 details）
          --compact         单条/向量文档输出紧凑 JSON（默认缩进）
          -h, --help        显示本帮助

        输出字段与 shared-vectors 的向量同形：reject、errorCode（拒绝时，另附 errorMessage）、warnings 与 expect（成功时）。
        带 frameType（厂商广播类）的向量不解析：输出 {"unsupported":true,…}，--compare 里计入 "skipped"。十进制数一律输出为字符串。支持的特征：181D/2A9E 181D/2A9D 181B/2A9B 181B/2A9C。退出码：0 成功，1 比较有差异，2 用法或输入错误。
        """;

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string? service = null, characteristic = null, hex = null;
        var useStdin = false;
        var compare = false;
        var compact = false;
        var details = false;
        var files = new List<string>();

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "-h" or "--help": stdout.WriteLine(Usage); return 0;
                    case "--service": service = Next(); break;
                    case "--char" or "--characteristic": characteristic = Next(); break;
                    case "--hex": hex = Next(); break;
                    case "--stdin": useStdin = true; break;
                    case "--vectors": files.Add(Next()); break;
                    case "--compare": compare = true; break;
                    case "--compact": compact = true; break;
                    case "--details": details = true; break;
                    default: throw new ArgumentException($"unknown argument '{args[i]}'");
                }
            }

            var modes = (hex is not null ? 1 : 0) + (useStdin ? 1 : 0) + (files.Count > 0 ? 1 : 0);
            if (modes != 1) throw new ArgumentException("choose exactly one of --hex, --stdin, --vectors (see --help)");
            if (compare && files.Count == 0) throw new ArgumentException("--compare needs --vectors");
        }
        catch (ArgumentException e)
        {
            stderr.WriteLine("error: " + e.Message);
            stderr.WriteLine("try --help");
            return 2;
        }

        var opts = compact ? Compact : Pretty;
        try
        {
            if (hex is not null)
            {
                if (service is null || characteristic is null) throw new ArgumentException("--hex needs --service and --char");
                var data = Parsing.ParseHex(hex);
                var o = Parsing.Parse(service, characteristic, data);
                stdout.WriteLine(Parsing.ToVectorJson(null, service, characteristic, data, o, details).ToJsonString(opts));
                return 0;
            }
            if (useStdin) return RunStdin(stdin, stdout, stderr, service, characteristic, details);
            return RunFiles(files, compare, details, opts, stdout, stderr);
        }
        catch (UnauthorizedAccessException e)
        {
            // 没有读取权限（或路径是目录/被系统拒绝）：友好提示 + 退出码 2，而不是未处理异常。
            stderr.WriteLine("error: cannot read the input (permission denied or not a regular file): " + e.Message);
            return 2;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException or IOException or JsonException)
        {
            stderr.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    private static int RunStdin(TextReader stdin, TextWriter stdout, TextWriter stderr, string? defService, string? defChar, bool details)
    {
        var status = 0;
        string? line;
        var n = 0;
        while ((line = stdin.ReadLine()) is not null)
        {
            n++;
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            string? service = defService, characteristic = defChar, hexPart = t;
            var sp = t.IndexOf(' ');
            var head = sp < 0 ? t : t[..sp];
            if (head.Contains('/'))
            {
                var parts = head.Split('/');
                service = parts[0];
                characteristic = parts.Length > 1 ? parts[1] : null;
                hexPart = sp < 0 ? "" : t[(sp + 1)..];
            }
            try
            {
                if (service is null || characteristic is null) throw new ArgumentException("no service/characteristic (use --service/--char or 'SERVICE/CHAR hex' per line)");
                var data = Parsing.ParseHex(hexPart);
                var o = Parsing.Parse(service, characteristic, data);
                stdout.WriteLine(Parsing.ToVectorJson(null, service, characteristic, data, o, details).ToJsonString(Compact));
            }
            catch (Exception e) when (e is ArgumentException or FormatException or NotSupportedException)
            {
                // 每一行都输出一个结果对象，行号对得上；错误行输出 cliError，整体退出码 2。
                stdout.WriteLine(new JsonObject { ["line"] = n, ["cliError"] = e.Message }.ToJsonString(Compact));
                stderr.WriteLine($"line {n}: {e.Message}");
                status = 2;
            }
        }
        return status;
    }

    private static int RunFiles(List<string> files, bool compare, bool details, JsonSerializerOptions opts, TextWriter stdout, TextWriter stderr)
    {
        var total = 0;
        var matched = 0;
        var skipped = 0;
        var mismatches = new JsonArray();
        var docs = new List<JsonObject>();

        foreach (var file in files)
        {
            if (Directory.Exists(file))
                throw new ArgumentException($"--vectors expects a vectors .json file, but '{file}' is a directory (e.g. pass shared-vectors/weight-scale.json; repeat --vectors for several files)");
            var doc = JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new FormatException($"{file}: not a JSON object");
            if (doc["format"]?.GetValue<string>() != "scalebridge-vectors/1")
                throw new FormatException($"{file}: format must be \"scalebridge-vectors/1\"");
            var outVectors = new JsonArray();
            foreach (var node in doc["vectors"]?.AsArray() ?? throw new FormatException($"{file}: missing 'vectors'"))
            {
                var v = node!.AsObject();
                var name = v["name"]?.GetValue<string>();
                if (Parsing.IsVendorVector(v))
                {
                    // 与 Web 版一致：厂商广播类向量不解析，输出 unsupported 并计入 skipped（不是错误）。
                    outVectors.Add(Parsing.UnsupportedVendor(name));
                    skipped++;
                    continue;
                }
                var service = v["service"]?.GetValue<string>() ?? throw new FormatException($"{file}: vector {name} has no service");
                var characteristic = v["characteristic"]?.GetValue<string>() ?? throw new FormatException($"{file}: vector {name} has no characteristic");
                var data = Parsing.ParseHex(v["hex"]?.GetValue<string>() ?? "");
                var o = Parsing.Parse(service, characteristic, data);
                outVectors.Add(Parsing.ToVectorJson(name, service, characteristic, data, o, details));
                total++;
                if (!compare) continue;
                var diff = Parsing.Compare(v, o);
                if (diff is null) matched++;
                else mismatches.Add(new JsonObject { ["file"] = Path.GetFileName(file), ["name"] = name, ["hex"] = Parsing.FormatHex(data), ["difference"] = diff });
            }
            docs.Add(new JsonObject
            {
                ["format"] = "scalebridge-vectors/1",
                ["suite"] = doc["suite"]?.GetValue<string>(),
                ["description"] = "Actual output of scalebridge-parse (ScaleBridge.Core) for " + Path.GetFileName(file),
                ["vectors"] = outVectors,
            });
        }

        if (compare)
        {
            stdout.WriteLine(new JsonObject { ["total"] = total, ["matched"] = matched, ["skipped"] = skipped, ["mismatches"] = mismatches }.ToJsonString(opts));
            return mismatches.Count == 0 ? 0 : 1;
        }
        if (docs.Count == 1) stdout.WriteLine(docs[0].ToJsonString(opts));
        else stdout.WriteLine(new JsonArray(docs.Select(d => (JsonNode?)d).ToArray()).ToJsonString(opts));
        return 0;
    }
}
