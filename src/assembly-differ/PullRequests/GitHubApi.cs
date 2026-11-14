using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Differ.PullRequests;

/// <summary>A GitHub API call that did not succeed.</summary>
public sealed class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
	public HttpStatusCode Status { get; } = status;
}

/// <summary>The few GitHub REST calls the pull request command makes: comments and labels on one pull request.</summary>
public sealed class GitHubApi(Uri baseAddress, string token, string repository, int pullRequest) : IDisposable
{
	private readonly HttpClient _http = Create(baseAddress, token);

	private static HttpClient Create(Uri baseAddress, string token)
	{
		var http = new HttpClient { BaseAddress = new Uri(baseAddress.ToString().TrimEnd('/') + "/") };
		http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("assembly-differ", null));
		http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
		return http;
	}

	/// <summary>The id and body of the comment that holds <paramref name="marker"/>, if there is one.</summary>
	public async Task<(long Id, string Body)?> FindComment(string marker)
	{
		for (var page = 1; ; page++)
		{
			using var document = await Get($"repos/{repository}/issues/{pullRequest}/comments?per_page=100&page={page}");
			if (document.RootElement.GetArrayLength() == 0)
				return null;
			foreach (var comment in document.RootElement.EnumerateArray())
			{
				var body = comment.GetProperty("body").GetString() ?? "";
				if (body.Contains(marker, StringComparison.Ordinal))
					return (comment.GetProperty("id").GetInt64(), body);
			}
		}
	}

	public Task CreateComment(string body) => Send(HttpMethod.Post, $"repos/{repository}/issues/{pullRequest}/comments", Body("body", body));

	public Task UpdateComment(long id, string body) => Send(HttpMethod.Patch, $"repos/{repository}/issues/comments/{id}", Body("body", body));

	public async Task<IReadOnlyCollection<string>> Labels()
	{
		using var document = await Get($"repos/{repository}/issues/{pullRequest}/labels?per_page=100");
		var labels = new List<string>();
		foreach (var label in document.RootElement.EnumerateArray())
			labels.Add(label.GetProperty("name").GetString()!);
		return labels;
	}

	public Task AddLabel(string label) => Send(HttpMethod.Post, $"repos/{repository}/issues/{pullRequest}/labels", Body("labels", [label]));

	public async Task RemoveLabel(string label)
	{
		try
		{
			await Send(HttpMethod.Delete, $"repos/{repository}/issues/{pullRequest}/labels/{Uri.EscapeDataString(label)}", null);
		}
		catch (GitHubApiException e) when (e.Status == HttpStatusCode.NotFound)
		{
			// someone already removed it
		}
	}

	private async Task<JsonDocument> Get(string path)
	{
		using var response = await _http.GetAsync(path);
		await EnsureSuccess(response, "GET", path);
		return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
	}

	private async Task Send(HttpMethod method, string path, HttpContent? content)
	{
		using var request = new HttpRequestMessage(method, path) { Content = content };
		using var response = await _http.SendAsync(request);
		await EnsureSuccess(response, method.Method, path);
	}

	private static async Task EnsureSuccess(HttpResponseMessage response, string method, string path)
	{
		if (response.IsSuccessStatusCode)
			return;
		var text = await response.Content.ReadAsStringAsync();
		throw new GitHubApiException(response.StatusCode, $"{method} {path} returned {(int)response.StatusCode}: {text}");
	}

	private static StringContent Body(string property, string value) => Json(w => w.WriteString(property, value));

	private static StringContent Body(string property, string[] values) =>
		Json(w =>
		{
			w.WriteStartArray(property);
			foreach (var value in values)
				w.WriteStringValue(value);
			w.WriteEndArray();
		});

	private static StringContent Json(Action<Utf8JsonWriter> write)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			write(writer);
			writer.WriteEndObject();
		}
		return new StringContent(Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8, "application/json");
	}

	public void Dispose() => _http.Dispose();
}
