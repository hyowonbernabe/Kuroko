using Microsoft.Data.Sqlite;
using System.IO;
using System.Numerics.Tensors;
using System.Text.Json;

namespace Kuroko.RAG;

public class VectorDbService : IDisposable
{
    private const string ConnectionString = "Data Source=kuroko_rag.db";
    private SqliteConnection? _connection;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection(ConnectionString);
        await _connection.OpenAsync();

        // simple table: ID, Content (Text), Embedding (JSON/Blob)
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
    }

    public async Task InsertChunkAsync(string content, float[] embedding)
    {
        if (_connection == null) return;

        // Convert float[] to byte[] for efficient storage
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO KnowledgeChunks (Content, EmbeddingBlob) VALUES (@content, @embedding)";
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@embedding", bytes);

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

            // Read Blob back to float[]
            using var stream = reader.GetStream(1);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var bytes = memoryStream.ToArray();
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);

            // Calculate Cosine Similarity using SIMD-accelerated System.Numerics.Tensors
            // This is extremely fast even for 1000s of chunks
            float similarity = TensorPrimitives.CosineSimilarity(queryEmbedding, embedding);

            results.Add((content, similarity));
        }

        // Return top K chunks by similarity
        return results
            .OrderByDescending(x => x.Similarity)
            .Take(limit)
            .Select(x => x.Content)
            .ToList();
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