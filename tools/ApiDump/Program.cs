using System.Reflection;
using System.Text;

var asmName = args.Length > 0 ? args[0] : "Microsoft.Agents.AI";
var filter = args.Length > 1 ? args[1] : null;
var dir = Path.GetDirectoryName(typeof(Microsoft.Agents.AI.AIAgent).Assembly.Location)!;
var asm = Assembly.LoadFrom(Path.Combine(dir, asmName + ".dll"));
var sb = new StringBuilder();

foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
{
    if (filter is not null && !t.FullName!.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    sb.AppendLine($"TYPE {t.FullName}");
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        sb.AppendLine($"    {m}");
}
Console.WriteLine(sb.ToString());
