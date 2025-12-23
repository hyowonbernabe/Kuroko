using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Kuroko.Core;

public class AiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _modelId;

    // Default fallback if settings are empty
    private const string DefaultModelFallback = "google/gemma-3-27b-it:free";

    public AiService(string apiKey, string? modelId = null)
    {
        _modelId = !string.IsNullOrWhiteSpace(modelId) ? modelId : DefaultModelFallback;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/Kuroko-AI");
        _httpClient.DefaultRequestHeaders.Add("X-Title", "Kuroko");
    }

    public async Task<string> GetInterviewAssistanceAsync(string transcript, string contextData = "")
    {
        string systemPrompt = """
            You are Kuroko, an invisible interview assistant. 
            Your goal is to help the user answer interview questions naturally.
            
            RULES:
            1. Output strictly 3-5 concise bullet points.
            2. Do not write full scripts or long paragraphs.
            3. Focus on metrics, keywords, and specific technical details.
            4. If provided, use the 'RAG Context' to ground your answers in the user's resume.
            5. Keep the tone professional, confident, and conversational.
            """;

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(contextData))
        {
            sb.AppendLine("--- RELEVANT RESUME CONTEXT ---");
            sb.AppendLine(contextData);
            sb.AppendLine("-------------------------------");
        }

        sb.AppendLine("--- LIVE TRANSCRIPT (INTERVIEWER & USER) ---");
        sb.AppendLine(transcript);
        sb.AppendLine("--------------------------------------------");
        sb.AppendLine("Based on the transcript above, provide bullet points to answer the interviewer's last question or continue the topic.");

        string userPrompt = sb.ToString();

        try
        {
            var requestBody = new
            {
                model = _modelId,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = 0.7,
                max_tokens = 250
            };

            var jsonContent = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("chat/completions", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return $"AI Error: {response.StatusCode} - {errorContent}";
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);

            var root = doc.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var contentElement))
                    {
                        return contentElement.GetString() ?? "No content returned.";
                    }
                }
            }

            return "No response generated.";
        }
        catch (Exception ex)
        {
            return $"AI Error: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }
}