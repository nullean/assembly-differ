using Differ;
using Nullean.Argh;

var app = new ArghApp();
app.MapAndRootAlias<DiffCommands>();

return await app.RunAsync(args);
