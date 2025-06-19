using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBDown.Core;

namespace BBDown;

public class BBDownApiServer
{
    private HttpListener? listener;
    private CancellationTokenSource? cts;
    private readonly List<DownloadTask> runningTasks = new();
    private readonly List<DownloadTask> finishedTasks = new();

    public void Run(string url)
    {
        if (listener != null) return;
        listener = new HttpListener();
        listener.Prefixes.Add(url.EndsWith("/") ? url : url + "/");
        listener.Start();
        cts = new CancellationTokenSource();
        Console.WriteLine($"HttpListener started at {url}");
        Task.Run(() => ListenLoop(cts.Token));
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var context = await listener!.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var resp = context.Response;
        try
        {
            // CORS
            resp.AddHeader("Access-Control-Allow-Origin", "*");
            resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 200;
                resp.Close();
                return;
            }

            var path = req.Url!.AbsolutePath.TrimEnd('/');
            if (req.HttpMethod == "GET" && path == "/get-tasks")
            {
                await WriteJson(resp, new DownloadTaskCollection(runningTasks, finishedTasks));
            }
            else if (req.HttpMethod == "GET" && path == "/get-tasks/running")
            {
                await WriteJson(resp, runningTasks);
            }
            else if (req.HttpMethod == "GET" && path == "/get-tasks/finished")
            {
                await WriteJson(resp, finishedTasks);
            }
            else if (req.HttpMethod == "GET" && path.StartsWith("/get-tasks/"))
            {
                var id = path.Substring("/get-tasks/".Length);
                var task = finishedTasks.FirstOrDefault(a => a.Aid == id) ?? runningTasks.FirstOrDefault(a => a.Aid == id);
                if (task == null)
                {
                    resp.StatusCode = 404;
                    await WriteString(resp, "Not Found");
                }
                else
                {
                    await WriteJson(resp, task);
                }
            }
            else if (req.HttpMethod == "POST" && path == "/add-task")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                ServeRequestOptions? option = null;
                try
                {
                    option = JsonSerializer.Deserialize<ServeRequestOptions>(body, SourceGenerationContext.Default.ServeRequestOptions);
                }
                catch (Exception ex)
                {
                    resp.StatusCode = 400;
                    await WriteString(resp, "输入有误: " + ex.Message);
                    return;
                }
                if (option == null)
                {
                    resp.StatusCode = 400;
                    await WriteString(resp, "输入有误");
                    return;
                }
                _ = AddDownloadTaskAsync(option)
                    .ContinueWith(async task => {
                        if (!string.IsNullOrEmpty(option.CallBackWebHook))
                        {
                            string callback = option.CallBackWebHook;
                            var client = new System.Net.Http.HttpClient();
                            var downloadTask = await task;
                            string? jsonContent = JsonSerializer.Serialize(downloadTask, AppJsonSerializerContext.Default.DownloadTask);
                            try
                            {
                                await client.PostAsync(callback, new System.Net.Http.StringContent(jsonContent, Encoding.UTF8, "application/json"));
                            }
                            catch (Exception e)
                            {
                                Logger.LogDebug("回调失败", e.Message);
                            }
                        }
                    });
                await WriteString(resp, "OK");
            }
            else if (req.HttpMethod == "GET" && path == "/remove-finished")
            {
                finishedTasks.Clear();
                await WriteString(resp, "OK");
            }
            else if (req.HttpMethod == "GET" && path == "/remove-finished/failed")
            {
                finishedTasks.RemoveAll(t => !t.IsSuccessful);
                await WriteString(resp, "OK");
            }
            else if (req.HttpMethod == "GET" && path.StartsWith("/remove-finished/"))
            {
                var id = path.Substring("/remove-finished/".Length);
                finishedTasks.RemoveAll(t => t.Aid == id);
                await WriteString(resp, "OK");
            }
            else
            {
                resp.StatusCode = 404;
                await WriteString(resp, "Not Found");
            }
        }
        catch (Exception ex)
        {
            resp.StatusCode = 500;
            await WriteString(resp, "Server Error: " + ex.Message);
        }
        finally
        {
            resp.Close();
        }
    }

    private async Task WriteJson(HttpListenerResponse resp, object obj)
    {
        resp.ContentType = "application/json; charset=utf-8";
        await using var stream = resp.OutputStream;
        await JsonSerializer.SerializeAsync(stream, obj, obj.GetType(), AppJsonSerializerContext.Default);
    }

    private async Task WriteString(HttpListenerResponse resp, string str)
    {
        resp.ContentType = "text/plain; charset=utf-8";
        var buffer = Encoding.UTF8.GetBytes(str);
        await resp.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }

    private async Task<DownloadTask> AddDownloadTaskAsync(MyOption option)
    {
        var aid = await BBDownUtil.GetAvIdAsync(option.Url);
        DownloadTask? runningTask = runningTasks.FirstOrDefault(task => task.Aid == aid);
        if (runningTask is not null)
        {
            return runningTask;
        }
        var task = new DownloadTask(aid, option.Url, DateTimeOffset.Now.ToUnixTimeSeconds());
        runningTasks.Add(task);
        try
        {
            var (encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats, input, savePathFormat, lang, aidOri, delay) = Program.SetUpWork(option);
            var (fetchedAid, vInfo, apiType) = await Program.GetVideoInfoAsync(option, aidOri, input);
            task.Title = vInfo.Title;
            task.Pic = vInfo.Pic;
            task.VideoPubTime = vInfo.PubTime;
            await Program.DownloadPagesAsync(option, vInfo, encodingPriority, dfnPriority, firstEncoding, downloadDanmaku, downloadDanmakuFormats,
                        input, savePathFormat, lang, fetchedAid, delay, apiType, task);
            task.IsSuccessful = true;
        }
        catch (Exception e)
        {
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"{aid}下载失败");
            var msg = Config.DEBUG_LOG ? e.ToString() : e.Message;
            Console.Write($"{msg}{Environment.NewLine}请尝试升级到最新版本后重试!");
            Console.ResetColor();
            Console.WriteLine();
        }
        task.TaskFinishTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        if (task.IsSuccessful)
        {
            task.Progress = 1f;
            task.DownloadSpeed = (double)(task.TotalDownloadedBytes / (task.TaskFinishTime - task.TaskCreateTime));
        }
        runningTasks.Remove(task);
        finishedTasks.Add(task);
        return task;
    }
}

public record DownloadTask(string Aid, string Url, long TaskCreateTime)
{
    [JsonInclude]
    public string? Title = null;
    [JsonInclude]
    public string? Pic = null;
    [JsonInclude]
    public long? VideoPubTime = null;
    [JsonInclude]
    public long? TaskFinishTime = null;
    [JsonInclude]
    public double Progress = 0f;
    [JsonInclude]
    public double DownloadSpeed = 0f;
    [JsonInclude]
    public double TotalDownloadedBytes = 0f;
    [JsonInclude]
    public bool IsSuccessful = false;

    [JsonInclude]
    public List<string> SavePaths = new();
};
public record DownloadTaskCollection(List<DownloadTask> Running, List<DownloadTask> Finished);

[JsonSerializable(typeof(DownloadTask))]
[JsonSerializable(typeof(List<DownloadTask>))]
[JsonSerializable(typeof(DownloadTaskCollection))]
public partial class AppJsonSerializerContext : JsonSerializerContext
{

}

[JsonSerializable(typeof(MyOption))]
[JsonSerializable(typeof(ServeRequestOptions))]
internal partial class SourceGenerationContext : JsonSerializerContext
{

}
