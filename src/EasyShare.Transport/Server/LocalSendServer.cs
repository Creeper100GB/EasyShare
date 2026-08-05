using System.Net;
using System.Security.Cryptography.X509Certificates;
using EasyShare.Core.Models;

namespace EasyShare.Transport.Server;

public class UploadRequestEventArgs : EventArgs
{
    public string SessionId { get; set; } = string.Empty;
    public DeviceInfo Sender { get; set; } = new();
    public List<FileEntry> Files { get; set; } = new();
    public string Fingerprint { get; set; } = string.Empty;
}

public class LocalSendServer
{
    public event EventHandler<UploadRequestEventArgs>? UploadRequested;
    private readonly X509Certificate2 _certificate;
    private CancellationTokenSource? _cts;
    private int _port;
    private string _alias = "EasyShare";
    private string _fingerprint = string.Empty;
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

    public LocalSendServer(X509Certificate2 certificate)
    {
        _certificate = certificate;
    }

    public void Start(int port, string alias = "EasyShare", string fingerprint = "")
    {
        _port = port;
        _alias = alias;
        _fingerprint = fingerprint;
        _cts = new CancellationTokenSource();
        Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port);
            options.ListenAnyIP(_port + 1, configure => { });
        });

        var app = builder.Build();

        app.MapGet("/", () => WebHtml.Replace("{ALIAS}", _alias));

        app.MapPost("/upload", async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var savePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "EasyShare");

            System.IO.Directory.CreateDirectory(savePath);

            foreach (var file in context.Request.Form.Files)
            {
                var filePath = System.IO.Path.Combine(savePath, file.FileName);
                using var stream = System.IO.File.Create(filePath);
                await file.CopyToAsync(stream);
            }

            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("OK");
        });

        app.MapGet("/api/localsend/v2/register", (HttpContext context) =>
        {
            context.Response.StatusCode = 200;
            return Results.Json(new { });
        });

        app.MapPost("/api/localsend/v2/prepare-upload", (HttpContext context) =>
        {
            context.Response.StatusCode = 200;
            return Results.Json(new { sessionId = Guid.NewGuid().ToString("N"), files = new { } });
        });

        app.MapPost("/api/localsend/v2/upload", (HttpContext context) =>
        {
            context.Response.StatusCode = 200;
            return Results.Ok();
        });

        app.MapGet("/api/localsend/v2/info", () =>
        {
            return Results.Json(new
            {
                alias = _alias,
                version = "2.0",
                deviceType = "desktop",
                fingerprint = _fingerprint,
                port = _port,
                protocol = "https",
                download = true,
            });
        });

        await app.RunAsync(ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }
}
