using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("==================================================");
Console.WriteLine("クリアエーピーアイ (Kuriāēpīai) - SURGICAL Installer v23");
Console.WriteLine("==================================================");

string rootPath = Directory.GetCurrentDirectory();
string programCsPath = Path.Combine(rootPath, "Program.cs");
if (!File.Exists(programCsPath)) return;

// 1. REFRESH REPORTER & CONFIG
File.WriteAllText(Path.Combine(rootPath, "KuraiaepiaiReporter.cs"), GetReporterCode(), Encoding.UTF8);

// 2. PATCH PROGRAM.CS
string content = File.ReadAllText(programCsPath);

// SURGERY: Remove ANY existing clearapi blocks or code containing the push route
content = Regex.Replace(content, @"// <clearapi-start>.*?// <clearapi-end>", "", RegexOptions.Singleline);
content = Regex.Replace(content, @"if \(app\.Environment\.IsDevelopment\(\)\)\s*\{.*?/clearapi/push.*?\}", "", RegexOptions.Singleline);

// 3. INJECT CLEAN USINGS
var usings = new[] { "using kuraiaepiai.Source;", "using Microsoft.OpenApi;", "using Swashbuckle.AspNetCore.Swagger;", "using System.Text.Json;", "using System.IO;", "using System.Text;" };
foreach (var u in usings) if (!content.Contains(u)) content = u + "\n" + content;

// 4. DEFINE NEW SYNC BLOCK
string syncEndpoint = @"
// <clearapi-start>
if (app.Environment.IsDevelopment())
{
    app.UseCors(""KuraiaepiaiPolicy"");
    app.MapGet(""/clearapi/push"", async (HttpContext context) => {
        try {
            string jsonContent = """";
            var swaggerProvider = context.RequestServices.GetService<ISwaggerProvider>();
            if (swaggerProvider != null) {
                var doc = swaggerProvider.GetSwagger(""v1"", null, ""/"");
                doc.Servers = new List<OpenApiServer> { new OpenApiServer { Url = $""{context.Request.Scheme}://{context.Request.Host}"" } };
                using var sw = new StringWriter();
                doc.SerializeAsV3(new OpenApiJsonWriter(sw));
                jsonContent = sw.ToString();
            } else {
                using var client = new HttpClient();
                jsonContent = await client.GetStringAsync($""{context.Request.Scheme}://{context.Request.Host}/openapi/v1.json"");
            }
            await File.WriteAllTextAsync(""swagger.json"", jsonContent, Encoding.UTF8);
            var report = await (new KuraiaepiaiReporter()).GenerateReport(Directory.GetCurrentDirectory(), jsonContent);
            using var client2 = new HttpClient();
            var response = await client2.PostAsJsonAsync(""http://localhost:8000/api/collect"", report);
            return response.IsSuccessStatusCode ? Results.Ok(""Synced!"") : Results.BadRequest(""Sync failed."");
        } catch (Exception ex) { return Results.Problem(ex.Message); }
    });
}
// <clearapi-end>
";

// 5. INJECT BEFORE RUN AND SANITIZE BRACES
content = Regex.Replace(content, @"app\.Run\(.*?\);", syncEndpoint + "$0");

// Remove illegal dangling braces
content = content.Trim();
while (content.EndsWith("}")) {
    content = content.Substring(0, content.Length - 1).Trim();
}

File.WriteAllText(programCsPath, content, Encoding.UTF8);
Console.WriteLine("✅ VoiceAPI Sanitized and Patched.");

string Prompt(string l) { Console.Write($"{l}: "); return Console.ReadLine() ?? "Unknown"; }

static string GetReporterCode() {
    return @"using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
namespace kuraiaepiai.Source;
public class KuraiaepiaiReporter {
    public async Task<object> GenerateReport(string projectPath, string swaggerJsonContent) {
        var config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(Path.Combine(projectPath, ""kuraiaepiai.config.json""), Encoding.UTF8)) ?? new ProjectConfig();
        var allFiles = Directory.GetFiles(projectPath, ""*.cs"", SearchOption.AllDirectories).Where(f => !f.Contains(""\\bin\\"") && !f.Contains(""\\obj\\"")).ToList();
        var dataFolder = Path.Combine(projectPath, ""Data"");
        var dataFiles = Directory.Exists(dataFolder) ? Directory.GetFiles(dataFolder, ""*.cs"").Select(File.ReadAllText).ToList() : new List<string>();
        var codeMap = new List<object>();
        foreach (var file in allFiles.Where(f => f.EndsWith(""Controller.cs""))) {
            var content = File.ReadAllText(file);
            string ctrl = Path.GetFileNameWithoutExtension(file);
            var matches = Regex.Matches(content, @""\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]"");
            foreach (Match m in matches) {
                var mm = Regex.Match(content.Substring(m.Index, Math.Min(400, content.Length - m.Index)), @""public\s+(?:async\s+)?(?:Task<|ActionResult<)?[\w\.<>\[\]\s]+\s+(\w+)\s*\("");
                if (mm.Success) {
                    string mName = mm.Groups[1].Value;
                    int bStart = content.IndexOf('{', m.Index);
                    string body = bStart != -1 ? content.Substring(bStart, Math.Min(1200, content.Length - bStart)) : """";
                    var dataCalls = Regex.Matches(body, @""\.(\w+)\s*\("").Select(mc => mc.Groups[1].Value).ToList();
                    var tables = new List<string>();
                    var sqlTypes = new List<string>();
                    var keywords = new[] { ""SELECT"",""UPDATE"",""INSERT"",""DELETE"" };
                    foreach(var dc in dataFiles) {
                        foreach(var dm in dataCalls) {
                            if (dc.Contains("" "" + dm + ""("")) {
                                foreach(var k in keywords) if(dc.ToUpper().Contains(k)) sqlTypes.Add(k);
                                var sm = Regex.Matches(dc, @""(?i)(?:FROM|JOIN|UPDATE|INTO)\s+([\[\]\w\d\._]+)"");
                                tables.AddRange(sm.Select(t => t.Groups[1].Value.Trim('[', ']', ' ', '""')).Where(t => !keywords.Contains(t.ToUpper()) && t.ToUpper() != ""VALUES""));
                            }
                        }
                    }
                    codeMap.Add(new { Controller = ctrl, MethodName = mName, Verb = m.Groups[1].Value.ToUpper(), SqlType = sqlTypes.Distinct().ToList(), TargetTables = tables.Distinct().ToList() });
                }
            }
        }
        return new { 
            ownership = new { config.BusinessOwner, config.BusinessDept, config.ITOwner, config.ITDept, config.SystemName, config.APIName, TotalLinesOfCode = allFiles.Sum(f => File.ReadAllLines(f).Length), TotalFiles = allFiles.Count },
            packages = Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Select(a => new { a.Name, Version = a.Version?.ToString() }),
            codeMap, swagger = JsonSerializer.Deserialize<object>(swaggerJsonContent) 
        };
    }
}
public class ProjectConfig { public string BusinessOwner { get; set; } = """"; public string BusinessDept { get; set; } = """"; public string ITOwner { get; set; } = """"; public string ITDept { get; set; } = """"; public string SystemName { get; set; } = """"; public string APIName { get; set; } = """"; }";
}