var builder = DistributedApplication.CreateBuilder(args);

var databaseDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "src", "Shelf.Api"));
var sqlite = builder.AddSqlite("sqlite", databaseDirectory, "shelf.db");

builder.AddProject<Projects.Shelf_Api>("api")
    .WithReference(sqlite)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
