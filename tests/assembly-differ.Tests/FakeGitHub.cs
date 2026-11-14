using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Differ.Tests;

/// <summary>
/// The part of the GitHub REST API the pull-request command calls, for one pull request, served on localhost: comments
/// and labels live in memory, and every request is recorded.
/// </summary>
public sealed partial class FakeGitHub : IDisposable
{
	private HttpListener _listener;
	private readonly Task _serving;
	private long _nextId = 100;

	public FakeGitHub()
	{
		// a free port can be taken again before the listener starts, by a test running alongside: try another
		for (var attempt = 1; ; attempt++)
		{
			Url = $"http://127.0.0.1:{FreePort()}/";
			_listener = new HttpListener();
			_listener.Prefixes.Add(Url);
			try
			{
				_listener.Start();
				break;
			}
			catch (HttpListenerException) when (attempt < 5)
			{
				try
				{
					_listener.Close();
				}
				catch (HttpListenerException) { }
			}
		}
		_serving = Task.Run(Serve);
	}

	public string Url { get; private set; }

	public List<(long Id, string Body)> Comments { get; } = [];

	public List<string> Labels { get; } = [];

	public List<string> Requests { get; } = [];

	/// <summary>Answer every write with this status, the way GitHub answers a read-only token.</summary>
	public HttpStatusCode? RefuseWrites { get; set; }

	private async Task Serve()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception) when (!_listener.IsListening)
			{
				return;
			}
			var request = context.Request;
			var body = await new StreamReader(request.InputStream).ReadToEndAsync();
			var path = request.Url!.AbsolutePath;
			lock (Requests)
				Requests.Add($"{request.HttpMethod} {path}");
			var (status, response) = Handle(request.HttpMethod, path, request.Url.Query, body);
			context.Response.StatusCode = (int)status;
			var bytes = Encoding.UTF8.GetBytes(response);
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}
	}

	private (HttpStatusCode, string) Handle(string method, string path, string query, string body)
	{
		if (method != "GET" && RefuseWrites is { } refused)
			return (refused, """{"message":"Resource not accessible by integration"}""");

		if (method == "GET" && CommentsPath().IsMatch(path))
			return (HttpStatusCode.OK, query.Contains("page=1") ? JsonSerializer.Serialize(Comments.Select(c => new { id = c.Id, body = c.Body })) : "[]");
		if (method == "POST" && CommentsPath().IsMatch(path))
		{
			Comments.Add((_nextId++, Property(body, "body")));
			return (HttpStatusCode.Created, "{}");
		}
		if (method == "PATCH" && CommentPath().Match(path) is { Success: true } comment)
		{
			var id = long.Parse(comment.Groups[1].Value);
			var index = Comments.FindIndex(c => c.Id == id);
			Comments[index] = (id, Property(body, "body"));
			return (HttpStatusCode.OK, "{}");
		}
		if (method == "GET" && LabelsPath().IsMatch(path))
			return (HttpStatusCode.OK, JsonSerializer.Serialize(Labels.Select(l => new { name = l })));
		if (method == "POST" && LabelsPath().IsMatch(path))
		{
			using var document = JsonDocument.Parse(body);
			Labels.AddRange(document.RootElement.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!));
			return (HttpStatusCode.OK, "[]");
		}
		if (method == "DELETE" && LabelPath().Match(path) is { Success: true } label)
			return Labels.Remove(Uri.UnescapeDataString(label.Groups[1].Value)) ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.NotFound, "{}");
		return (HttpStatusCode.NotFound, "{}");
	}

	private static string Property(string json, string name)
	{
		using var document = JsonDocument.Parse(json);
		return document.RootElement.GetProperty(name).GetString()!;
	}

	private static int FreePort()
	{
		using var socket = new TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		return ((IPEndPoint)socket.LocalEndpoint).Port;
	}

	[GeneratedRegex("^/repos/owner/repo/issues/7/comments$")]
	private static partial Regex CommentsPath();

	[GeneratedRegex("^/repos/owner/repo/issues/comments/(\\d+)$")]
	private static partial Regex CommentPath();

	[GeneratedRegex("^/repos/owner/repo/issues/7/labels$")]
	private static partial Regex LabelsPath();

	[GeneratedRegex("^/repos/owner/repo/issues/7/labels/(.+)$")]
	private static partial Regex LabelPath();

	public void Dispose()
	{
		// Close stops the listener; calling Stop first makes Close bind the port again, which can be taken by then
		try
		{
			_listener.Close();
		}
		catch (HttpListenerException) { }
		try
		{
			_serving.Wait(TimeSpan.FromSeconds(5));
		}
		catch (AggregateException) { }
	}
}
