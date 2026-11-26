using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Retail.Api;

internal sealed record ProductCustomField(string Key, string Label, string Type, int? Position, bool ManagerCanUse, string? TargetModelKey)
{
    public bool Placed => Position.HasValue;
}

internal sealed record ProductFieldOption(Guid Id, string Name);

internal sealed record ProductEditorState(bool CanEdit, bool CanGrant, bool ManagerCanEdit, IReadOnlyList<ProductCustomField> Fields);

internal sealed class ProductModelEditor(string connectionString)
{
    private static readonly Regex ValidKey = new(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ProductKeys = new(StringComparer.Ordinal)
    {
        "id", "sku", "name", "unitpriceamd", "unit_price_amd", "stock", "requestid", "request_id", "customfields", "custom_fields"
    };

    public async Task<ProductEditorState> GetEditorAsync(RetailRole role, CancellationToken token)
    {
        RequireEditorRole(role);
        return await ExecuteAsync(false, async (connection, transaction) =>
        {
            var managerCanEdit = await GetManagerCanEditAsync(connection, transaction, token);
            var canGrant = role == RetailRole.Admin;
            var canEdit = canGrant || managerCanEdit;
            IReadOnlyList<ProductCustomField> fields = canEdit
                ? await ReadFieldsAsync(connection, transaction, null, token)
                : [];
            return new ProductEditorState(canEdit, canGrant, managerCanEdit, fields);
        }, token);
    }

    public async Task<ProductCustomField> CreateFieldAsync(RetailRole role, CreateFieldRequest request, CancellationToken token)
    {
        RequireEditorRole(role);
        return await ExecuteAsync(true, async (connection, transaction) =>
        {
            await RequireEditorAsync(connection, transaction, role, token);
            var key = request.Key ?? "";
            var label = request.Label?.Trim();
            var type = request.Type;
            if (!IsValidKey(key) || ProductKeys.Contains(key))
            {
                throw RetailException.Invalid("Use a new key starting with a lowercase letter, followed by up to 63 lowercase letters, digits or underscores.");
            }

            if (string.IsNullOrEmpty(label) || label.Length > 200)
            {
                throw RetailException.Invalid("Provide a field label of 1–200 characters.");
            }

            if (type is not ("text" or "integer" or "boolean" or "reference"))
            {
                throw RetailException.Invalid("Field type must be text, integer, boolean or reference.");
            }

            if (type == "reference")
            {
                RequireAdmin(role);
                if (request.TargetModelKey is not string target || !IsValidKey(target))
                {
                    throw RetailException.Invalid("Select a target model for this reference field.");
                }

                await using var lookup = new SqlCommand("SELECT 1 FROM dbo.CustomModels WHERE ModelKey = @target;", connection, transaction);
                lookup.Parameters.Add("@target", SqlDbType.VarChar, 64).Value = target;
                if (await lookup.ExecuteScalarAsync(token) is null)
                {
                    throw RetailException.Invalid("The selected target model does not exist.");
                }
            }
            else if (request.TargetModelKey is not null)
            {
                throw RetailException.Invalid("Only reference fields accept a target model.");
            }

            await using var command = new SqlCommand("""
                INSERT INTO dbo.ProductCustomFields(FieldKey, Label, FieldType, TargetModelKey)
                SELECT @key, @label, @type, @target
                WHERE NOT EXISTS (SELECT 1 FROM dbo.ProductCustomFields WHERE FieldKey = @key);
                """, connection, transaction);
            AddKey(command, key);
            command.Parameters.Add("@target", SqlDbType.VarChar, 64).Value = (object?)request.TargetModelKey ?? DBNull.Value;
            command.Parameters.Add("@label", SqlDbType.NVarChar, 200).Value = label;
            command.Parameters.Add("@type", SqlDbType.VarChar, 12).Value = type;
            if (await command.ExecuteNonQueryAsync(token) == 0)
            {
                throw RetailException.Conflict("This field key is already in use.");
            }

            return new ProductCustomField(key, label, type, null, false, request.TargetModelKey);
        }, token);
    }

    public Task PlaceFieldAsync(RetailRole role, string key, int? position, CancellationToken token)
    {
        RequireEditorRole(role);
        return ExecuteAsync(true, async (connection, transaction) =>
        {
            await RequireEditorAsync(connection, transaction, role, token);
            if (position is < 0 or > 10000)
            {
                throw RetailException.Invalid("Form position must be between 0 and 10000 or null.");
            }

            await using var command = new SqlCommand("""
                UPDATE dbo.ProductCustomFields SET FormPosition = @position WHERE FieldKey = @key;
                """, connection, transaction);
            command.Parameters.Add("@position", SqlDbType.Int).Value = (object?)position ?? DBNull.Value;
            return await UpdateFieldAsync(command, key, token);
        }, token);
    }

    public Task SetManagerEditorAsync(RetailRole role, bool? canEdit, CancellationToken token)
    {
        RequireAdmin(role);
        if (canEdit is null)
        {
            throw RetailException.Invalid("Send canEdit as true or false.");
        }

        return ExecuteAsync(true, async (connection, transaction) =>
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.ProductEditorPermissions SET ManagerCanEdit = @canEdit WHERE Id = 1;
                """, connection, transaction);
            command.Parameters.Add("@canEdit", SqlDbType.Bit).Value = canEdit.Value;
            return await command.ExecuteNonQueryAsync(token);
        }, token);
    }

    public Task SetManagerFieldAsync(RetailRole role, string key, bool? canUse, CancellationToken token)
    {
        RequireAdmin(role);
        if (canUse is null)
        {
            throw RetailException.Invalid("Send canUse as true or false.");
        }

        return ExecuteAsync(true, async (connection, transaction) =>
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.ProductCustomFields SET ManagerCanUse = @canUse WHERE FieldKey = @key;
                """, connection, transaction);
            command.Parameters.Add("@canUse", SqlDbType.Bit).Value = canUse.Value;
            return await UpdateFieldAsync(command, key, token);
        }, token);
    }

    public async Task<IReadOnlyList<ProductCustomField>> GetFormAsync(RetailRole role, CancellationToken token)
    {
        if (role == RetailRole.Cashier)
        {
            return [];
        }

        return await ExecuteAsync(false, (connection, transaction) =>
            ReadFieldsAsync(connection, transaction, role, token), token);
    }

    public Task<Dictionary<string, JsonElement>> GetValuesAsync(RetailRole role, Guid id, CancellationToken token) =>
        ExecuteAsync(false, async (connection, transaction) =>
        {
            await RequireProductAsync(connection, transaction, id, token);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (role == RetailRole.Cashier)
            {
                return values;
            }

            await using var command = new SqlCommand("""
                SELECT v.FieldKey, v.ValueJson
                FROM (
                    SELECT FieldKey, ValueJson FROM dbo.ProductCustomValues WHERE ProductId = @id
                    UNION ALL
                    SELECT FieldKey, CONCAT('"', LOWER(CONVERT(char(36), RecordId)), '"')
                    FROM dbo.ProductReferenceValues WHERE ProductId = @id
                ) AS v
                INNER JOIN dbo.ProductCustomFields AS f ON f.FieldKey = v.FieldKey
                WHERE f.FormPosition IS NOT NULL AND (@isAdmin = 1 OR f.ManagerCanUse = 1);
                """, connection, transaction);
            AddProductId(command, id);
            command.Parameters.Add("@isAdmin", SqlDbType.Bit).Value = role == RetailRole.Admin;

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                values.Add(reader.GetString(0), document.RootElement.Clone());
            }

            return values;
        }, token);

    public Task SetValueAsync(RetailRole role, Guid id, string key, JsonElement value, CancellationToken token)
    {
        if (role == RetailRole.Cashier)
        {
            throw Forbidden();
        }

        return ExecuteAsync(true, async (connection, transaction) =>
        {
            var field = await GetUsableFieldAsync(connection, transaction, role, key, token);
            await RequireProductAsync(connection, transaction, id, token);
            if (field.TargetModelKey is string target)
            {
                return await SetReferenceAsync(connection, transaction, id, key, target, value, token);
            }

            var json = ValidateValue(field.Type, value);
            await using var command = new SqlCommand("""
                IF @json IS NULL
                    DELETE FROM dbo.ProductCustomValues WHERE ProductId = @id AND FieldKey = @key;
                ELSE
                BEGIN
                    UPDATE dbo.ProductCustomValues SET ValueJson = @json WHERE ProductId = @id AND FieldKey = @key;
                    IF @@ROWCOUNT = 0
                        INSERT INTO dbo.ProductCustomValues(ProductId, FieldKey, ValueJson) VALUES (@id, @key, @json);
                END;
                """, connection, transaction);
            AddProductId(command, id);
            AddKey(command, key);
            command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = (object?)json ?? DBNull.Value;
            return await command.ExecuteNonQueryAsync(token);
        }, token);
    }

    public Task<IReadOnlyList<ProductFieldOption>> GetOptionsAsync(RetailRole role, string key, CancellationToken token)
    {
        RequireEditorRole(role);
        return ExecuteAsync<IReadOnlyList<ProductFieldOption>>(false, async (connection, transaction) =>
        {
            var field = await GetUsableFieldAsync(connection, transaction, role, key, token);
            if (field.TargetModelKey is null)
            {
                throw RetailException.Invalid("This field is not a reference.");
            }

            await using var command = new SqlCommand("""
                SELECT Id, Name FROM dbo.CustomModelRecords
                WHERE ModelKey = @target ORDER BY Name, Id;
                """, connection, transaction);
            command.Parameters.Add("@target", SqlDbType.VarChar, 64).Value = field.TargetModelKey;
            var options = new List<ProductFieldOption>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                options.Add(new ProductFieldOption(reader.GetGuid(0), reader.GetString(1)));
            }
            return options;
        }, token);
    }

    private static async Task<(string Type, string? TargetModelKey)> GetUsableFieldAsync(SqlConnection connection,
        SqlTransaction transaction, RetailRole role, string key, CancellationToken token)
    {
        if (IsValidKey(key))
        {
            await using var command = new SqlCommand("""
                SELECT FieldType, TargetModelKey FROM dbo.ProductCustomFields
                WHERE FieldKey = @key AND FormPosition IS NOT NULL AND (@isAdmin = 1 OR ManagerCanUse = 1);
                """, connection, transaction);
            AddKey(command, key);
            command.Parameters.Add("@isAdmin", SqlDbType.Bit).Value = role == RetailRole.Admin;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            }
        }

        throw role == RetailRole.Manager ? Forbidden() : RetailException.Missing("Placed custom field was not found.");
    }

    private static async Task<int> SetReferenceAsync(SqlConnection connection, SqlTransaction transaction,
        Guid productId, string key, string target, JsonElement value, CancellationToken token)
    {
        Guid? recordId = null;
        if (value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var selected))
            {
                throw RetailException.Invalid("Select a valid record ID or null to clear.");
            }
            recordId = selected;
        }

        await using var command = new SqlCommand("""
            IF @record IS NOT NULL AND NOT EXISTS (
                SELECT 1 FROM dbo.CustomModelRecords WITH (HOLDLOCK) WHERE ModelKey = @target AND Id = @record
            ) SELECT 0;
            ELSE
            BEGIN
                DELETE FROM dbo.ProductReferenceValues WHERE ProductId = @id AND FieldKey = @key;
                IF @record IS NOT NULL
                    INSERT INTO dbo.ProductReferenceValues(ProductId, FieldKey, ModelKey, RecordId)
                    VALUES (@id, @key, @target, @record);
                SELECT 1;
            END;
            """, connection, transaction);
        AddKey(command, key);
        AddProductId(command, productId);
        command.Parameters.Add("@target", SqlDbType.VarChar, 64).Value = target;
        command.Parameters.Add("@record", SqlDbType.UniqueIdentifier).Value = (object?)recordId ?? DBNull.Value;
        if (await command.ExecuteScalarAsync(token) is not 1)
        {
            throw RetailException.Invalid("Select an existing record from the target model.");
        }
        return 1;
    }

    private async Task<T> ExecuteAsync<T>(bool write, Func<SqlConnection, SqlTransaction, Task<T>> execute, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // Keep reads and mutations consistent when placement or a role grant changes concurrently.
        await using var command = new SqlCommand("""
            DECLARE @status int;
            EXEC @status = sys.sp_getapplock
                @Resource = N'ArmeniaRetailERP.ProductModelEditor.v1',
                @LockMode = @mode,
                @LockOwner = N'Transaction',
                @LockTimeout = 10000;
            SELECT @status;
            """, connection, transaction);
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 9).Value = write ? "Exclusive" : "Shared";
        if (await command.ExecuteScalarAsync(token) is not int status || status < 0)
        {
            throw new RetailException(503, "The product editor is busy. Retry the request.");
        }

        var result = await execute(connection, transaction);
        await transaction.CommitAsync(token);
        return result;
    }

    private static async Task<IReadOnlyList<ProductCustomField>> ReadFieldsAsync(SqlConnection connection,
        SqlTransaction transaction, RetailRole? runtimeRole, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT FieldKey, Label, FieldType, FormPosition, ManagerCanUse, TargetModelKey
            FROM dbo.ProductCustomFields
            WHERE @editor = 1 OR (FormPosition IS NOT NULL AND (@isAdmin = 1 OR ManagerCanUse = 1))
            ORDER BY CASE WHEN FormPosition IS NULL THEN 1 ELSE 0 END, FormPosition, FieldKey;
            """, connection, transaction);
        command.Parameters.Add("@editor", SqlDbType.Bit).Value = runtimeRole is null;
        command.Parameters.Add("@isAdmin", SqlDbType.Bit).Value = runtimeRole == RetailRole.Admin;

        var fields = new List<ProductCustomField>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            fields.Add(new ProductCustomField(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return fields;
    }

    private static async Task<bool> GetManagerCanEditAsync(SqlConnection connection, SqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ManagerCanEdit FROM dbo.ProductEditorPermissions WHERE Id = 1;
            """, connection, transaction);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task RequireEditorAsync(SqlConnection connection, SqlTransaction transaction, RetailRole role,
        CancellationToken token)
    {
        if (role != RetailRole.Admin && !await GetManagerCanEditAsync(connection, transaction, token))
        {
            throw Forbidden();
        }
    }

    private static void RequireEditorRole(RetailRole role)
    {
        if (role == RetailRole.Cashier)
        {
            throw Forbidden();
        }
    }

    private static void RequireAdmin(RetailRole role)
    {
        if (role != RetailRole.Admin)
        {
            throw Forbidden();
        }
    }

    private static RetailException Forbidden() => new(403, "Your role does not have permission for this operation.");

    private static async Task<int> UpdateFieldAsync(SqlCommand command, string key, CancellationToken token)
    {
        if (!IsValidKey(key))
        {
            throw RetailException.Missing("Custom field was not found.");
        }

        AddKey(command, key);
        if (await command.ExecuteNonQueryAsync(token) == 0)
        {
            throw RetailException.Missing("Custom field was not found.");
        }

        return 1;
    }

    private static async Task RequireProductAsync(SqlConnection connection, SqlTransaction transaction, Guid id,
        CancellationToken token)
    {
        await using var command = new SqlCommand("SELECT 1 FROM dbo.Products WHERE Id = @id;", connection, transaction);
        AddProductId(command, id);
        if (await command.ExecuteScalarAsync(token) is null)
        {
            throw RetailException.Missing("Product was not found.");
        }
    }

    private static string? ValidateValue(string type, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String when type == "text" && value.GetString()!.Length <= 2000 => value.GetRawText(),
        JsonValueKind.Number when type == "integer" && value.TryGetInt64(out _) => value.GetRawText(),
        JsonValueKind.True or JsonValueKind.False when type == "boolean" => value.GetRawText(),
        _ => throw RetailException.Invalid("Send a value that matches the field type or null to clear it. Text is limited to 2000 characters.")
    };

    private static bool IsValidKey(string key) => ValidKey.IsMatch(key);

    private static void AddKey(SqlCommand command, string key) =>
        command.Parameters.Add("@key", SqlDbType.VarChar, 64).Value = key;

    private static void AddProductId(SqlCommand command, Guid id) =>
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
}
