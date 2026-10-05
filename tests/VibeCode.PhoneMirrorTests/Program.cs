using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using VibeCode.Services;

// Exercise the published immutable transcript shape without constructing the desktop or touching user data.
const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
var mirror = (PhoneBridgeMirror)RuntimeHelpers.GetUninitializedObject(typeof(PhoneBridgeMirror));
typeof(PhoneBridgeMirror).GetField("_gate", hidden)!.SetValue(mirror, new object());
var field = typeof(PhoneBridgeMirror).GetField("_chats", hidden)!;
var chats = (IDictionary)Activator.CreateInstance(field.FieldType)!;
field.SetValue(mirror, chats);
var chatType = typeof(PhoneBridgeMirror).GetNestedType("ChatMirror", BindingFlags.NonPublic)!;
var chat = Activator.CreateInstance(chatType, nonPublic: true)!;
void Set(string name, object value) => chatType.GetField(name)!.SetValue(chat, value);
Set("Summary", "{\"id\":\"fixture\"}");
Set("Detail", "{}");
Set("Version", 3);
Set("Items", new List<string> { "{\"k\":\"user\",\"t\":\"first\"}", "{\"k\":\"assistant\",\"t\":\"last\"}" });
Set("Changes", new List<(int Version, int MinIndex)> { (1, 0), (2, 1), (3, 2) });
Set("Full", true);
chats["fixture"] = chat;
var checks = 0;
void Require(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
JsonNode Read(int version) => JsonNode.Parse(mirror.MessagesJson("fixture", version)!)!;

var initial = Read(-1);
Require(initial["base"]!.GetValue<int>() == 0 && initial["items"]!.AsArray().Count == 2, "Initial snapshot must include every mirrored row.");
var restarted = Read(80);
Require(restarted["base"]!.GetValue<int>() == 0 && restarted["items"]!.AsArray().Count == 2,
    "A client from a previous server lifetime must receive a full transcript, not an empty tail.");
var streaming = Read(1);
Require(streaming["base"]!.GetValue<int>() == 1 && streaming["items"]!.AsArray().Count == 1, "Streaming updates must replace the changed suffix.");
var detailOnly = Read(2);
Require(detailOnly["base"]!.GetValue<int>() == 2 && detailOnly["items"]!.AsArray().Count == 0, "Detail-only updates must retain the transcript.");
Require(mirror.MessagesJson("fixture", 3) is null, "Current clients should long poll.");
Require(mirror.MessagesJson("missing", -1) is null, "Unknown chats must not return another transcript.");
Console.WriteLine($"PASS: {checks} phone transcript mirror regression checks; no desktop initialization or real user data.");
