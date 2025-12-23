using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Kuroko.Core;

public class EmbeddingService : IDisposable
{
    private readonly HttpClient _httpClient;
    private const string EmbeddingModelId = "text-embedding-3-small";

    public EmbeddingService(string apiKey)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Set headers once
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // OpenRouter sometimes requires this to route correctly
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Kuroko-AI");
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text)
    {
        try
        {
            // Construct the raw JSON payload manually
            var requestBody = new
            {
                model = EmbeddingModelId,
                input = text
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Send raw POST request
            var response = await _httpClient.PostAsync("embeddings", content);
            response.EnsureSuccessStatusCode();

            // Parse response manually to avoid SDK strictness issues
            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);

            var root = doc.RootElement;

            // OpenRouter/OpenAI structure: { "data": [ { "embedding": [ ... ] } ] }
            if (root.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
            {
                var firstItem = dataArray[0];
                if (firstItem.TryGetProperty("embedding", out var embeddingJson))
                {
                    int length = embeddingJson.GetArrayLength();
                    float[] embedding = new float[length];

                    for (int i = 0; i < length; i++)
                    {
                        embedding[i] = embeddingJson[i].GetSingle();
                    }

                    return embedding;
                }
            }
        }
        catch (Exception)
        {
            // In production, log the error (ex.Message)
            // For now, return empty to indicate failure without crashing app
        }

        return Array.Empty<float>();
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}