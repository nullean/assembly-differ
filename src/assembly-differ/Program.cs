using Differ;
using Nullean.Argh;

// Before it had commands, the tool took two provider specs at the root, such as "nuget|Proc|0.13.0": route those to
// diff, which still takes them, so the builds that call it that way keep working.
if (args.Length > 0 && args[0].Contains('|'))
	args = ["diff", .. args];

var app = new ArghApp();
app.Map<ApiCommands>();
app.Map<Differ.PullRequests.PullRequestCommand>();
app.Map<DiffCommands>();
app.MapAndRootAlias<Differ.Projects.ProjectsCommand>();

return await app.RunAsync(args);
