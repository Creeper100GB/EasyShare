using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyShare.Core.Crypto;

var testDir = Path.Combine(Path.GetTempPath(), "EasyShare-LiveTest");
var saveDir = Path.Combine(testDir, "received");
if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
Directory.CreateDirectory(saveDir);

var port = 54321;
using var cert = TlsCertificate.LoadOrCreate();
var fingerprint = TlsCertificate.GetFingerprint(cert);

Console.WriteLine($"[Setup] Zertifikat geladen, Fingerprint: {fingerprint.Substring(0, 16)}...");

var server = new EasyShare.Transport.Server.LocalSendServer(cert);
server.Start(port, alias: "LiveTest", fingerprint: fingerprint, savePath: saveDir);

bool rejectNext = false;
server.UploadRequested += (_, e) =>
{
    if (rejectNext) server.RejectUpload(e.SessionId);
    else server.AcceptUpload(e.SessionId, saveDir);
};

for (int i = 0; i < 30; i++)
{
    try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port); Console.WriteLine("[Setup] Server bereit"); break; }
    catch { Thread.Sleep(200); }
}

var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

int passed = 0, total = 0;

// TEST 1-5: Endpoints
total++;
try
{
    var resp = await client.GetStringAsync($"https://127.0.0.1:{port}/api/localsend/v2/info");
    var j = JsonDocument.Parse(resp);
    var alias = j.RootElement.GetProperty("alias").GetString();
    var portInfo = j.RootElement.GetProperty("port").GetInt32();
    Console.WriteLine($"[TEST 1] /info GET: alias={alias} port={portInfo} PASS");
    passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 1] /info GET: FAIL - {ex.Message}"); }

total++;
try
{
    var resp = await client.GetStringAsync($"https://127.0.0.1:{port}/api/localsend/v2/register");
    Console.WriteLine($"[TEST 2] /register GET: {resp.Substring(0, Math.Min(80, resp.Length))}... PASS");
    passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 2] /register GET: FAIL - {ex.Message}"); }

total++;
try
{
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/register", null);
    resp.EnsureSuccessStatusCode();
    Console.WriteLine("[TEST 3] /register POST: PASS");
    passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 3] /register POST: FAIL - {ex.Message}"); }

total++;
try
{
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/cancel", new StringContent($"{{\"sessionId\":\"notexistent\"}}", Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();
    Console.WriteLine("[TEST 4] /cancel POST: PASS");
    passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 4] /cancel POST: FAIL - {ex.Message}"); }

total++;
try
{
    var page = await client.GetStringAsync($"https://127.0.0.1:{port}/");
    var hasUpload = page.Contains("upload");
    Console.WriteLine($"[TEST 5] Browser-Seite /: {page.Length} bytes - {(hasUpload ? "PASS" : "CHECK")}");
    if (hasUpload) passed++;
    else Console.WriteLine("  WARN: Kein 'upload' in Response");
}
catch (Exception ex) { Console.WriteLine($"[TEST 5] Browser-Seite /: FAIL - {ex.Message}"); }

// TEST 6: Reject (prepare-upload -> 403)
total++;
try
{
    var body = BuildPrepareBody(fingerprint, port, "t6", "reject.bin", 1024);
    rejectNext = true;
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/prepare-upload", content);
    rejectNext = false;
    var sc = (int)resp.StatusCode;
    var keepResult = sc == 403 ? "PASS" : "FAIL";
    Console.WriteLine($"[TEST 6] Reject: HTTP {sc} - {keepResult}");
    if (sc == 403) passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 6] Reject: FAIL - {ex.Message}"); }

// TEST 7: 1MB Upload
total++;
try
{
    var (path, hash) = CreateTestFile("1mb.bin", 1 * 1024 * 1024);
    var sw = Stopwatch.StartNew();
    var result = await SendAndVerify("7_1mb", path, hash, port, fingerprint, server, client, saveDir, compress: false, sw);
    Console.WriteLine($"[TEST 7] 1MB Upload: {sw.ElapsedMilliseconds}ms, {result.Speed:F1} MB/s - {result.PassText}");
    if (result.Pass) passed++;
    File.Delete(path);
}
catch (Exception ex) { Console.WriteLine($"[TEST 7] 1MB Upload: FAIL - {ex.GetType().Name}: {ex.Message}"); }

// TEST 8: 100MB Upload
total++;
try
{
    var (path, hash) = CreateTestFile("100mb.bin", 100 * 1024 * 1024);
    var sw = Stopwatch.StartNew();
    var result = await SendAndVerify("8_100mb", path, hash, port, fingerprint, server, client, saveDir, compress: false, sw);
    Console.WriteLine($"[TEST 8] 100MB Upload: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F1}s), {result.Speed:F1} MB/s - {result.PassText}");
    if (result.Pass) passed++;
    File.Delete(path);
}
catch (Exception ex) { Console.WriteLine($"[TEST 8] 100MB Upload: FAIL - {ex.GetType().Name}: {ex.Message}"); }

// TEST 9: 1GB Upload
total++;
try
{
    var (path, hash) = CreateTestFile("1gb.bin", 1024L * 1024 * 1024);
    var sw = Stopwatch.StartNew();
    var result = await SendAndVerify("9_1gb", path, hash, port, fingerprint, server, client, saveDir, compress: false, sw);
    Console.WriteLine($"[TEST 9] 1GB Upload: {sw.ElapsedMilliseconds}ms ({sw.Elapsed.TotalSeconds:F1}s), {result.Speed:F1} MB/s - {result.PassText}");
    if (result.Pass) passed++;
    File.Delete(path);
}
catch (Exception ex) { Console.WriteLine($"[TEST 9] 1GB Upload: FAIL - {ex.GetType().Name}: {ex.Message}"); }

// TEST 10: Zero-Byte Upload
total++;
try
{
    var zeroPath = Path.Combine(testDir, "zero.bin");
    File.WriteAllBytes(zeroPath, []);
    var sw = Stopwatch.StartNew();
    var result = await SendAndVerify("10_zero", zeroPath, "", port, fingerprint, server, client, saveDir, compress: false, sw);
    Console.WriteLine($"[TEST 10] Zero-Byte: {result.PassText}");
    if (result.Pass) passed++;
}
catch (Exception ex) { Console.WriteLine($"[TEST 10] Zero-Byte: FAIL - {ex.GetType().Name}: {ex.Message}"); }

// TEST 11: Cancel mid-upload (200MB)
total++;
try
{
    const long cancelSize = 200L * 1024 * 1024;
    var (path, _) = CreateTestFile("cancel.bin", cancelSize);
    var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bool cancelled = false;
    var cts = new CancellationTokenSource();

    server.UploadProgress += (_, e) => { if (!uploadStarted.Task.IsCompleted && e.BytesReceived > 4096) uploadStarted.TrySetResult(); };
    server.UploadCancelled += (_, e) => cancelled = true;

    var body = BuildPrepareBody(fingerprint, port, "c11", "cancel.bin", cancelSize);
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/prepare-upload", content);
    resp.EnsureSuccessStatusCode();
    var prep = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var sessionId = prep.RootElement.GetProperty("sessionId").GetString()!;
    var token = prep.RootElement.GetProperty("files").GetProperty("c11").GetString()!;

    using var fs = File.OpenRead(path);
    var form = CreateMultipart(sessionId, "c11", token, fs, "cancel.bin");
    var uploadTask = client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/upload", form, cts.Token);

    await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));

    using var cancel = new StringContent($"{{\"sessionId\":\"{sessionId}\"}}", Encoding.UTF8, "application/json");
    await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/cancel", cancel);

    try { await uploadTask; }
    catch (HttpRequestException ex) when ((int?)ex.StatusCode is 499 or 403) { }
    catch (OperationCanceledException) { }
    catch { }

    await Task.Delay(2000);
    var partialExists = File.Exists(Path.Combine(saveDir, "cancel.bin"));
    Console.WriteLine($"[TEST 11] Cancel: Cancelled={cancelled}, PartialFileExists={partialExists}");
    if (cancelled && !partialExists) { Console.WriteLine("  PASS"); passed++; }
    else { Console.WriteLine($"  FAIL: partial={partialExists}"); }
}
catch (Exception ex) { Console.WriteLine($"[TEST 11] Cancel: FAIL - {ex.GetType().Name}: {ex.Message}"); }

// TEST 12: 3x50MB Multi-File
total++;
try
{
    var files = new List<(string path, string hash)>();
    for (int i = 0; i < 3; i++)
    {
        var (p, h) = CreateTestFile($"mf{i}.bin", 50 * 1024 * 1024 + i * 1024);
        files.Add((p, h));
    }

    var multiCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    server.UploadCompleted += (_, e) => multiCompleted.TrySetResult(true);

    var filesDict = new Dictionary<string, object>();
    for (int i = 0; i < files.Count; i++)
        filesDict[$"mf{i}"] = new { id = $"mf{i}", fileName = $"mf{i}.bin", size = new FileInfo(files[i].path).Length, fileType = "application/octet-stream" };

    var body = BuildPrepareBody(fingerprint, port, "m12", null, 0, compressed: false, originalFileCount: 3,
        filesDict: filesDict);
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/prepare-upload", content);
    resp.EnsureSuccessStatusCode();
    var prep = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var sid = prep.RootElement.GetProperty("sessionId").GetString()!;

    for (int i = 0; i < files.Count; i++)
    {
        var tok = prep.RootElement.GetProperty("files").GetProperty($"mf{i}").GetString()!;
        using var fs = File.OpenRead(files[i].path);
        var respI = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/upload",
            CreateMultipart(sid, $"mf{i}", tok, fs, $"mf{i}.bin"));
        respI.EnsureSuccessStatusCode();
    }

    await multiCompleted.Task.WaitAsync(TimeSpan.FromSeconds(180));
    var allMatch = true;
    foreach (var (p, h) in files)
    {
        var rp = Path.Combine(saveDir, Path.GetFileName(p));
        if (!File.Exists(rp)) { allMatch = false; continue; }
        if (HashFile(rp) != h) { allMatch = false; Console.WriteLine($"  MISMATCH: {Path.GetFileName(p)}"); }
    }
    Console.WriteLine($"[TEST 12] Multi-File 3x50MB: AllMatch={allMatch} PASS");
    if (allMatch) passed++;

    foreach (var f in files) File.Delete(f.path);
}
catch (Exception ex) { Console.WriteLine($"[TEST 12] Multi-File: FAIL - {ex.GetType().Name}: {ex.Message}"); }

server.Stop();
Console.WriteLine();
Console.WriteLine($"========== ERGEBNIS: {passed}/{total} Tests ==========");
if (passed < total) Console.WriteLine($"FEHLER: {total - passed} Tests fehlgeschlagen!");

(string path, string hash) CreateTestFile(string name, long size)
{
    var path = Path.Combine(testDir, name);
    using (var s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
    {
        var buf = new byte[1 << 20];
        var rng = RandomNumberGenerator.Create();
        long rem = size;
        while (rem > 0)
        {
            int c = (int)Math.Min(buf.Length, rem);
            rng.GetBytes(buf); s.Write(buf, 0, c); rem -= c;
        }
    }
    return (path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
}

static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

static async Task<(bool Pass, string PassText, double Speed)> SendAndVerify(
    string id, string filePath, string expectedHash, int port, string fingerprint,
    EasyShare.Transport.Server.LocalSendServer server, HttpClient client, string saveDir,
    bool compress, Stopwatch sw)
{
    var fileName = Path.GetFileName(filePath);
    var fileSize = new FileInfo(filePath).Length;

    var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    string receivedPath = "";
    server.UploadCompleted += (_, e) => { receivedPath = e.SavePath; completed.TrySetResult(true); };

    var body = BuildPrepareBody(fingerprint, port, id, fileName, fileSize, compressed: compress, originalFileCount: 1);
    using var content = new StringContent(body, Encoding.UTF8, "application/json");
    var resp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/prepare-upload", content);
    resp.EnsureSuccessStatusCode();
    var prep = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var sid = prep.RootElement.GetProperty("sessionId").GetString()!;
    var tok = prep.RootElement.GetProperty("files").GetProperty(id).GetString()!;

    using var fs = File.OpenRead(filePath);
    var form = CreateMultipart(sid, id, tok, fs, fileName);
    var uploadResp = await client.PostAsync($"https://127.0.0.1:{port}/api/localsend/v2/upload", form);
    uploadResp.EnsureSuccessStatusCode();
    fs.Dispose();

    var ok = await completed.Task.WaitAsync(sw.ElapsedMilliseconds > 30000 ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
    sw.Stop();

    var match = string.IsNullOrEmpty(expectedHash) || HashFile(receivedPath) == expectedHash;
    var speed = fileSize / 1048576.0 / sw.Elapsed.TotalSeconds;
    var passText = match && ok ? "PASS" : $"FAIL (match={match}, completed={ok}, path={receivedPath})";
    return (match && ok, passText, speed);
}

static string BuildPrepareBody(string fingerprint, int port, string fileId, string? fileName = null, long fileSize = 0,
    bool compressed = false, int originalFileCount = 1,
    Dictionary<string, object>? filesDict = null)
{
    var files = filesDict ?? new Dictionary<string, object> { { fileId, new { id = fileId, fileName, size = fileSize, fileType = "application/octet-stream" } } };
    return JsonSerializer.Serialize(new
    {
        info = new { alias = "TestSender", version = "2.0", deviceModel = "TestPC", deviceType = "desktop", fingerprint, port, protocol = "https", download = true, announce = false },
        files,
        compressed,
        originalFileCount
    });
}

static MultipartFormDataContent CreateMultipart(string sessionId, string fileId, string token, Stream file, string fileName)
{
    var form = new MultipartFormDataContent();
    form.Add(new StringContent(sessionId), "sessionId");
    form.Add(new StringContent(fileId), "fileId");
    form.Add(new StringContent(token), "token");
    form.Add(new StreamContent(file), "file", fileName);
    return form;
}

Console.WriteLine($"[Cleanup] Loesche Testdateien...");
try { Directory.Delete(testDir, true); } catch { }
