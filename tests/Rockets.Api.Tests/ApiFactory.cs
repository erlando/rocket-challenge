using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rockets.Application.Storage;

namespace Rockets.Api.Tests;

/// <summary>Hosts the real service in memory, on its own temporary SQLite database.</summary>
public sealed class ApiFactory(string? databasePath = null, IMessageStore? store = null, bool resetOnStart = false)
    : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = databasePath ?? Path.Combine(Path.GetTempPath(), $"rockets-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DatabasePath", DatabasePath);
        builder.UseSetting("Storage:Synchronous", "Normal");
        if (resetOnStart)
        {
            builder.UseSetting("Storage:ResetOnStart", "true");
        }
        if (store is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(store));
        }
    }

    public async Task<HttpResponseMessage> PostAsync(string body)
    {
        using var client = CreateClient();
        return await client.PostAsync("/messages", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(string path)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static void DeleteDatabase(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            File.Delete(file);
        }
    }
}
