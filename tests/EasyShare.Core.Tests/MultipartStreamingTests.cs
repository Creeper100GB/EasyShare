using System.IO;
using System.Text;
using EasyShare.Core.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace EasyShare.Core.Tests;

public class MultipartStreamingTests
{
    [Fact]
    public async Task MultipartReader_BreakOnFileSection_PreservesFileBody()
    {
        var fileContent = new byte[1024];
        Random.Shared.NextBytes(fileContent);

        var boundary = "testboundary123";
        var body = BuildMultipartBody(boundary, "session123", "file1", "tok456", fileContent, "test.bin");

        using var ms = new MemoryStream(body);
        var reader = new MultipartReader(boundary, ms);

        string sessionId = "", fileId = "", token = "";
        string? fileName = null;
        MultipartSection? fileSection = null;

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

        Assert.NotNull(fileSection);
        Assert.Equal("session123", sessionId);
        Assert.Equal("file1", fileId);
        Assert.Equal("tok456", token);
        Assert.Equal("test.bin", fileName);

        using var outStream = new MemoryStream();
        await fileSection!.Body.CopyToAsync(outStream);
        var received = outStream.ToArray();

        Assert.Equal(fileContent.Length, received.Length);
        Assert.Equal(fileContent, received);
    }

    [Fact]
    public async Task MultipartReader_ContinueOnFileSection_DrainsBody()
    {
        var fileContent = new byte[1024];
        Random.Shared.NextBytes(fileContent);

        var boundary = "testboundary456";
        var body = BuildMultipartBody(boundary, "s1", "f1", "t1", fileContent, "data.dat");

        using var ms = new MemoryStream(body);
        var reader = new MultipartReader(boundary, ms);

        MultipartSection? fileSection = null;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync()) != null)
        {
            var disposition = section.GetContentDispositionHeader();
            if (disposition is null) continue;

            if (disposition.IsFileDisposition())
            {
                fileSection = section;
                continue;
            }
        }

        Assert.NotNull(fileSection);
        using var outStream = new MemoryStream();
        await fileSection!.Body.CopyToAsync(outStream);
        var received = outStream.ToArray();

        Assert.Equal(0, received.Length);
    }

    private static byte[] BuildMultipartBody(string boundary, string sessionId, string fileId, string token, byte[] fileData, string fileName)
    {
        using var ms = new MemoryStream();
        var nl = Encoding.UTF8.GetBytes("\r\n");

        void WriteField(string name, string value)
        {
            ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\n"));
            ms.Write(Encoding.UTF8.GetBytes($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n"));
            ms.Write(Encoding.UTF8.GetBytes(value));
            ms.Write(nl);
        }

        WriteField("sessionId", sessionId);
        WriteField("fileId", fileId);
        WriteField("token", token);

        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\n"));
        ms.Write(Encoding.UTF8.GetBytes($"Content-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\n"));
        ms.Write(Encoding.UTF8.GetBytes("Content-Type: application/octet-stream\r\n\r\n"));
        ms.Write(fileData);
        ms.Write(nl);
        ms.Write(Encoding.UTF8.GetBytes($"--{boundary}--\r\n"));

        return ms.ToArray();
    }
}
