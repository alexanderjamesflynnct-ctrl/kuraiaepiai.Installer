using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("==================================================");
Console.WriteLine("クリアエーピーアイ (Kuriāēpīai) - PRO Installer v18");
Console.WriteLine("==================================================");

string rootPath = Directory.GetCurrentDirectory();
string programCsPath = Path.Combine(rootPath, "Program.cs");
string[] csprojFiles = Directory.GetFiles(rootPath, "*.csproj");

if (!File.Exists(programCsPath) || csprojFiles.Length == 0)
{
    Console.WriteLine("❌ Error: Program.cs or .csproj not found.");
    return;
}

string csprojPath = csprojFiles[0];

// 1. STABLE FOUNDATION: Clean .csproj and ensure Swashbuckle v10
Console.WriteLine("\n🧹 Cleaning .csproj and ensuring .NET 10 compatibility...");
string csprojContent = File.ReadAllText(csprojPath);
csprojContent = Regex.Replace(csprojContent, @"<PackageReference [^>]*Swashbuckle[^>]*/>", "");
csprojContent = Regex.Replace(csprojContent, @"<PackageReference [^>]*Microsoft\.OpenApi[^>]*/>", "");
if (!csprojContent.Contains("DisableMicrosoftAspNetCoreOpenApiSourceGenerator"))
{
    string generatorFix = "\n    <DisableMicrosoftAspNetCoreOpenApiSourceGenerator>true</DisableMicrosoftAspNetCoreOpenApiSourceGenerator>\n    <OpenApiGenerateDocuments>false</OpenApiGenerateDocuments>\n    <NoWarn>$(NoWarn);NU1605</NoWarn>";
    csprojContent = csprojContent.Replace("</PropertyGroup>", generatorFix + "\n  </PropertyGroup>");
}
File.WriteAllText(csprojPath, csprojContent);
RunCommand("dotnet", "add package Swashbuckle.AspNetCore");

// 2. CONFIGURATION
string configPath = Path.Combine(rootPath, "kuraiaepiai.config.json");
if (!File.Exists(configPath))
{
    var config = new {
        BusinessOwner = Prompt("Business Owner"), BusinessDept = Prompt("Department"),
        ITOwner = Prompt("IT Owner"), ITDept = Prompt("IT Dept"),
        SystemName = Prompt("System Name"), APIName = Prompt("API Name")
    };
    File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
}

// 3. INJECT REPORTER (V18 - Data Layer Tracing Engine)
File.WriteAllText(Path.Combine(rootPath, "KuraiaepiaiReporter.cs"), GetReporterCode());

// 4. PATCH PROGRAM.CS (Ensuring .NET 10 Namespaces stay correct)
Console.WriteLine("⚙️ Patching Program.cs...");
string content = File.ReadAllText(programCsPath);
content = content.Replace("using Microsoft.OpenApi.Models;", "").Replace("using Microsoft.OpenApi.Writers;", "").Replace("using Microsoft.OpenApi.Extensions;", "");

var usings = new[] { "using kuraiaepiai.Source;", "using Microsoft.OpenApi;", "using Swashbuckle.AspNetCore.Swagger;", "using System.Text.Json;", "using System.IO;" };
foreach (var u in usings) if (!content.Contains(u)) content = u + "\n" + content;

if (!content.Contains("AddSwaggerGen"))
    content = content.Replace("var builder = WebApplication.CreateBuilder(args);", "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddEndpointsApiExplorer();\nbuilder.Services.AddSwaggerGen();");

if (!content.Contains("app.UseSwagger()"))
    content = content.Replace("var app = builder.Build();", "var app = builder.Build();\n\napp.UseSwagger();\napp.UseSwaggerUI();");

// Cleanup and Inject Sync Endpoint
content = Regex.Replace(content, @"if \(app\.Environment\.IsDevelopment\(\)\)\s*\{\s*app\.UseCors\(""KuraiaepiaiPolicy""\);\s*app\.MapGet\(""/clearapi/push"".*?\}\);", "", RegexOptions.Singleline);
content = Regex.Replace(content, @"app\.MapGet\(""/clearapi/push"".*?\}\);", "", RegexOptions.Singleline);

if (!content.Contains("AddCors"))
    content = content.Replace("var builder = WebApplication.CreateBuilder(args);", "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddCors(options => { options.AddPolicy(\"KuraiaepiaiPolicy\", p => p.WithOrigins(\"http://localhost:5173\").AllowAnyHeader().AllowAnyMethod()); });");

string syncEndpoint = @"
if (app.Environment.IsDevelopment())
{
    app.UseCors(""KuraiaepiaiPolicy"");
    app.MapGet(""/clearapi/push"", async (HttpContext context) => {
        try {
            var swaggerProvider = context.RequestServices.GetRequiredService<ISwaggerProvider>();
            var swaggerDoc = swaggerProvider.GetSwagger(""v1"", null, ""/"");
            var host = context.Request.Host.Value;
            var scheme = context.Request.Scheme;
            swaggerDoc.Servers = new List<OpenApiServer> { new OpenApiServer { Url = $""{scheme}://{host}"" } };
            using var sw = new StringWriter();
            var writer = new OpenApiJsonWriter(sw);
            swaggerDoc.SerializeAsV3(writer);
            var jsonContent = sw.ToString();
            await File.WriteAllTextAsync(""swagger.json"", jsonContent);
            var report = await (new KuraiaepiaiReporter()).GenerateReport(Directory.GetCurrentDirectory(), jsonContent);
            using var client = new HttpClient();
            var response = await client.PostAsJsonAsync(""http://localhost:8000/api/collect"", report);
            return response.IsSuccessStatusCode ? Results.Ok(""Synced!"") : Results.BadRequest(""Sync failed."");
        } catch (Exception ex) { return Results.Problem(ex.Message); }
    });
}
";
content = content.Replace("app.Run();", syncEndpoint + "\napp.Run();");
File.WriteAllText(programCsPath, content);

RunCommand("dotnet", "restore");
Console.WriteLine("\n✅ Done. Please build and visit /clearapi/push.");

void RunCommand(string cmd, string args) { var p = Process.Start(new ProcessStartInfo { FileName = cmd, Arguments = args, CreateNoWindow = true }); p?.WaitForExit(); }
string Prompt(string l) { Console.Write($"{l}: "); return Console.ReadLine() ?? "Unknown"; }

static string GetReporterCode() {
    return @"using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace kuraiaepiai.Source;
public class KuraiaepiaiReporter {
    public async Task<object> GenerateReport(string projectPath, string swaggerJsonContent) {
        var config = JsonSerializer.Deserialize<ProjectConfig>(File.ReadAllText(Path.Combine(projectPath, ""kuraiaepiai.config.json""))) ?? new ProjectConfig();
        var allFiles = Directory.GetFiles(projectPath, ""*.cs"", SearchOption.AllDirectories).Where(f => !f.Contains(""\\bin\\"") && !f.Contains(""\\obj\\"")).ToList();
        
        // Load Data Layer contents
        var dataFolder = Path.Combine(projectPath, ""Data"");
        var dataFilesContent = Directory.Exists(dataFolder) 
            ? Directory.GetFiles(dataFolder, ""*.cs"").Select(File.ReadAllText).ToList() 
            : new List<string>();

        var codeMap = new List<object>();
        foreach (var file in allFiles.Where(f => f.EndsWith(""Controller.cs""))) {
            var content = File.ReadAllText(file);
            var matches = Regex.Matches(content, @""\[Http(Get|Post|Put|Delete|Patch)[^\]]*\]"");
            foreach (Match m in matches) {
                var search = content.Substring(m.Index, Math.Min(400, content.Length - m.Index));
                var mm = Regex.Match(search, @""public\s+(?:async\s+)?(?:Task<|ActionResult<)?[\w\.<>\[\]\s]+\s+(\w+)\s*\("");
                if (mm.Success) {
                    string mName = mm.Groups[1].Value;
                    int bStart = content.IndexOf('{', m.Index);
                    string controllerBody = bStart != -1 ? content.Substring(bStart, Math.Min(1000, content.Length - bStart)) : """";
                    
                    var dataMethodCalls = Regex.Matches(controllerBody, @""\.(\w+)\s*\("").Select(mc => mc.Groups[1].Value).ToList();
                    var tablesFound = new List<string>();
                    var sqlTypes = new List<string>();

                    foreach(var dataMethod in dataMethodCalls) {
                        foreach(var dataContent in dataFilesContent) {
                            if (dataContent.Contains("" "" + dataMethod + ""("")) {
                                var sqlKeywords = new[] { ""SELECT"", ""UPDATE"", ""INSERT"", ""DELETE"" };
                                foreach(var k in sqlKeywords) if(dataContent.ToUpper().Contains(k)) sqlTypes.Add(k);
                                var sqlMatches = Regex.Matches(dataContent, @""(?i)(?:FROM|JOIN|UPDATE|INTO)\s+([\[\]\w\d\._]+)"");
                                tablesFound.AddRange(sqlMatches.Select(t => t.Groups[1].Value.Trim('[', ']', ' ', '""'))
                                    .Where(t => !sqlKeywords.Contains(t.ToUpper()) && t.ToUpper() != ""VALUES""));
                            }
                        }
                    }

                    codeMap.Add(new { MethodName = mName, Verb = m.Groups[1].Value.ToUpper(), SqlType = sqlTypes.Distinct().ToList(), TargetTables = tablesFound.Distinct().ToList() });
                }
            }
        }
        return new { 
            ownership = new { config.BusinessOwner, config.BusinessDept, config.ITOwner, config.ITDept, config.SystemName, config.APIName, TotalLinesOfCode = allFiles.Sum(f => File.ReadAllLines(f).Length), TotalFiles = allFiles.Count },
            packages = Assembly.GetEntryAssembly()?.GetReferencedAssemblies().Select(a => new { Name = a.Name, Version = a.Version?.ToString() }),
            codeMap, swagger = JsonSerializer.Deserialize<object>(swaggerJsonContent) 
        };
    }
}
public class ProjectConfig { public string BusinessOwner { get; set; } = """"; public string BusinessDept { get; set; } = """"; public string ITOwner { get; set; } = """"; public string ITDept { get; set; } = """"; public string SystemName { get; set; } = """"; public string APIName { get; set; } = """"; }";
}