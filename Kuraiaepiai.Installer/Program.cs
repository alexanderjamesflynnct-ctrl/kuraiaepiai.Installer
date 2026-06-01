using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("==================================================");
Console.WriteLine("クリアエーピーアイ (Kuriāēpīai) - SURGICAL Installer v26");
Console.WriteLine("==================================================");

string rootPath = Directory.GetCurrentDirectory();
string programCsPath = Path.Combine(rootPath, "Program.cs");

if (!File.Exists(programCsPath)) 
{ 
    Console.WriteLine("❌ Error: Program.cs not found. Run this in the API root folder."); 
    return; 
}

// 1. CONFLICT SCRUBBING
Console.WriteLine("\n🧹 Scrubbing old logic and braces...");
string content = File.ReadAllText(programCsPath);
content = Regex.Replace(content, @"// <kuria-usings-start>.*?// <kuria-usings-end>", "", RegexOptions.Singleline);
content = Regex.Replace(content, @"// <clearapi-start>.*?// <clearapi-end>", "", RegexOptions.Singleline);
content = Regex.Replace(content, @"if \(app\.Environment\.IsDevelopment\(\)\)\s*\{.*?/clearapi/push.*?\}", "", RegexOptions.Singleline);

// Remove trailing braces that break .NET 10 top-level files
content = content.Trim();
while (content.EndsWith("}")) 
{
    content = content.Substring(0, content.Length - 1).Trim();
}

// 2. INTERACTIVE CONFIGURATION (Now using the Prompt function)
string configPath = Path.Combine(rootPath, "kuraiaepiai.config.json");
bool proceedWithConfig = true;

if (File.Exists(configPath))
{
    Console.Write($"\n📄 Configuration already exists. Overwrite? (y/n): ");
    if (Console.ReadLine()?.ToLower() != "y") proceedWithConfig = false;
}

if (proceedWithConfig)
{
    Console.WriteLine("\n--- Enter API Metadata ---");
    var config = new {
        BusinessOwner = Prompt("Business Owner Name"),
        BusinessDept  = Prompt("Business Department"),
        ITOwner       = Prompt("IT Owner (Lead Dev)"),
        ITDept        = Prompt("IT Department"),
        SystemName    = Prompt("System Name (Japanese allowed)"),
        APIName       = Prompt("API Name")
    };
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    Console.WriteLine("✅ kuraiaepiai.config.json saved.");
}

// 3. INJECT TAGGED USINGS
string taggedUsings = @"
// <kuria-usings-start>
using kuraiaepiai.Source;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using System.Text.Json;
using System.IO;
using System.Text;
// <kuria-usings-end>
";
content = taggedUsings + content;

// 4. ENSURE CONTROLLER SUPPORT
if (!content.Contains("app.MapControllers()"))
    content = content.Replace("app.Run(", "app.MapControllers();\napp.Run(");

File.WriteAllText(programCsPath, content, Encoding.UTF8);

// 5. CREATE KURIA CONTROLLER
string controllersDir = Path.Combine(rootPath, "Controllers");
if (!Directory.Exists(controllersDir)) Directory.CreateDirectory(controllersDir);

File.WriteAllText(Path.Combine(controllersDir, "KuriaController.cs"), GetKuriaControllerCode(), Encoding.UTF8);

// 6. INJECT REPORTER
File.WriteAllText(Path.Combine(rootPath, "KuraiaepiaiReporter.cs"), GetReporterCode(), Encoding.UTF8);

// 7. CLEANUP & RESTORE (Now using the RunCommand function)
Console.WriteLine("\n♻️ Restoring project dependencies...");
RunCommand("dotnet", "add package Swashbuckle.AspNetCore");
RunCommand("dotnet", "restore");

Console.WriteLine("\n✅ Done. Visit /Kuria/push to initialize monitoring.");

// --- HELPER FUNCTIONS ---

string Prompt(string label)
{
    Console.Write($"{label}: ");
    return Console.ReadLine() ?? "Unknown";
}

void RunCommand(string cmd, string args)
{
    var startInfo = new ProcessStartInfo {
        FileName = cmd,
        Arguments = args,
        CreateNoWindow = true,
        UseShellExecute = false
    };
    var proc = Process.Start(startInfo);
    proc?.WaitForExit();
}

static string GetKuriaControllerCode()
{
    return @"using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;
using System.Text.Json;
using System.Text;
using kuraiaepiai.Source;

namespace kuraiaepiai.Controllers;

[ApiController]
[Route(""Kuria"")]
[Tags(""Kuriāēpīai-Managed"")]
public class KuriaController : ControllerBase
{
    private readonly ISwaggerProvider _swaggerProvider;
    public KuriaController(ISwaggerProvider swaggerProvider) => _swaggerProvider = swaggerProvider;

    [HttpGet(""push"")]
    public async Task<IActionResult> Push()
    {
        try {
            var doc = _swaggerProvider.GetSwagger(""v1"", null, ""/"");
            doc.Servers = new List<OpenApiServer> { new OpenApiServer { Url = $""{Request.Scheme}://{Request.Host}"" } };
            using var sw = new StringWriter();
            doc.SerializeAsV3(new Microsoft.OpenApi.OpenApiJsonWriter(sw));
            string json = sw.ToString();
            await System.IO.File.WriteAllTextAsync(""swagger.json"", json, Encoding.UTF8);
            var report = await (new KuraiaepiaiReporter()).GenerateReport(Directory.GetCurrentDirectory(), json, $""{Request.Scheme}://{Request.Host}"");
            using var client = new HttpClient();
            var response = await client.PostAsJsonAsync(""http://localhost:8000/api/collect"", report);
            return response.IsSuccessStatusCode ? Ok(""Synced Successfully!"") : BadRequest(""Collector rejected sync."");
        } catch (System.Exception ex) { return Problem(ex.Message); }
    }

    [HttpGet(""health"")]
    public IActionResult Health()
    {
        return Ok(new { 
            Status = ""Healthy"", 
            Message = ""This Kuriāēpīai Managed API is functioning normally."",
            Timestamp = System.DateTime.UtcNow,
            Port = Request.Host.Port,
            IP = HttpContext.Connection.RemoteIpAddress?.ToString()
        });
    }
}";
}

static string GetReporterCode() 
{ 
    return @"using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
namespace kuraiaepiai.Source;
public class KuraiaepiaiReporter {
    public async Task<object> GenerateReport(string projectPath, string swaggerJsonContent, string baseUrl) {
        var configText = File.ReadAllText(Path.Combine(projectPath, ""kuraiaepiai.config.json""), Encoding.UTF8);
        var config = JsonSerializer.Deserialize<ProjectConfig>(configText) ?? new ProjectConfig();
        var allFiles = Directory.GetFiles(projectPath, ""*.cs"", SearchOption.AllDirectories).Where(f => !f.Contains(""\\bin\\"") && !f.Contains(""\\obj\\"")).ToList();
        var dataFolder = Path.Combine(projectPath, ""Data"");
        var dataFiles = Directory.Exists(dataFolder) ? Directory.GetFiles(dataFolder, ""*.cs"").Select(File.ReadAllText).ToList() : new List<string>();
        var codeMap = new List<object>();
        foreach (var file in allFiles.Where(f => f.EndsWith(""Controller.cs""))) {
            var content = File.ReadAllText(file);
            string ctrl = Path.GetFileNameWithoutExtension(file);
            var matches = Regex.Matches(content, @""\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]"");
            foreach (Match m in matches) {
                var search = content.Substring(m.Index, Math.Min(400, content.Length - m.Index));
                var mm = Regex.Match(search, @""public\s+(?:async\s+)?(?:Task<|ActionResult<)?[\w\.<>\[\]\s]+\s+(\w+)\s*\("");
                if (mm.Success) {
                    string mName = mm.Groups[1].Value;
                    int bStart = content.IndexOf('{', m.Index);
                    string body = bStart != -1 ? content.Substring(bStart, Math.Min(1000, content.Length - bStart)) : """";
                    var dataCalls = Regex.Matches(body, @""\.(\w+)\s*\("").Select(mc => mc.Groups[1].Value).ToList();
                    var tables = new List<string>(); var sqlTypes = new List<string>(); var keywords = new[] { ""SELECT"",""UPDATE"",""INSERT"",""DELETE"" };
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
        return new { BaseUrl = baseUrl, ownership = new { config.BusinessOwner, config.BusinessDept, config.ITOwner, config.ITDept, config.SystemName, config.APIName, TotalLinesOfCode = allFiles.Sum(f => File.ReadAllLines(f).Length), TotalFiles = allFiles.Count },
            packages = Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Select(a => new { a.Name, Version = a.Version?.ToString() }),
            codeMap, swagger = JsonSerializer.Deserialize<object>(swaggerJsonContent) 
        };
    }
}
public class ProjectConfig { public string BusinessOwner { get; set; } = """"; public string BusinessDept { get; set; } = """"; public string ITOwner { get; set; } = """"; public string ITDept { get; set; } = """"; public string SystemName { get; set; } = """"; public string APIName { get; set; } = """"; }";
}