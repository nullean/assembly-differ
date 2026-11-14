using Differ;
using Nullean.Argh;

var app = new ArghApp();
app.Map<ApiCommands>();
app.MapAndRootAlias<DiffCommands>();

return await app.RunAsync(args);
