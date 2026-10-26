using System.Text.Json;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace DLSSGManager.Core.Persistence;

public sealed class LibraryStore : ILibraryStore
{
    private readonly string _database;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public LibraryStore(AppDataPaths paths)
    {
        paths.Ensure();
        _database = paths.Database;
        _settingsPath = paths.SettingsFile;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS games (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS installations (
                game_id TEXT PRIMARY KEY,
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS operation_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                utc TEXT NOT NULL,
                game_id TEXT,
                action TEXT NOT NULL,
                result TEXT NOT NULL,
                detail TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<GameRecord> LoadGames()
    {
        var games = new List<GameRecord>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM games";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var game = JsonSerializer.Deserialize<GameRecord>(reader.GetString(0), _json);
            if (game == null)
                continue;
            game.Installation = LoadInstallation(game.Id);
            games.Add(game);
        }
        return games;
    }

    public void SaveGames(IEnumerable<GameRecord> games)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM games";
            delete.ExecuteNonQuery();
        }
        foreach (var game in games)
        {
            var copy = JsonSerializer.Deserialize<GameRecord>(JsonSerializer.Serialize(game, _json), _json)!;
            copy.Installation = null;
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO games (id, json) VALUES ($id, $json)";
            insert.Parameters.AddWithValue("$id", game.Id);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(copy, _json));
            insert.ExecuteNonQuery();
            if (game.Installation != null)
                SaveInstallation(game.Installation, connection, tx);
        }
        tx.Commit();
    }

    public void SaveInstallation(InstallationRecord record)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        SaveInstallation(record, connection, tx);
        tx.Commit();
    }

    public void DeleteInstallation(string gameId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM installations WHERE game_id = $id";
        command.Parameters.AddWithValue("$id", gameId);
        command.ExecuteNonQuery();
    }

    public InstallationRecord? LoadInstallation(string gameId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM installations WHERE game_id = $id";
        command.Parameters.AddWithValue("$id", gameId);
        var json = command.ExecuteScalar() as string;
        return json == null ? null : JsonSerializer.Deserialize<InstallationRecord>(json, _json);
    }

    public void AppendLog(string? gameId, string action, string result, string detail)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO operation_log (utc, game_id, action, result, detail) VALUES ($utc, $game, $action, $result, $detail)";
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$game", (object?)gameId ?? DBNull.Value);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<OperationLogEntry> RecentLog(string? gameId, int take)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = gameId == null
            ? "SELECT id, utc, game_id, action, result, detail FROM operation_log ORDER BY id DESC LIMIT $take"
            : "SELECT id, utc, game_id, action, result, detail FROM operation_log WHERE game_id = $game ORDER BY id DESC LIMIT $take";
        command.Parameters.AddWithValue("$take", take);
        if (gameId != null)
            command.Parameters.AddWithValue("$game", gameId);
        var list = new List<OperationLogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OperationLogEntry
            {
                Id = reader.GetInt64(0),
                Utc = DateTimeOffset.Parse(reader.GetString(1)),
                GameId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Action = reader.GetString(3),
                Result = reader.GetString(4),
                Detail = reader.GetString(5)
            });
        }
        return list;
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), _json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings) => File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, _json));

    private void SaveInstallation(InstallationRecord record, SqliteConnection connection, SqliteTransaction tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO installations (game_id, json) VALUES ($id, $json)
            ON CONFLICT(game_id) DO UPDATE SET json = excluded.json
            """;
        command.Parameters.AddWithValue("$id", record.GameId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, _json));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database }.ToString());
        connection.Open();
        return connection;
    }
}
