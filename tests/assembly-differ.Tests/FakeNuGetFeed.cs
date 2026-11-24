using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Differ.Tests;

/// <summary>
/// A NuGet v3 feed on localhost for one package: the service index, the flat container (which lists every version,
/// unlisted ones too, as nuget.org does), the registration (which says which versions are listed) and the .nupkg files.
/// </summary>
public sealed class FakeNuGetFeed : IDisposable
{
	private readonly string _id;
	private readonly HttpListener _listener = new();
	private readonly Task _serving;
	private readonly List<(string Version, bool Listed, string Path)> _versions = [];

	public FakeNuGetFeed(string id)
	{
		_id = id;
		// a free port can be taken again before the listener starts, by a test running alongside: try another
		for (var attempt = 1; ; attempt++)
		{
			Url = $"http://127.0.0.1:{FreePort()}/";
			_listener.Prefixes.Clear();
			_listener.Prefixes.Add(Url);
			try
			{
				_listener.Start();
				break;
			}
			catch (HttpListenerException) when (attempt < 5) { }
		}
		_serving = Task.Run(Serve);
	}

	public string Url { get; }

	public string ServiceIndexUrl => Url + "index.json";

	public void Add(string version, bool listed, string nupkg) => _versions.Add((version, listed, nupkg));

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
			var path = context.Request.Url!.AbsolutePath;
			var lower = _id.ToLowerInvariant();
			byte[]? body = null;
			if (path == "/index.json")
				body = Json(new
				{
					version = "3.0.0",
					resources = new object[]
					{
						new Dictionary<string, string> { ["@id"] = Url + "flat/", ["@type"] = "PackageBaseAddress/3.0.0" },
						new Dictionary<string, string> { ["@id"] = Url + "registration/", ["@type"] = "RegistrationsBaseUrl/3.6.0" },
					},
				});
			else if (path == $"/flat/{lower}/index.json")
				body = Json(new { versions = _versions.Select(v => v.Version.ToLowerInvariant()) });
			else if (path == $"/registration/{lower}/index.json")
				body = Json(new
				{
					count = 1,
					items = new[]
					{
						new Dictionary<string, object>
						{
							["@id"] = Url + $"registration/{lower}/index.json#page",
							["count"] = _versions.Count,
							["lower"] = _versions.Min(v => v.Version),
							["upper"] = _versions.Max(v => v.Version),
							["items"] = _versions.Select(
								v => new Dictionary<string, object>
								{
									["@id"] = Url + $"registration/{lower}/{v.Version.ToLowerInvariant()}.json",
									["packageContent"] = Url + $"flat/{lower}/{v.Version.ToLowerInvariant()}/{lower}.{v.Version.ToLowerInvariant()}.nupkg",
									["catalogEntry"] = new Dictionary<string, object>
									{
										["@id"] = Url + $"catalog/{lower}.{v.Version.ToLowerInvariant()}.json",
										["id"] = _id,
										["version"] = v.Version,
										["listed"] = v.Listed,
									},
								}
							),
						},
					},
				});
			else
				foreach (var (version, _, nupkg) in _versions)
					if (path == $"/flat/{lower}/{version.ToLowerInvariant()}/{lower}.{version.ToLowerInvariant()}.nupkg")
						body = await File.ReadAllBytesAsync(nupkg);

			context.Response.StatusCode = body is null ? 404 : 200;
			if (body is not null)
				await context.Response.OutputStream.WriteAsync(body);
			context.Response.Close();
		}
	}

	private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	public void Dispose()
	{
		_listener.Close();
		_serving.GetAwaiter().GetResult();
	}
}
