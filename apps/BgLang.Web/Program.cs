using BgLang.Web;

await using var app = WebHostFactory.Create(args);
await app.RunAsync();
