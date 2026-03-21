using Atlantis;

var server = new WebServer("http://localhost:5000/");
Console.WriteLine("Listening on http://localhost:5000/");
await server.RunAsync(CancellationToken.None);
