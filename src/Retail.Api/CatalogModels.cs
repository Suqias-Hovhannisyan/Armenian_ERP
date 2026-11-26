using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Retail.Api;

internal sealed record CatalogModel(string Key, string Label);
internal sealed record CatalogRecord(Guid Id, string Name);
internal sealed record NewCatalogModel(string? Key, string? Label);
internal sealed record NewCatalogRecord(string? Name);

internal sealed class CatalogModels(string connectionString)
{
    public static void MapEndpoints(WebApplication app, string connectionString)
    {
        var catalog = new CatalogModels(connectionString);
        var models = app.MapGroup("/api/model-editor/models").WithMetadata(new RequireModelEditorToken());
        models.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Items[typeof(RetailRole)] is not RetailRole.Admin)
                throw new RetailException(403, "An administrator is required to manage models and records.");

            return await next(context);
        });

        models.MapGet("/", (CancellationToken token) => catalog.ListModelsAsync(token));
        models.MapPost("/", async (NewCatalogModel request, CancellationToken token) =>
            Results.Json(await catalog.CreateModelAsync(request, token), statusCode: 201));
        models.MapGet("/{key}/records", (string key, CancellationToken token) => catalog.ListRecordsAsync(key, token));
        models.MapPost("/{key}/records", async (string key, NewCatalogRecord request, CancellationToken token) =>
            Results.Json(await catalog.CreateRecordAsync(key, request, token), statusCode: 201));
    }

    private async Task<List<CatalogModel>> ListModelsAsync(CancellationToken token)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("SELECT ModelKey, Label FROM dbo.CustomModels ORDER BY Label, ModelKey;", connection);
        using var reader = await command.ExecuteReaderAsync(token);
        var models = new List<CatalogModel>();
        while (await reader.ReadAsync(token))
            models.Add(new(reader.GetString(0), reader.GetString(1)));
        return models;
    }

    private async Task<CatalogModel> CreateModelAsync(NewCatalogModel request, CancellationToken token)
    {
        var model = new CatalogModel(ValidateKey(request.Key), ValidateName(request.Label));
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("INSERT INTO dbo.CustomModels(ModelKey, Label) VALUES (@key, @label);", connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 64).Value = model.Key;
        command.Parameters.Add("@label", SqlDbType.NVarChar, 200).Value = model.Label;
        await InsertAsync(command, "This model key is already in use.", token);
        return model;
    }

    private async Task<List<CatalogRecord>> ListRecordsAsync(string key, CancellationToken token)
    {
        key = ValidateKey(key);
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("""
            SELECT ModelKey FROM dbo.CustomModels WHERE ModelKey = @key;
            SELECT Id, Name FROM dbo.CustomModelRecords WHERE ModelKey = @key ORDER BY Name, Id;
            """, connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 64).Value = key;
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw RetailException.Missing("Model was not found.");

        await reader.NextResultAsync(token);
        var records = new List<CatalogRecord>();
        while (await reader.ReadAsync(token))
            records.Add(new(reader.GetGuid(0), reader.GetString(1)));
        return records;
    }

    private async Task<CatalogRecord> CreateRecordAsync(string key, NewCatalogRecord request, CancellationToken token)
    {
        key = ValidateKey(key);
        var record = new CatalogRecord(Guid.NewGuid(), ValidateName(request.Name));
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = new SqlCommand("""
            INSERT INTO dbo.CustomModelRecords(ModelKey, Id, Name)
            SELECT ModelKey, @id, @name FROM dbo.CustomModels WHERE ModelKey = @key;
            """, connection);
        command.Parameters.Add("@key", SqlDbType.VarChar, 64).Value = key;
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = record.Id;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = record.Name;
        if (await InsertAsync(command, "This record name is already in use in this model.", token) == 0)
            throw RetailException.Missing("Model was not found.");
        return record;
    }

    private static async Task<int> InsertAsync(SqlCommand command, string duplicateMessage, CancellationToken token)
    {
        try
        {
            return await command.ExecuteNonQueryAsync(token);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw RetailException.Conflict(duplicateMessage);
        }
    }

    private static string ValidateKey(string? key)
    {
        if (key is null || key == "products" || !Regex.IsMatch(key, @"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant))
            throw RetailException.Invalid("Use a new model key starting with a lowercase letter, followed by up to 63 lowercase letters, digits or underscores.");
        return key;
    }

    private static string ValidateName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            throw RetailException.Invalid("Provide a name of 1–200 characters.");
        return name;
    }
}
