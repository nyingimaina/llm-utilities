#r "src/FReader/bin/Release/net10.0/FReader.dll"
#r "src/FReader/bin/Release/net10.0/Microsoft.CodeAnalysis.dll"
#r "src/FReader/bin/Release/net10.0/Microsoft.CodeAnalysis.CSharp.dll"

using FReader;
using System.Text.Json;

var path = @"src/FReader.Tests/TestFixtures/SampleClass.cs";
var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

// Task 1: read full file
var readResult = LineEngine.ReadLines(path, null, null, null);
var readJson = JsonSerializer.Serialize(readResult.Data, json);
Console.WriteLine($"TASK 1 - read (full file): {readJson.Length} chars, 1 call");

// Task 2: read_function (medium - ProcessAsync)
var funcs = ProviderFactory.GetFunctions(path, "ProcessAsync");
var f = funcs[0];
var fnResult = LineEngine.ReadLines(path, f.LineStart, f.LineEnd, truncate: 0);
var fnData = new Dictionary<string, object?> { ["_name"] = f.Name, ["_sig"] = f.Signature, ["_line_start"] = f.LineStart, ["_line_end"] = f.LineEnd, ["_r"] = fnResult.Data?["_r"] };
var fnJson = JsonSerializer.Serialize(fnData, json);
Console.WriteLine($"TASK 2 - read_function(ProcessAsync): {fnJson.Length} chars, 1 call");

// Task 3: read_function (small - Add(int,int))
var addFuncs = ProviderFactory.GetFunctions(path, "Add");
var addF = addFuncs.FirstOrDefault(af => af.Name == "Add" && af.Signature.Contains("(int a, int b)"));
if (addF == null) addF = addFuncs[0];
var addResult = LineEngine.ReadLines(path, addF.LineStart, addF.LineEnd, truncate: 0);
var addData = new Dictionary<string, object?> { ["_name"] = addF.Name, ["_sig"] = addF.Signature, ["_line_start"] = addF.LineStart, ["_line_end"] = addF.LineEnd, ["_r"] = addResult.Data?["_r"] };
var addJson = JsonSerializer.Serialize(addData, json);
Console.WriteLine($"TASK 3 - read_function(Add): {addJson.Length} chars, 1 call");

var total = readJson.Length + fnJson.Length + addJson.Length;
Console.WriteLine($"---");
Console.WriteLine($"TOTAL: {total} chars, 3 calls");
Console.WriteLine($"Target: ≤ 5,450 chars, 3 calls");
Console.WriteLine(total <= 5450 ? "PASS" : "FAIL");
