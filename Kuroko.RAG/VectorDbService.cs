using Microsoft.Data.Sqlite;
using System.IO;
using System.Numerics.Tensors;

namespace Kuroko.RAG;

public class VectorDbService : IDisposable
{
    private const string ConnectionString = "Data Source=kuroko_rag.db";
    private SqliteConnection? _connection;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(ConnectionString);
        await _connection.OpenAsync();

        // 1. Create table with basic schema if it doesn't exist
        var cmd = _connection.CreateCommand();
        cmd.CommandText =
            @"
            CREATE TABLE IF NOT EXISTS KnowledgeChunks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Content TEXT NOT NULL,
                EmbeddingBlob BLOB NOT NULL
            );
            ";
        await cmd.ExecuteNonQueryAsync();

        // 2. Migration: Check if 'Source' column exists
        // This prevents crashes if using an old DB file
        var checkCmd = _connection.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(KnowledgeChunks);";

        bool sourceExists = false;
        using (var reader = await checkCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(1); // Column name is at index 1
                if (colName == "Source")
                {
                    sourceExists = true;
                    break;
                }
            }
        }

        // 3. Add column if missing
        if (!sourceExists)
        {
            var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE KnowledgeChunks ADD COLUMN Source TEXT DEFAULT 'Unknown';";
            await alterCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task InsertChunkAsync(string content, float[] embedding, string source)
    {
        if (_connection == null) return;

        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO KnowledgeChunks (Content, EmbeddingBlob, Source) VALUES (@content, @embedding, @source)";
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@embedding", bytes);
        cmd.Parameters.AddWithValue("@source", source);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<string>> SearchAsync(float[] queryEmbedding, int limit = 3)
    {
        if (_connection == null) return new List<string>();

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Content, EmbeddingBlob FROM KnowledgeChunks";

        var results = new List<(string Content, float Similarity)>();

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string content = reader.GetString(0);

            using var stream = reader.GetStream(1);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var bytes = memoryStream.ToArray();
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);

            float similarity = TensorPrimitives.CosineSimilarity(queryEmbedding, embedding);
            results.Add((content, similarity));
        }

        return results.OrderByDescending(x => x.Similarity).Take(limit).Select(x => x.Content).ToList();
    }

    public async Task<List<string>> GetSourcesAsync()
    {
        if (_connection == null) return new List<string>();
        var list = new List<string>();

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT Source FROM KnowledgeChunks";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetString(0));
            }
        }
        return list;
    }

    public async Task DeleteSourceAsync(string source)
    {
        if (_connection == null) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM KnowledgeChunks WHERE Source = @source";
        cmd.Parameters.AddWithValue("@source", source);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearDatabaseAsync()
    {
        if (_connection == null) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM KnowledgeChunks";
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        GC.SuppressFinalize(this);
    }
}