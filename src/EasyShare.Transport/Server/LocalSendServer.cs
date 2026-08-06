using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using EasyShare.Core;
using EasyShare.Core.Models;

namespace EasyShare.Transport.Server;

public class UploadRequestEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public DeviceInfo Sender { get; set; } = new();
    public List<FileEntry> Files { get; set; } = new();
    public string Fingerprint { get; set; } = string.Empty;
}

public class UploadProgressEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
}

public class UploadCancelledEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public class UploadCompletedEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string SavePath { get; set; } = string.Empty;
}

public class LocalSendServer
{
    public event EventHandler<UploadRequestEventArgs>? UploadRequested;
    public event EventHandler<UploadProgressEventArgs>? UploadProgress;
    public event EventHandler<UploadCancelledEventArgs>? UploadCancelled;
    public event EventHandler<UploadCompletedEventArgs>? UploadCompleted;
    private readonly X509Certificate2 _certificate;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _alias = "EasyShare";
    private string _fingerprint = string.Empty;
    private string _savePath = string.Empty;
    private readonly object _lock = new();
    private readonly Dictionary<string, PendingUpload> _pending = new();

    private sealed class PendingUpload
    {
        public DeviceInfo Sender { get; set; } = new();
        public List<FileEntry> Files { get; set; } = new();
        public Dictionary<string, string> Tokens { get; set; } = new();
        public HashSet<string> Received { get; set; } = new();
        public TaskCompletionSource<PrepareUploadResponse> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource UploadCts { get; } = new();
    }

    public LocalSendServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
    }

    public void Start(int port, string alias = "EasyShare", string fingerprint = "", string savePath = "")
    {
        _port = port;
        _alias = alias;
        _fingerprint = fingerprint;
        _savePath = savePath;
        _cts = new CancellationTokenSource();
        Task.Run(() => RunAsync(_cts.Token));
    }

    public string GetDefaultSavePath()
    {
        if (!string.IsNullOrEmpty(_savePath)) return _savePath;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "EasyShare");
    }

    public void AcceptUpload(string sessionId, string? savePath = null)
    {
        PendingUpload? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(sessionId, out pending)) return;
            pending.Tokens = pending.Files.ToDictionary(f => f.Id, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        }

        if (string.IsNullOrEmpty(savePath))
            savePath = GetDefaultSavePath();
        Directory.CreateDirectory(savePath);
        pending.Tcs.TrySetResult(new PrepareUploadResponse
        {
            SessionId = sessionId,
            Files = pending.Tokens,
        });
    }

    public void RejectUpload(string sessionId)
    {
        PendingUpload? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(sessionId, out pending)) return;
            _pending.Remove(sessionId);
        }
        pending.Tcs.TrySetResult(new PrepareUploadResponse { SessionId = sessionId });
    }

    public void CancelUpload(string sessionId)
    {
        PendingUpload? pending;
        lock (_lock)
        {
            if (!_pending.TryGetValue(sessionId, out pending)) return;
            _pending.Remove(sessionId);
            pending.Tcs.TrySetResult(new PrepareUploadResponse { SessionId = sessionId });
            pending.UploadCts.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = null;
            options.ListenAnyIP(_port, listen =>
            {
                listen.UseHttps(_certificate);
            });
        });

        var app = builder.Build();

        app.MapGet("/", () => WebHtml.Replace("{ALIAS}", _alias));

        app.MapPost("/upload", async (HttpContext context) =>
        {
            var boundary = GetMultipartBoundary(context.Request.ContentType);
            if (boundary is null)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var savePath = GetDefaultSavePath();
            Directory.CreateDirectory(savePath);

            var reader = new MultipartReader(boundary, context.Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync()) != null)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null || !disposition.IsFileDisposition()) continue;

                var safeName = SanitizeFileName(disposition.FileName.Value ?? "file");
                var filePath = GetUniquePath(savePath, safeName);
                await using var stream = File.Create(filePath);
                await section.Body.CopyToAsync(stream);
            }

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("OK");
        });

        app.MapGet("/api/localsend/v2/register", (HttpContext context) =>
        {
            return Results.Json(GetInfo());
        });

        app.MapPost("/api/localsend/v2/prepare-upload", async (HttpContext context) =>
        {
            PrepareUploadRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<PrepareUploadRequest>(context.Request.Body);
            }
            catch
            {
                request = null;
            }

            if (request is null || request.Files.Count == 0)
            {
                return Results.BadRequest();
            }

            var sender = new DeviceInfo
            {
                Alias = request.Info.Alias,
                Version = request.Info.Version,
                DeviceModel = request.Info.DeviceModel,
                DeviceType = request.Info.DeviceType,
                Fingerprint = request.Info.Fingerprint,
                Port = request.Info.Port,
                Protocol = request.Info.Protocol,
                Download = request.Info.Download,
                IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? "",
            };

            var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var pending = new PendingUpload
            {
                Sender = sender,
                Files = request.Files.Values.ToList(),
            };

            lock (_lock) _pending[sessionId] = pending;

            UploadRequested?.Invoke(this, new UploadRequestEventArgs
            {
                SessionId = sessionId,
                Sender = sender,
                Files = pending.Files,
                Fingerprint = sender.Fingerprint,
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            PrepareUploadResponse? response;
            try
            {
                response = await pending.Tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _pending.Remove(sessionId);
                return Results.StatusCode(StatusCodes.Status408RequestTimeout);
            }

            if (response.Files.Count == 0)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(response);
        });

        app.MapPost("/api/localsend/v2/upload", async (HttpContext context) =>
        {
            var boundary = GetMultipartBoundary(context.Request.ContentType);
            if (boundary is null)
            {
                return Results.BadRequest();
            }

            string sessionId = "", fileId = "", token = "";
            string? fileName = null;
            MultipartSection? fileSection = null;

            var reader = new MultipartReader(boundary, context.Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync()) != null)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null) continue;

                if (disposition.IsFileDisposition())
                {
                    fileSection = section;
                    fileName = disposition.FileName.Value;
                    break;
                }

                using var sr = new StreamReader(section.Body);
                var value = await sr.ReadToEndAsync();
                switch (disposition.Name.Value)
                {
                    case "sessionId": sessionId = value; break;
                    case "fileId": fileId = value; break;
                    case "token": token = value; break;
                }
            }

            if (fileSection is null || string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(token))
            {
                return Results.BadRequest();
            }

            PendingUpload? pending;
            lock (_lock)
            {
                if (!_pending.TryGetValue(sessionId, out pending) || !pending.Tokens.TryGetValue(fileId, out var expected) || expected != token)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }

            var fileEntry = pending.Files.FirstOrDefault(f => f.Id == fileId);
            var safeName = SanitizeFileName(fileName ?? fileEntry?.FileName ?? "file");
            var savePath = GetDefaultSavePath();
            Directory.CreateDirectory(savePath);
            var filePath = GetUniquePath(savePath, safeName);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long bytesReceived = 0;
            var totalBytes = fileEntry?.Size ?? 0;
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, pending.UploadCts.Token);
            bool writeSucceeded = false;
            long lastProgressTicks = 0;

            try
            {
                await using (var stream = File.Create(filePath))
                {
                    var buffer = new byte[262144];
                    while (true)
                    {
                        int read = await fileSection.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token);
                        if (read <= 0) break;
                        await stream.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token);
                        bytesReceived += read;

                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        var elapsedMs = (now - lastProgressTicks) * 1000 / (double)System.Diagnostics.Stopwatch.Frequency;
                        if (elapsedMs >= 200 || bytesReceived >= totalBytes)
                        {
                            lastProgressTicks = now;
                            UploadProgress?.Invoke(this, new UploadProgressEventArgs
                            {
                                SessionId = sessionId,
                                FileName = safeName,
                                BytesReceived = bytesReceived,
                                TotalBytes = totalBytes,
                                BytesPerSecond = bytesReceived / sw.Elapsed.TotalSeconds,
                            });
                        }
                    }
                }
                writeSucceeded = true;
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(filePath); } catch { }
                lock (_lock) _pending.Remove(sessionId);
                UploadCancelled?.Invoke(this, new UploadCancelledEventArgs { SessionId = sessionId, FileName = safeName });
                return Results.StatusCode(499);
            }
            finally
            {
                linkedCts.Dispose();
            }

            bool complete;
            lock (_lock)
            {
                pending.Received.Add(fileId);
                complete = pending.Received.SetEquals(pending.Tokens.Keys);
                if (complete) _pending.Remove(sessionId);
            }

            if (complete)
            {
                UploadCompleted?.Invoke(this, new UploadCompletedEventArgs
                {
                    SessionId = sessionId,
                    FileName = safeName,
                    Size = writeSucceeded ? bytesReceived : 0,
                    SavePath = filePath,
                });
            }

            return Results.Ok("OK");
        });

        app.MapGet("/api/localsend/v2/info", () => Results.Json(GetInfo()));

        await app.RunAsync(ct);
    }

    private object GetInfo() => new
    {
        alias = _alias,
        version = Constants.DefaultProtocolVersion,
        deviceType = "desktop",
        fingerprint = _fingerprint,
        port = _port,
        protocol = "https",
        download = true,
        announce = false,
    };

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string? GetMultipartBoundary(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        var parts = contentType.Split(';');
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                return trimmed["boundary=".Length..].Trim('"');
        }
        return null;
    }

    private static string GetUniquePath(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private const string WebHtml = """
<!DOCTYPE html>
<html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>EasyShare - Dateien senden</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#0f0f0f;color:#e5e5e5;display:flex;justify-content:center;align-items:center;min-height:100vh;padding:20px}
.card{background:#1e1e1e;border:1px solid #333;border-radius:12px;padding:32px;max-width:480px;width:100%}
h1{font-size:20px;margin-bottom:4px}
.subtitle{color:#888;font-size:13px;margin-bottom:24px}
label{display:block;font-size:13px;color:#888;margin-bottom:4px}
select,input[type=file]{width:100%;padding:10px;background:#2a2a2a;border:1px solid #444;border-radius:6px;color:#e5e5e5;font-size:14px;margin-bottom:16px}
input[type=file]::-webkit-file-upload-button{background:#6366f1;border:none;color:#fff;padding:6px 14px;border-radius:4px;cursor:pointer;margin-right:8px}
.btn{width:100%;padding:12px;background:#6366f1;border:none;color:#fff;border-radius:8px;font-size:15px;cursor:pointer;font-weight:600}
.btn:hover{background:#8b5cf6}
.btn:disabled{background:#444;cursor:not-allowed;color:#888}
.status{margin-top:16px;font-size:13px;color:#888;text-align:center;min-height:20px}
.progress{margin-top:12px;background:#2a2a2a;border-radius:6px;overflow:hidden;height:8px;display:none}
.progress-bar{height:100%;background:#6366f1;border-radius:6px;transition:width .2s}
.file-list{margin-top:12px;max-height:200px;overflow-y:auto}
.file-item{padding:8px 0;border-bottom:1px solid #2a2a2a;font-size:13px;display:flex;justify-content:space-between}
.file-item:last-child{border-bottom:none}
</style></head><body>
<div class="card">
<h1>EasyShare</h1>
<p class="subtitle">Dateien an {ALIAS} senden</p>
<input type="file" id="fileInput" multiple>
<button class="btn" id="sendBtn" onclick="sendFiles()">Senden</button>
<div class="progress" id="progress"><div class="progress-bar" id="progressBar"></div></div>
<div class="file-list" id="fileList"></div>
<div class="status" id="status"></div>
</div>
<script>
const status=document.getElementById('status');
const sendBtn=document.getElementById('sendBtn');
const progress=document.getElementById('progress');
const progressBar=document.getElementById('progressBar');

async function sendFiles(){
  const input=document.getElementById('fileInput');
  if(!input.files.length){status.textContent='Bitte Dateien auswählen';return}
  sendBtn.disabled=true;
  status.textContent='Sende...';
  progress.style.display='block';
  progressBar.style.width='0%';
  document.getElementById('fileList').innerHTML='';

  const formData=new FormData();
  for(const f of input.files){
    formData.append('files',f);
    const item=document.createElement('div');item.className='file-item';
    item.innerHTML=`<span>${f.name}</span><span>${formatSize(f.size)}</span>`;
    document.getElementById('fileList').appendChild(item);
  }

  try{
    const total=input.files.length;
    let done=0;
    for(const f of input.files){
      const body=new FormData();body.append('file',f);
      await fetch('/upload',{method:'POST',body});
      done++;
      progressBar.style.width=(done/total*100)+'%';
    }
    status.textContent=total+' Datei(en) gesendet';
    status.style.color='#4ade80';
  }catch(e){
    status.textContent='Fehler: '+e.message;
    status.style.color='#f87171';
  }
  sendBtn.disabled=false;
}

function formatSize(b){return b>=1e9?(b/1e9).toFixed(1)+' GB':b>=1e6?(b/1e6).toFixed(1)+' MB':b>=1e3?(b/1e3).toFixed(1)+' KB':b+' B'}
</script></body></html>
""";
}