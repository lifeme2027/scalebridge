using System.Text.Json.Nodes;
using ScaleBridge.ParseCli;

namespace ScaleBridge.Core.Tests;

/// <summary>tools/ParseCli：单条、stdin、向量文件三种入口；输出与 shared-vectors 同形。</summary>
public class ParseCliTests
{
    private static (int Code, string Out, string Err) Run(string? stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = Cli.Run(args, new StringReader(stdin ?? ""), o, e);
        return (code, o.ToString().Replace("\r\n", "\n"), e.ToString());
    }

    private static string VectorsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "shared-vectors"))) d = d.Parent;
        return Path.Combine(d!.FullName, "shared-vectors");
    }

    [Fact]
    public void Single_hex_prints_vector_shaped_json_with_decimal_strings()
    {
        var (code, output, _) = Run(null, "--service", "181D", "--char", "2A9D", "--hex", "00 59 36", "--compact");
        Assert.Equal(0, code);
        var j = JsonNode.Parse(output)!.AsObject();
        Assert.False(j["reject"]!.GetValue<bool>());
        Assert.Empty(j["warnings"]!.AsArray());
        Assert.Equal("69.565", j["expect"]!["weight"]!["kilograms"]!.GetValue<string>());
        Assert.Equal(13913, j["expect"]!["weight"]!["raw"]!.GetValue<int>());
        Assert.Equal("181D", j["service"]!.GetValue<string>());
        Assert.Equal("00 59 36", j["hex"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0x181d", "0x2a9d", "005936")]
    [InlineData("181d", "2a9d", "00:59:36")]
    [InlineData("181D", "2A9D", "00-59-36")]
    [InlineData("181D", "2A9D", "00,59,36")]
    public void Uuid_and_hex_spelling_variants_are_equivalent(string service, string ch, string hex)
    {
        var a = Run(null, "--service", service, "--char", ch, "--hex", hex, "--compact");
        var b = Run(null, "--service", "181D", "--char", "2A9D", "--hex", "00 59 36", "--compact");
        Assert.Equal(0, a.Code);
        Assert.Equal(b.Out, a.Out);
    }

    [Fact]
    public void Rejections_carry_error_code()
    {
        var (code, output, _) = Run(null, "--service", "181D", "--char", "2A9D", "--hex", "0E E2 36", "--compact");
        Assert.Equal(0, code);
        var j = JsonNode.Parse(output)!;
        Assert.True(j["reject"]!.GetValue<bool>());
        Assert.Equal("Truncated", j["errorCode"]!.GetValue<string>());
        Assert.Null(j["expect"]);
    }

    [Fact]
    public void Empty_hex_is_a_valid_empty_packet_and_is_rejected_as_too_short()
    {
        var (code, output, _) = Run(null, "--service", "181B", "--char", "2A9C", "--hex", "", "--compact");
        Assert.Equal(0, code);
        Assert.Equal("TooShort", JsonNode.Parse(output)!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public void Warnings_are_reported()
    {
        // flags 0x10：保留位 bit4 置位。
        var (_, output, _) = Run(null, "--service", "181D", "--char", "2A9D", "--hex", "10 E2 36", "--compact");
        var j = JsonNode.Parse(output)!;
        Assert.Equal(new[] { "ReservedBitsSet" }, j["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Equal("70.250", j["expect"]!["weight"]!["kilograms"]!.GetValue<string>());
    }

    [Fact]
    public void Stdin_gives_one_compact_json_per_line_and_skips_blanks_and_comments()
    {
        var input = "# comment\n\n181D/2A9D 00 59 36\n181B/2A9C 00 00 EA 00\n00 FF FF\n";
        var (code, output, _) = Run(input, "--stdin", "--service", "181D", "--char", "2A9D");
        Assert.Equal(0, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.DoesNotContain("\n", l));
        Assert.Equal("23.4", JsonNode.Parse(lines[1])!["expect"]!["bodyFatPercent"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(lines[2])!["expect"]!["measurementUnsuccessful"]!.GetValue<bool>());
    }

    [Fact]
    public void Stdin_bad_line_reports_cliError_with_line_number_and_exit_2_but_continues()
    {
        var (code, output, err) = Run("181D/2A9D zz\n181D/2A9D 00 59 36\n9999/0000 00\n181D/2A9D 0\n", "--stdin");
        Assert.Equal(2, code);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(1, lines[0]["line"]!.GetValue<int>());
        Assert.NotNull(lines[0]["cliError"]);
        Assert.Equal("69.565", lines[1]["expect"]!["weight"]!["kilograms"]!.GetValue<string>());
        Assert.NotNull(lines[2]["cliError"]);
        Assert.Contains("odd number", lines[3]["cliError"]!.GetValue<string>());
        Assert.Contains("line 1", err);
    }

    [Fact]
    public void Stdin_line_without_uuids_and_no_defaults_is_an_input_error()
    {
        var (code, output, _) = Run("00 59 36\n", "--stdin");
        Assert.Equal(2, code);
        Assert.Contains("cliError", output);
    }

    [Fact]
    public void Vectors_file_output_is_a_vectors_document_with_actual_results()
    {
        var (code, output, _) = Run(null, "--vectors", Path.Combine(VectorsDir(), "weight-scale.json"));
        Assert.Equal(0, code);
        var doc = JsonNode.Parse(output)!.AsObject();
        Assert.Equal("scalebridge-vectors/1", doc["format"]!.GetValue<string>());
        Assert.Equal("weight-scale", doc["suite"]!.GetValue<string>());
        var n = doc["vectors"]!.AsArray().Count;
        Assert.True(n >= 100);
        Assert.All(doc["vectors"]!.AsArray(), v => Assert.NotNull(v!["name"]));
    }

    [Fact]
    public void Compare_over_all_shared_vectors_has_no_mismatches()
    {
        var dir = VectorsDir();
        var (code, output, _) = Run(null, "--vectors", Path.Combine(dir, "weight-scale.json"),
            "--vectors", Path.Combine(dir, "body-composition.json"), "--compare", "--compact");
        Assert.Equal(0, code);
        var j = JsonNode.Parse(output)!;
        Assert.Equal(235, j["total"]!.GetValue<int>());
        Assert.Equal(235, j["matched"]!.GetValue<int>());
        Assert.Empty(j["mismatches"]!.AsArray());
    }

    [Fact]
    public void Compare_reports_differences_and_exits_1()
    {
        var path = Path.Combine(Path.GetTempPath(), "sb-parsecli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
            {"format":"scalebridge-vectors/1","suite":"t","vectors":[
              {"name":"ok","service":"181D","characteristic":"2A9D","hex":"00 59 36","reject":false,"warnings":[],
               "expect":{"unit":"si","measurementUnsuccessful":false,"weight":{"raw":13913,"original":"69.5650","kilograms":"69.565"},"timestamp":null,"userId":null,"bmi":null,"height":null}},
              {"name":"wrong-weight","service":"181D","characteristic":"2A9D","hex":"00 59 36","reject":false,"warnings":[],
               "expect":{"unit":"si","measurementUnsuccessful":false,"weight":{"raw":13913,"original":"69.5","kilograms":"69.5"},"timestamp":null,"userId":null,"bmi":null,"height":null}},
              {"name":"wrong-reject","service":"181D","characteristic":"2A9D","hex":"00 59 36","reject":true,"errorCode":"TooShort"},
              {"name":"wrong-code","service":"181D","characteristic":"2A9D","hex":"00","reject":true,"errorCode":"Truncated"},
              {"name":"wrong-warning","service":"181D","characteristic":"2A9D","hex":"10 E2 36","reject":false,"warnings":[],
               "expect":{"unit":"si","measurementUnsuccessful":false,"weight":{"raw":14050,"original":"70.250","kilograms":"70.250"},"timestamp":null,"userId":null,"bmi":null,"height":null}}
            ]}
            """);
            var (code, output, _) = Run(null, "--vectors", path, "--compare", "--compact");
            Assert.Equal(1, code);
            var j = JsonNode.Parse(output)!;
            Assert.Equal(5, j["total"]!.GetValue<int>());
            Assert.Equal(1, j["matched"]!.GetValue<int>());
            var names = j["mismatches"]!.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToList();
            Assert.Equal(new[] { "wrong-weight", "wrong-reject", "wrong-code", "wrong-warning" }, names);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("--hex", "00")]                                     // 缺 service/char
    [InlineData("--service", "181D")]                               // 没有模式
    [InlineData("--bogus")]
    [InlineData("--compare")]
    [InlineData("--service", "181D", "--char", "2A9D", "--hex", "00", "--stdin")] // 两种模式
    [InlineData("--service", "181D", "--char", "2A9D", "--hex", "0")] // 奇数位
    [InlineData("--service", "181D", "--char", "2A9D", "--hex", "xx")]
    [InlineData("--service", "1800", "--char", "2A00", "--hex", "00")] // 不支持的特征
    [InlineData("--vectors", "/definitely/not/here.json")]
    public void Bad_usage_exits_2_with_message_on_stderr(params string[] args)
    {
        var (code, output, err) = Run(null, args);
        Assert.Equal(2, code);
        Assert.Equal("", output);
        Assert.StartsWith("error:", err);
    }

    [Fact]
    public void Help_exits_0()
    {
        var (code, output, _) = Run(null, "--help");
        Assert.Equal(0, code);
        Assert.Contains("--vectors", output);
    }

    [Fact]
    public void Wrong_format_file_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "sb-parsecli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"format":"other/9","vectors":[]}""");
            var (code, _, err) = Run(null, "--vectors", path);
            Assert.Equal(2, code);
            Assert.Contains("scalebridge-vectors/1", err);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Every_shared_vector_roundtrips_through_the_cli_output_shape()
    {
        // 把 CLI 对每条向量的输出当作「期望」再比较一次：保证输出字段集合足以自我验证（同形）。
        var dir = VectorsDir();
        foreach (var file in new[] { "weight-scale.json", "body-composition.json" })
        {
            var (code, output, _) = Run(null, "--vectors", Path.Combine(dir, file), "--compact");
            Assert.Equal(0, code);
            var tmp = Path.Combine(Path.GetTempPath(), "sb-rt-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmp, output);
                var cmp = Run(null, "--vectors", tmp, "--compare", "--compact");
                Assert.Equal(0, cmp.Code);
                Assert.Empty(JsonNode.Parse(cmp.Out)!["mismatches"]!.AsArray());
            }
            finally { File.Delete(tmp); }
        }
    }
}
