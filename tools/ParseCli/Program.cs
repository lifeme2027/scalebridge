using System.Text;
using ScaleBridge.ParseCli;

// 输出一律 UTF-8（无 BOM），换行 \n，方便管道与比对。
var utf8 = new UTF8Encoding(false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n", AutoFlush = true };
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
return Cli.Run(args, stdin, stdout, stderr);
