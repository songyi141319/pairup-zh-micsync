using System.Reflection;
using System.Text.Json;
using PairUp.App;

var flags = BindingFlags.NonPublic | BindingFlags.Static;
object? Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(null, args);
void Require(bool success, string detail) { if (!success) throw new Exception(detail); }
var passed = 0;
void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS: " + name); }
bool Rejected(string method, params object[] args) { try { Invoke(method, args); return false; } catch (TargetInvocationException) { return true; } }
Check("only the local main document may send commands", () =>
{
    Require((bool)Invoke("IsGlassDocument", "https://shengyu.local/index.html?demo=1")!, "preview should be allowed");
    foreach (var url in new[] { "http://shengyu.local/index.html", "https://shengyu.local.evil/index.html", "https://evil.test/index.html", "file:///index.html", "https://shengyu.local:8000/index.html", "https://shengyu.local/other.html", "not a URL" })
        Require(!(bool)Invoke("IsGlassDocument", url)!, "allowed " + url);
});
Check("volume delay and trim require finite bounded JSON numbers", () =>
{
    using var valid = JsonDocument.Parse("187.5");
    Require((double)Invoke("Number", valid.RootElement, 0d, 3000d)! == 187.5, "precision lost");
    foreach (var raw in new[] { "-1", "3001", "1e999", "\"30\"", "null", "true" })
    { using var doc = JsonDocument.Parse(raw); Require(Rejected("Number", doc.RootElement, 0d, 3000d), "accepted " + raw); }
});
Check("spatial protocol rejects invalid roles and reversed bands", () =>
{
    foreach (var raw in new[] {
        "{\"role\":\"Unknown\",\"lowCutHz\":0,\"highCutHz\":0,\"trimDb\":0}",
        "{\"role\":\"999\",\"lowCutHz\":0,\"highCutHz\":0,\"trimDb\":0}",
        "{\"role\":\"Bass\",\"lowCutHz\":120,\"highCutHz\":80,\"trimDb\":0}",
        "{\"role\":\"Bass\",\"lowCutHz\":10,\"highCutHz\":120,\"trimDb\":0}",
        "{\"role\":\"Bass\",\"lowCutHz\":0,\"highCutHz\":120,\"trimDb\":1}" })
    { using var doc = JsonDocument.Parse(raw); Require(Rejected("ReadSpatial", doc.RootElement), "invalid profile accepted"); }
});
Check("valid low frequency profile is accepted", () =>
{
    using var doc = JsonDocument.Parse("{\"role\":\"Bass\",\"lowCutHz\":30,\"highCutHz\":100,\"trimDb\":-6}");
    Require(Invoke("ReadSpatial", doc.RootElement) is not null, "valid profile rejected");
});
Console.WriteLine($"Results: {passed} passed. No windows or audio devices opened.");
